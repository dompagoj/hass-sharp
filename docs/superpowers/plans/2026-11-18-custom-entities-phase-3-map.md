# Phase 3 code map (the Entities section of the panel)

Repo `/Users/dompa/dev/HassSharp`, branch `custom-entities`, HEAD `bc9a123`. Paths are under
`custom_components/hass_sharp/` unless they start with `tests/` or `docs/`. Every line number was read at HEAD.
Short names: `fe` = `frontend/src/`, `ct` = `fe/visual/contract.ts`, `VE` = `fe/visual/VisualEditor.tsx`,
`ses` = `fe/visual/session.ts`, `EA` = `HassSharp/Devices/EntityApi.cs`, `cat` = `visual_node_catalog.json`,
`EAT` = `tests/Runtime/EntityApiTests.cs`, `EGT` = `tests/Runtime/EntityGraphTests.cs`, `ft` = `frontend/tests/`.

## 1. Frontend layout and build

**Folders of `fe`** (58 test files in `ft`, ~20 k lines of source):

| Folder | What it holds |
|---|---|
| `fe/` root | `hass-sharp.ts` (entry, Lit element), `App.tsx` (router), `context.ts` (`HassCtx`, `useHass`, `liveHass`), `registry.ts` + `entities.ts` (the `hass_sharp/get_entities` query), `types.ts` (HA types + run history/trace types), `utils.ts` (`plural`, `errorWarningCounts`), `hooks.ts` (`useLocalStorage`), `panel-stylesheet.ts`, `style.css` |
| `layout/` | `MainLayout.tsx` (header + nav + `<main>`) |
| `views/` | `AutomationsListView.tsx`, `VisualAutomationView.tsx`, `RunTraceView.tsx`, `NodeLibraryView.tsx`, `TODOView.tsx` |
| `automations/` | list-page parts: `AutomationCard.tsx`, `EnabledSwitch.tsx`, `GraphThumbnail.tsx`, `RunDots.tsx`, `enabled.ts` (`setAutomationEnabled`), `model.ts` (filters, sorts, `relativeTime`, `pollInterval`) |
| `components/` | generic: `Controls.tsx` (`Button`, `Field`, `Status`, `Icon`), `Dialog.tsx`, `Menu.tsx`, `Tabs.tsx`, `CatalogPicker.tsx`, `EntityAutocomplete.tsx`, `ServicePicker.tsx`, `JsonTree.tsx`, `LazyLoad.tsx`, `HelpDialog.tsx`, `AutomationRuns.tsx`, `TriggerSummary.tsx`, `runDispatch.ts`, `Editor/` (Monaco) |
| `visual/` | the editor: `VisualEditor.tsx`, `session.ts`, `api.ts`, `library-api.ts`, `library-session.ts`, `contract.ts`, `graph.ts`, `connectionIndex.ts`, `Inspector.tsx`, `NodeConfig.tsx`, `TriggerConfig.tsx`, `InputRow.tsx`, `InputEditor.tsx`, `BottomDrawer.tsx`, `TopBar.tsx`, `NodeLibraryPanel.tsx`, `QuickAdd.tsx`, `RunNow.tsx`, `drafts.ts`, `layout.ts`, `icons.ts`, `nodeSummary.ts`, … |
| `visual/flow/` | canvas: `FlowAdapter.tsx` (1387 l.), `flowModel.ts`, `autoConnect.ts`, `NodeCard.tsx`, `Edges.tsx`, `Pin.tsx`, `CanvasToolbar.tsx` |
| `visual/trace/` | run trace: `TracePage.tsx`, `TraceInspector.tsx`, `Timeline.tsx`, `traceModel.ts`, `poll.ts`, `traceWords.ts` |
| `visual/widgets/` | value widgets: `pickers.tsx` (`EntityWidget` l.62, `StateWidget`), `model.ts`, `basic.tsx`, `common.tsx` (`DraftField`), `SearchSelect.tsx`, … |
| `styles/` | 11 sheets (below) |

No `fe/entities/` folder exists; `fe/entities.ts` (30 l.) is the registry entity list (`EntityInfo`, `useEntities` l.27).

**Entry and mounting.**
- `fe/hass-sharp.ts:15` `@customElement('ha-panel-hass-sharp-view')`, a `LitElement` rendering into light DOM (`createRenderRoot` returns `this`, l.27-29). HA sets `hass` on every update; l.25 `_hass = createSignal<HomeAssistant>()`, l.41 updates it, l.51 `render(() => App({ hass: liveHass(current) }), container)`.
- Python registers it: `__init__.py:115-128` `frontend.async_register_built_in_panel(hass, "hass-sharp-view", "HassSharp", "mdi:language-csharp", True, PANEL_URL, {}, True)`, `PANEL_URL = "hass-sharp"` (l.112); static files `/hass-sharp-static` → `www/` (l.90-96); JS loaded with `add_extra_js_url(... hass-sharp.js?v=<mtime>)` (l.99-102). The stylesheet is a `<link>` to `/hass-sharp-static/hass-sharp.css<same query>` (`fe/panel-stylesheet.ts:1-5`, rendered at `hass-sharp.ts:81-82`).
- What the app gets: `liveHass(current)` (`fe/context.ts:17-23`), a `Proxy` whose `get`/`has` read the latest `hass` **untracked**: reading `hass.states` never re-renders (doc comment l.8-16). Only named reads work (no `ownKeys`). `HomeAssistant` type `fe/types.ts:163-211`: `connection: Connection` (home-assistant-js-websocket 9.7.0), `states: HassEntities`, `user: CurrentUser` (`is_admin`), `callWS<T>(msg)`, `sendWS`, `callService`, `callApi`.

**Router** (`fe/App.tsx:41-49`, `@solidjs/router`, `base="/hass-sharp"`, `root={MainLayout}`):

| Path | Component |
|---|---|
| `/` | `<Navigate href="/automations" />` (l.42) |
| `/automations` | `AutomationsListView` (l.43) |
| `/visual/:id` | `VisualAutomationView` (l.44; `id === 'new'` is a new one) |
| `/visual/:id/runs/:runId` | `RunTraceView` (l.45) |
| `/libraries` | `NodeLibraryView` (l.46, `lazyView` l.16: Monaco is lazy) |
| `/devices` | `TODOView` (l.47) |
| `/entities` | `TODOView` (l.48) — renders `<div>TODO</div>` (`fe/views/TODOView.tsx:1-3`) |

`QueryClient` defaults (l.18-26): `refetchOnWindowFocus: false`, `retry: false`, `staleTime: 5 min`.

**Navbar** (`fe/layout/MainLayout.tsx:4-17`): sidebar toggle (dispatches `hass-toggle-menu`, l.6), brand link to `/automations` (l.11), `<nav aria-label="HassSharp">` with exactly two `A` links: `/automations` "Automations" and `/libraries` "Node library", `class="hs-nav-link" activeClass="hs-nav-link--active"` (l.12); `HOME ASSISTANT WORKSPACE` label (l.13); skip link (l.8).

**Build and test** (`frontend/package.json`): `bun run build` = `tsc && vite build` (to `../www`, entry `src/hass-sharp.ts`, `lit` external from unpkg, `frontend/vite.config.ts:20-37`); `bun run build:watch`; `bun run test` = `vitest run`; `build:preview`. Vitest 5.0.5, `environment: 'happy-dom'`, `include: ['tests/**/*.test.{ts,tsx}']`, `setupFiles: ['tests/setup.ts']` (`frontend/vitest.config.ts`); `tests/setup.ts` clears `localStorage` before each test. TypeScript ~7.0.2. Tailwind's Vite plugin is configured but no stylesheet imports Tailwind (only a stray `class="h-full"`, `hass-sharp.ts:86`).

**Test helpers** (detail in section 10): components are rendered with `render(() => <QueryClientProvider client><HassCtx.Provider value={hass}><MemoryRouter history>…`; `hass` is a plain object `{ callWS, user: { is_admin } }`; the in-memory backend is `ft/visual-fixture.ts:104` `visualFixture(options)` (handles `hass_sharp/node_library` l.144-232 and `hass_sharp/visual` from l.236; **no `hass_sharp/entities`**); the catalog is the raw `nodeCatalog` or `tests/fixtures/visual-rich-catalog.json`; `matchMedia` is stubbed (`ft/editor-shell.test.tsx:106,118`).

**CSS** (`fe/style.css:2-12`, cascade order): `tokens`, `base`, `controls`, `automations`, `editor-shell`, `flow`, `inspector`, `runs`, `trace`, `library`, `widgets`. All rules are scoped `.hs-app …`.
- Tokens (`fe/styles/tokens.css`): `--hs-bg`, `--hs-surface`, `--hs-text`, `--hs-muted`, `--hs-line`, `--hs-accent`, `--hs-action`, `--hs-on-action`, `--hs-danger`, `--hs-soft`, `--hs-focus` (l.2-14), each falling back from HA theme variables (`--primary-background-color`, `--card-background-color`, …); port colours l.19-27; category colours `--hs-cat-trigger|hass|flow|logic|data|code` l.38-43 (no `entity`); trace and run colours l.48-56 (`--hs-run-succeeded`, `--hs-run-cancelled`); `--hs-motion`.
- Dark: `@media (prefers-color-scheme: dark)` redefines the fallbacks (l.15-17, l.59-61); HA theme variables win when set. `color-scheme: inherit` (`base.css:10`).
- Page frame: `.hs-page` max 1120 px, padding 42/40 px (`base.css:44`); `.hs-page-heading` (l.45-47); `.hs-home` 1240 px (`automations.css:2`).
- Breakpoints in use: `max-width: 700px` (base l.48, automations l.89, controls l.64, editor-shell l.29/90, runs l.77), `699px` (flow l.223, editor-shell l.231, library l.108, trace l.93; JS `media('(max-width: 699px)')` `VE:241`, `BottomDrawer.tsx:41`), `600px` (inspector l.94), `899px`/`900px` (inspector l.195/207; JS `VE:179`), `1000px`/`1150px` (library l.59/56), `420px` (flow l.299), `pointer: coarse` (flow l.303), `forced-colors` (controls l.68), `prefers-reduced-motion` (several). `fe/visual/media.ts:4` `media(query)` is the reactive matcher.

## 2. The API layer

**Websocket wrapper.** Every call is `hass.callWS<T>({ type, … })`; answers are typed by a TS generic only (no runtime check). Call sites: `fe/registry.ts:55` (`hass_sharp/get_entities`), `fe/components/runDispatch.ts:55` (`hass_sharp/run_now`), `fe/components/AutomationRuns.tsx:59` and `VE:356` (`hass_sharp/run_history`), `fe/visual/api.ts:120,125,136`, `fe/visual/library-api.ts:51`, plus HA's own (`config_entries/get`, `frontend/get_themes`, `conversation/agent/list`, `auth/sign_path` in `fe/visual/widgets/*`; `trigger_platforms/subscribe` via `connection.subscribeMessage` in `fe/visual/TriggerConfig.tsx:251`).

**Envelope** (`fe/visual/api.ts:99`): `{ version: 1; ok: true; result: T } | { version: 1; ok: false; error: { code: string; detail: unknown } }`. `visualApi(hass)` (l.118-148): `request(operation, fields)` sends `{ type: 'hass_sharp/visual', request: { operation, ...fields } }` and throws `VisualApiError(code, detail)` (l.100-113; message = `detail` when a string, else "Fix the graph errors before saving." for `validation`, else `Visual automation request failed (<code>).`). `visualError(err)` (l.114-117) → `message ?? error ?? code ?? 'Check your connection and try again.'` (also covers HA's `{code, message}` websocket errors). `libraryApi` (`library-api.ts:49-84`) is the same for `hass_sharp/node_library`, with `LibraryApiError` (l.45-47).

**Python side.** `websocket.py:71-82` registers `hass_sharp/visual`, `hass_sharp/node_library`, `hass_sharp/entities` with `envelope_command(service_method, subject, code)` (l.56-68), schema `{id: int, type, request: dict}`. Before the handler: non-admin → `send_error(id, "unauthorized", "Administrator required")`; no ready entry → `"not_ready"` with `NOT_READY` text (l.13, 42-53). A Python exception → `send_error(id, "entity_request_failed", "Entity request failed; check integration logs")`. Other routes: `run_now` (`automationId`, `className`, `method`; refusals → `invalid_request`, else `run_failed`, l.84-95), `run_history` (`automationId`, `offset` default 0, `limit` 1-100 default 50, `status?: [RUN_STATUSES]`; error `history_failed`, l.97-105, 131-135), `run_trace` (`runId`; `trace_failed` / `not_found` "Run not found", l.107-125), `get_entities` (admin only, `entities_failed`, l.145-159).
- `authoring.py:69-70` `entities()` passes the request through to `manager.EntityRequest` as JSON. `visual()` (l.31-64) additionally (a) for `preview`/`save` runs `native_triggers.async_validate_graph_triggers` and merges its diagnostics (l.38-55), (b) adds `triggers` (`runnable_triggers`, l.11-15) to `list`/`read`/`save` documents (l.57-63). **Neither happens for `entities`.**

**Automation operations the panel uses.**

| Call | Request | Answer (TS type) |
|---|---|---|
| `node_library` `catalog` (`api.ts:131-132`, session) | `{operation}` | `{ catalog: VisualCatalogSnapshot; generation; servicesGeneration?; saved: true; valid; diagnostics }` |
| `node_library` `services` (`api.ts:134`) | `{operation}` | `ServicesSection` `{servicesGeneration, items}` (`ct:69`) |
| `visual` `preview` (`api.ts:137`) | `{graph}` | `VisualPreview {valid, source, sourceMap: SourceRange[], diagnostics: VisualDiagnostic[]}` (l.25-30) |
| `visual` `list` (`api.ts:138`) | — | `VisualDocumentSummary[]` (doc without source/sourceMap/savedUtc) |
| `visual` `list` `view:'home'` (`api.ts:140`) | `{view:'home', todayUtc}` | `HomeAutomation[]` (l.75-93: `id, revision, automationId, enabled, active?, currentRevisionActive, activationError?, problemCount?, savedUtc?, catalogGeneration?, outline: GraphOutline, recentRuns: RecentRun[] (≤10, newest last), runsToday, failedLastDay, lastRunUtc`) |
| `visual` `set_enabled` (`api.ts:141`) | `{id, enabled}` | `{id, enabled}` |
| `visual` `read` (`api.ts:142`) | `{id}` | `VisualDocument` (l.31-49: `version:1, id, revision, graph, source, sourceMap, catalogGeneration?, activeRevision?, activeSourceHash?, currentRevisionActive?, active?, activationError?, automationId, enabled?, savedUtc?`) |
| `visual` `save` (`api.ts:144-145`) | `{id, graph, expectedRevision, enabled?}` | `VisualSaveResult {document, active, activationError}` (l.94-98; server also sends `id`, `saved`, `diagnostics?`, `VisualApi.cs:33-34`) |
| `visual` `delete` (`api.ts:146`) | `{id, expectedRevision}` | `{deleted: true}` |
| `run_trace` (`api.ts:136`, not enveloped) | `{runId}` | `RunTraceResponse` (`types.ts:475-481`) |
| `run_history` (not enveloped) | `{automationId, offset, limit, status?}` | `RunHistoryResponse {version:2, offset, limit, runs: AutomationRun[]}` (`types.ts:418-423`) |
| `run_now` (`runDispatch.ts:48-61`) | `{automationId, className, method}` | `RunAcceptedResponse {version:1, runId}` |
| `get_entities` (`registry.ts:53-59`) | — | `EntityRegistry {entities: EntityInfo[], areas, devices, floors, labels}` |

The `visual` API also has an operation `run` (`{id, triggerId}`, `VisualApi.cs:127`) the panel does not use.

**Entity definition operations** (`EA:229-334`, one `_visualGate` for the request; operation checked first, l.237: `types`, `list`, `read`, `preview`, `save`, `set_enabled`, `delete`; anything else → `invalid_request` "Unknown entity operation", `EAT:101`). JSON is camelCase (`VisualStore.Json`, `HassSharp/Visual/VisualStore.cs:33`).

| Operation | Request fields | Answer `result` | Source / test |
|---|---|---|---|
| `types` | — | `{ types: EntityTypeDescriptor[] (by id), generation: string }` | `EA:246-247`; `EAT:108-112` |
| `list` | — | `EntityJson(doc)[]` minus `source`, `sourceMap`; by id | `EA:255-260`; `EAT:224-228` |
| `read` | `id` | `EntityJson(doc)` with `source`, `sourceMap` | `EA:264`; `EAT:229-231` |
| `preview` | `definition` (a `DefinitionBody`) | `{ valid, diagnostics, source }` — **no `sourceMap`** | `EA:220,248-253`; `EAT:189-191` asserts the keys are exactly `diagnostics, source, valid` |
| `save` | `id`, `expectedRevision` (0 = new), `definition`, `enabled?` (new only) | `{ id, saved: true, document: EntityJson, active, activationError, diagnostics? }` (`diagnostics` only with warnings) | `EA:292-299`, `EA:110-115`; `EAT:116-125` |
| `set_enabled` | `id`, `enabled` | `{ id, enabled, active, activationError }` | `EA:265-289`; `EAT:237` `{"id":"plug1","enabled":false,"active":false,"activationError":null}` |
| `delete` | `id`, `expectedRevision` | `{ deleted: true }` | `EA:300-318`; `EAT:295` |

`EntityJson(document)` (`EA:35-49`) = the stored document + `automationId` (`_history.Identity(path)`, the run history key), `active` (a loaded script exists), `currentRevisionActive`, `activationError` (load error, else the failed host's `Error`, else `null` if running, else `"Not active"`), `status` (`running` | `setup` | `failed` | `off`; host constants `DefinitionHost.cs:34`, `stopped` maps to `off`), `liveEntities: LiveEntity[]` (`[]` when not loaded). `LiveEntity` (`Devices/EntityDirectory.cs:11`): `{uniqueId, key, domain, name, entityId}`, e.g. `{"uniqueId":"plug1:relay","key":"relay","domain":"switch","name":null,"entityId":"switch.plug1_relay"}` (`EAT:124`).
- `id` must match `^[a-z0-9][a-z0-9_-]{0,79}\z` (`Devices/EntityDefinition.cs:101`), else `invalid_request` "Invalid definition ID" (`EAT:309`).
- Save of a new definition with `enabled:false` stores it off (`EAT:284`); `enabled` on an existing one → `invalid_request` "enabled can only be given when saving a new definition; use set_enabled to turn a saved one on or off" (`EntityDefinition.cs:190`, `EAT:267`).
- A save waits up to 5 s for `Start()` (`EA:21,127`); later it answers `status: "setup"`.
- `set_enabled` on: regenerates against the saved libraries; invalid → records the load error and answers it (`EA:272-287`).
- `delete` also deletes the history identity (`EA:305`) and removes HA entities, bounded by `RemovalTimeout` 30 s (`EA:24,311`); a timeout is logged, the answer is still `{deleted:true}`.

**Error forms** (`EA:324-332`, helpers `HassSharp/Visual/VisualApi.cs:321-327`): `{version:1, ok:false, error:{code, detail}}` with
`storage` (IO message) · `validation` (detail = `{valid:false, diagnostics, source:""}`, `EAT:193-195`) · `conflict` ("Revision conflict; reload the current document", `VisualStore.cs:7`) · `not_found` ("Entity definition not found") · `invalid_request` (ArgumentException/JsonException/InvalidOperationException/OverflowException text: "id is required", "definition is required", "definition must be an object", "expectedRevision is required: the revision this replaces, 0 for a new definition", "enabled must be true or false", …; `HassSharp/Visual/ApiRequest.cs:15-54`) · `internal` ("HassSharp hit an internal error; see the Home Assistant log", `ApiRequest.cs:68`).

**Missing compared with automations:** no `home`-style list (no outline, `recentRuns`, `runsToday`, `failedLastDay`, `problemCount`); no `run` and `RunNow` refuses definitions (`HassSharp/Manager.cs:240` → "Automation is not active", `EAT:183-184`); no history or trace operation of its own (`run_history`/`run_trace` work with the definition's `automationId`/run ids, `EGT:674-707`); no `triggers` list; no native-trigger validation (`authoring.py:38-55`); no `deriveSignatures`; no `catalog` (use the library's); no secret masking in `list`/`read`/`save` (only the library save masks, section 8); no TS client module.

## 3. The stored definition document and its two logic kinds

**Records** (`Devices/EntityDefinition.cs`): `DefinitionVia(Definition?, DeviceId?)` l.12; `DefinitionDevice(Manufacturer?, Model?, SwVersion?, HwVersion?, ConfigurationUrl?, SuggestedArea?, Via?)` l.22-23; `DefinitionSetting(Key, Value: JsonNode?)` + `Label?` (omitted when null), `Type?` (omitted when null), `Secret` (omitted when false) l.31-39; `DefinitionLogic(Kind, Type?, Graph?)` l.46; `DefinitionBody(Name, Device?, Settings?, Entities: JsonArray?, Logic)` l.54-55 (the `definition` request field); `EntityDefinitionDocument(Version, Id, Revision, Name, Device?, Settings, Entities?, Logic, Source, SourceMap)` + `Enabled` (default true), `SavedUtc?`, `CatalogGeneration?` l.61-77 (`Body`, `EmbeddedCatalog` are `[JsonIgnore]`).

**Document checks** (graph logic only, `Devices/DefinitionDocument.cs:27-62`): a graph is required; ≥1 entity; entity key `^[a-z][a-z0-9_]{0,39}\z` (`Devices/SharpEntity.cs:181`), unique; `name`, `icon`, `deviceClass` texts or null (l.15); `entityCategory` `config` | `diagnostic` | null (l.17); `enabledByDefault` true/false or absent (absent = true); `domain` a key of `entityDomains`; `options` only those the domain lists, of their type/enum (`array` = list of texts) (l.79-105); unknown entity members kept. Settings: key unique, `type` ∈ `string`|`boolean`|`number`, value of that type or null. Name 1-120 chars (`Devices/DefinitionGenerator.cs:48`). A class definition with entities → `definition.entitiesNotAllowed` (l.52); an empty array counts as none.

**Graph definition — the derived motion sensor** (`definition` as `EGT:88-94` builds it from `EGT:111`; graph = shared fixture case "entity graph: derived binary sensor" in `tests/fixtures/visual-graph-validation.json`, `definition` stripped by `tests/Runtime/EntityStoreTests.cs:82-84`):

```json
{"operation":"save","id":"motion","expectedRevision":0,"definition":{
  "name":"Any motion","device":null,"settings":[],
  "entities":[{"key":"any_motion","domain":"binary_sensor","name":"Any motion"}],
  "logic":{"kind":"graph","graph":{"version":5,"name":"Any motion",
    "nodes":[
      {"id":"a","kind":"entity_is","config":{"entity":{"kind":"literal","type":"entity_id","value":"binary_sensor.motion_a"},"state":{"kind":"literal","type":"string","value":"on"}},"label":"Has state ▸ motion A"},
      {"id":"b","kind":"entity_is","config":{"entity":{"kind":"literal","type":"entity_id","value":"binary_sensor.motion_b"},"state":{"kind":"literal","type":"string","value":"on"}},"label":"Has state ▸ motion B"},
      {"id":"or","kind":"or","config":{"left":{"kind":"unset"},"right":{"kind":"unset"}}},
      {"id":"s","kind":"entity_state","config":{"entity":"any_motion"},"label":"State ▸ any motion"}],
    "edges":[
      {"id":"a.value:or.left","kind":"data","source":"a","target":"or","sourcePort":"value","targetPort":"left"},
      {"id":"b.value:or.right","kind":"data","source":"b","target":"or","sourcePort":"value","targetPort":"right"},
      {"id":"or.value:s.is_on","kind":"data","source":"or","target":"s","sourcePort":"value","targetPort":"is_on"}]}}}}
```
Answer facts (`EGT:253-283`): sync entities `[{"uniqueId":"motion:any_motion","key":"any_motion","domain":"binary_sensor","name":"Any motion","icon":null,"deviceClass":null,"entityCategory":null,"enabledByDefault":true,"commands":[],"options":{}}]` (l.261); `document.liveEntities` `[{"uniqueId":"motion:any_motion","key":"any_motion","domain":"binary_sensor","name":"Any motion","entityId":"binary_sensor.motion_any_motion"}]` (l.263). The stored `logic.graph` has positions filled (`EntityDefinition.cs:194-199`, `FillMissingLayout`) and never a `definition` (l.207).

**Graph definition — the device** (`EGT:511-535`): `Definition("Plug", graph, [power: sensor {name:"Power", deviceClass:"power", options:{unit:"W", stateClass:"measurement"}}, online: binary_sensor {deviceClass:"connectivity", entityCategory:"diagnostic"}, relay: switch {name:"Relay", icon:"mdi:power-socket", enabledByDefault:false}], device: {manufacturer:"Acme", model:"P1", suggestedArea:"Kitchen"})`, graph nodes `entity_state p (power, value=42)`, `entity_state o (online, is_on=true)`, `entity_command on (relay, turn_on) → set_entity_state set_on (relay, is_on=true)`, `entity_command off → set_entity_state set_off`. The device synced is `{"name":"Plug","manufacturer":"Acme","model":"P1","swVersion":null,"hwVersion":null,"configurationUrl":null,"suggestedArea":"Kitchen","via":null}` (l.525): its name is the definition's `name`.

**Graph settings** (`EGT:540-541`, `EGT:983-985`): `{"key":"offset","type":"number","label":"Offset","value":4}`; stored/answered as `{"key":"offset","value":4,"type":"number"}`, a secret one `{"key":"token","value":"graph-hunter2","label":"Token","type":"string","secret":true}` (answered in clear by `read`, `EGT:1003-1004`). A value-only change keeps the generated source (`EGT:546`).

**Class definition** (`EAT:60-65,116`):

```json
{"operation":"save","id":"plug1","expectedRevision":0,"definition":{
  "name":"Plug one","settings":[{"key":"Host","value":"10.0.0.1"}],
  "logic":{"kind":"class","type":"library:shelly:NodeLib.FakePlug"}}}
```
`result` (field order of the records, values asserted in `EAT:117-125`, `230`):
```json
{"id":"plug1","saved":true,"active":true,"activationError":null,"document":{
  "version":1,"id":"plug1","revision":1,"name":"Plug one","device":null,
  "settings":[{"key":"Host","value":"10.0.0.1"}],"entities":null,
  "logic":{"kind":"class","type":"library:shelly:NodeLib.FakePlug","graph":null},
  "source":"// HassSharp entity definition\npublic static class SharpDefinition { public static object Create() => new global::NodeLib.FakePlug(); }\n…",
  "sourceMap":[{"nodeId":"$library:shelly","startLine":…,"endLine":…}],
  "enabled":true,"savedUtc":"…","catalogGeneration":"…",
  "automationId":"…","active":true,"currentRevisionActive":true,"activationError":null,"status":"running",
  "liveEntities":[{"uniqueId":"plug1:relay","key":"relay","domain":"switch","name":null,"entityId":"switch.plug1_relay"},{"uniqueId":"plug1:power",…}]}}
```
Class settings store `{key, value}` only (`EntityDefinition.cs:204`); `null` keeps the class default (`DefinitionGenerator.cs:99-103`). Class setting checks: `definition.unknownSetting`, `definition.settingType` ("<Label> takes a whole number from … to …", "one of A, B", …), `definition.duplicateSetting` (l.86-122). Unknown class → `definition.unknownType` (l.60-61); library errors → `definition.library` (l.56).

**`types` answer.** `EntityTypeDescriptor(Id, SourceId, TypeExpression, Kind: "device"|"entity", Domain?, Title, Description?, Settings: EntitySettingDescriptor[])` (`HassSharp/Visual/VisualCatalogSnapshot.cs:72-73`); `EntitySettingDescriptor(Key, Label, Description?, Type: "string"|"boolean"|"integer"|"number"|"enum", Secret, Options: string[])` + `minimum?`/`maximum?` (omitted when null) (l.51-59). Example setting: `{"key":"Channel","label":"Channel","description":null,"type":"integer","secret":false,"options":[],"minimum":-2147483648,"maximum":2147483647}` (`tests/Runtime/EntityTypeTests.cs:76`). Id form `library:<sourceId>:NodeLib.<Type>`. **The descriptor lists no entities and no device metadata**; `EntityTypes` never enter the catalog root (l.90-94).

**Compose on the server** (`Devices/DefinitionGraph.cs:31-48`): `Compose(body)` = `Decompose(graph)` + `definition = {entities: <body.entities as given>, settings: [{key, type}]}` (values, labels, secret never reach the graph); `Decompose` removes `definition`. Used by preview (`DefinitionGenerator.cs:76`), compile (`EA:68`), store (`EntityDefinition.cs:196,207,226`). `Roots(graph, snapshot)` (l.51-73) keys commands `<entity key>:<command>`. The document problems are reported **before** the graph is checked; with any of them no graph diagnostic is returned (`DefinitionGenerator.cs:65-66`). The automation API drops an object `definition` (`VisualApi.cs:178-183`).

**What the TS validator expects** (`ct`): `GraphDefinition { entities: ({key; domain} & Record<string, unknown>)[]; settings: {key; type}[] }` (l.17); `Graph.definition?` (l.19); `graphDocument(graph)` = `'entity'` iff `graph.definition` is an object (l.193); `withGraphTypes` lifts it into `catalog.definition` and types each `setting` node's output under `resolvedOutputs` (l.309,313, `resolveSettings` l.320-332); `connectionIndex`/`graphTypes` (`fe/visual/connectionIndex.ts:62-85`), `buildNodeView` (`flow/flowModel.ts:217`), `NodeConfig.tsx:91` and `FlowAdapter.tsx:1060` all lift, so a graph that carries `definition` is enough for validation and for `dynamicInputs: 'entity'` pins. `checkDefinition` (l.1328-1342) wants arrays of objects with text `key`/`domain` and `key`/`type` (extra fields allowed), unique keys, domains in `entityDomains`, types in `settingTypes` (l.195). `entityDomains` comes from the catalog root (`cat:1985`, typed `EntityDomainSpec` `ct:71`); the server catalog (library `catalog`) carries it because it is part of the embedded JSON. An entity graph may be empty (l.992-994); no trigger needed (l.1063). TS does **not** mirror the document checks (key pattern, options, setting values, name length).

## 4. The visual editor as it is

**Page:** `fe/views/VisualAutomationView.tsx:8-39` — `useBeforeLeave` guard with "Unsaved changes" dialog (l.17-21, 34-37); renders `<VisualEditor documentId={id !== 'new' ? id : undefined} onDirtyChange discardRef onSaved={doc => navigate('/visual/<id>', {replace})} onBack={() => navigate('/automations')} onOpenLibrary={(id, line) => navigate(libraryHref(id, line))} drawer={'runs'?} name={?name ?? 'Untitled visual automation'} />` (l.26-33).

**`VisualEditor` props** (`VE:46-59`): `name: string; documentId?; onDirtyChange?(dirty); onSaved?(document: VisualDocument); onBack?(); drawer?: 'problems'|'code'|'runs'; discardRef?(discard); onOpenLibrary?(id, line?)`.

**Tree** (`VE:437-667`), all inside `<section class="hs-visual" aria-label="Visual automation editor">` (keyboard boundary, `delegateEvents`, l.441-449):

| Component (file) | Props passed in VE | Responsibility |
|---|---|---|
| `TypeDefinitions` (`visual/typeDefinition.tsx:72`) l.451 | `load`, `graph`, `catalog`, `onOpenLibrary`, `onEditCode` | context for "Go to type definition" popovers |
| `TopBar` (`visual/TopBar.tsx:47-71`) l.453-481 | `name`, `onRename(name,{coalesce})`, `onBack`, `backLabel?`, `save: SaveState {saving, dirty, errors, revision?}` (l.12-19), `status?`, `busy`, `canUndo/canRedo/onUndo/onRedo`, `canSave`, `saveTitle`, `onSave`, `run?: JSX`, `actions?: JSX`, `notice?`, `fullscreen`, `onExitFullscreen`, `menu: MenuItem[]` | back, editable name, save chip, undo/redo, Save, run group, ⋯ menu |
| `EnabledSwitch` (`automations/EnabledSwitch.tsx:6`) l.470 | `name, enabled, disabled?, busy?, class?, onChange` | on/off via `setAutomationEnabled` (`VE:335-349`) |
| `RunNowButton` (`visual/RunNow.tsx:51-61`) l.472 | `document?: RunnableDocument (Pick<VisualDocument,'graph'|'automationId'>)`, `catalog`, `names?`, `unavailable?`, `dirty`, `busy?`, `onNotice` | manual runs of `trigger` nodes (l.23-41) via `dispatchRun` |
| `NodeLibraryPanel` (`visual/NodeLibraryPanel.tsx:11-21`) l.507 | `catalog?`, `collapsed`, `onCollapsedChange`, `onAdd(kind, label)`, `onQuickAdd()`, `disabled?` | step library (palette) |
| `FlowAdapter` (`visual/flow/FlowAdapter.tsx:43-68`) l.515 | `graph`, `catalog?`, `readOnly?`, `selectedId?`, `onSelect`, `onSelectionChange?`, `onChange?(graph,{coalesce?})`, `diagnostics?`, `addStepRequest?`, `fitViewKey?`, `trace?`, `toolbarExtra?`, `controls?(FlowControls)`, `onEditCode?` | canvas; hosts `QuickAdd` (l.1371-1379; props `catalog?, x, y, title, allowed(kind), onChoose, onClose`, `QuickAdd.tsx:18-30`); `FlowControls {duplicate, placeAtCentre, reveal, notify}` (l.71-83) |
| `Inspector` (`visual/Inspector.tsx:21-58`) l.530 | `selected?`, `selectedIds?`, `selectedEdgeId?`, `graph`, `catalog`, `names?`, `diagnostics`, `commit`, `onSelect?`, `onShowProblems?`, `lastRun?`, `enabled?`, `docked?`, `open?`, `onClose?`, `onOpen?`, `onDuplicate?`, `onEditCode?`, `onNotice?` | side panel / sheet; `NodeConfig` for one node; `Overview` when nothing is selected (l.216-250) |
| `BottomDrawer` (`visual/BottomDrawer.tsx:29-39`) l.552 | `tabs: DrawerTab[] {id, label, count?, tone?, lazy?, content}` (l.11-20), `open`, `tab`, `onOpenChange`, `onTabChange`, `label?`, `variant?` | tabs Problems (`ProblemsPanel` l.132-141), Generated C# (`CodePanel` l.210-221), Runs (`AutomationRuns`, `VE:581-592`) |
| `Dialog` (`components/Dialog.tsx:4-12`: `open, title, children, footer?, width?, preventClose?, onClose`) | l.595, 617, 636 | reload/discard, conflict (keep/copy/discard), restore draft |
| `HelpDialog` l.647 | `open`, `onClose` | automation help text |
| `CustomCodeEditor` / `CodeLeftBehind` l.649-664 | `node, catalog, draft, onDirtyChange, onApply, onClose` | Custom C# node code (Monaco) |

**Session** (`ses:36-576`, `createVisualSession(hass, initial: VisualGraph, {unsaved?, code?})`):
- State: `history = createGraphHistory(initial)` (l.42; `graph.ts:276-323`: `graph`, `commit(next,{coalesce?})`, `undo`, `redo`, `reset`, `canUndo/Redo`, 100 steps); `libraryCatalog` + `services` → `catalog` memo (l.43-49); `catalogGeneration`, `catalogReady`, `catalogError`; `document: VisualDocument` (l.53); `baseline` (serialized graph, l.54); `preview`, `previewError`; `error`, `conflict`, `conflictDraft`, `restoreOffer`, `restoredCode`, `activationError`, `loading`, `saving`, `readFailed`.
- Dirty: `dirty = serializeGraph(history.graph()) !== baseline()` (l.69) — **the graph only**. `diagnostics` = local `connectionIndex(graph, catalog).analysis.diagnostics` + backend preview diagnostics not already local (l.71-83).
- Loads: `api.catalog()` on start, on `hass-sharp-libraries-changed` and window `focus` (l.112-176); if the open document was not checked against this generation, `api.read(id)` and either show the new revision or set `conflict` (l.128-157). `load(id)` → `api.read` (l.353-382; rejects a graph whose `version` ≠ 5, l.367).
- Preview: effect on graph/catalog/attempt, 300 ms debounce, `api.preview(graph)` (l.184-205); stale answers dropped by `generation`. `canSave` (l.210-218).
- Save: `api.save(document()?.id ?? id, graph, revision ?? 0)` (l.383-400); new ids `visual-<uuid>` (l.84, 335, 477); `saved()` (l.401-422); `refused()` handles `conflict` (reload or set conflict) and `validation` (shows detail as preview) (l.423-433); `keepMine` (l.435-470), `saveCopy` (name + " (copy)", saved `enabled: false`, l.472-499), `discardChanges` (l.501-509).
- No autosave. Drafts: `localStorage` `hass-sharp:draft:<user>/<id>` (`fe/visual/drafts.ts:6,22`), `VisualDraft {graph, revision, name, at, code?}` (l.11), written 500 ms after a change (`ses:12,263-277`), on `pagehide` and teardown (l.279-284); new automations use `new:<uuid>` keys (l.314-320).

**Every automation-specific place in the editor:**

| Where | What |
|---|---|
| `ses:4,41` | `visualApi(hass)`; calls at l.116 (`catalog`), 134, 365, 443 (`read`), 195 (`preview`), 390, 482 (`save`) |
| `ses:13` | `conflictMessage = 'This automation changed elsewhere while you had unsaved changes here.'` |
| `ses:53,322-331,367,401-422` | `VisualDocument`, `saved.graph`, `latest.graph?.version` (l.139), `VisualSaveResult` |
| `ses:84,335,477` | id prefix `visual-` |
| `ses:476,482` | copy named `(copy)` and saved off |
| `VE:7,588` | `AutomationRuns automationId` |
| `VE:22,62,112` | `createGraph(props.name)` (starter trigger→condition→service, `graph.ts:93-111`) |
| `VE:25-26,335-349` | `EnabledSwitch` + `setAutomationEnabled` (`automations/enabled.ts:17-29`, `visualApi.setEnabled`, cache key `['visual-automations','home']` l.6) |
| `VE:123` | invalidates `['visual-automations']` |
| `VE:171` | initial selection `'trigger-1'` |
| `VE:311,430` | "The saved automation changed elsewhere…" |
| `VE:315,354-362` | `automationId`, `hass_sharp/run_history` last run |
| `VE:416` | download name `automation.graph.json` |
| `VE:447` | `aria-label="Visual automation editor"` |
| `VE:472` | `RunNowButton` (triggers only) |
| `VE:586` | "Save this automation to see its runs here." |
| `VE:617,627,629,631` | conflict dialog texts ("This automation changed elsewhere", "Saving a node library does this to every automation that uses it", "a new automation, turned off") |
| `TopBar.tsx:78,95,108-109` | coalesce key `automation-name:`, back label default "Back to automations", `aria-label="Automation name"` |
| `Inspector.tsx:218,226,228,231` | Overview lists `kind === 'trigger'`, "Automation overview", "Off: nothing starts this automation by itself. Run now still works.", "This automation has no trigger yet." |
| `BottomDrawer.tsx:150,160` | null-node problems grouped under heading "This automation" |
| `RunNow.tsx:30,72,81-82` | `node.kind !== 'trigger'`, "Save the automation to run it", "Run the saved automation now" |
| `components/HelpDialog.tsx:5,7,19,24` | "Visual automation help", trigger/Run now wording |
| `views/VisualAutomationView.tsx:29-30,33` | `/visual/<id>`, `/automations`, "Untitled visual automation" |
| `NodeLibraryPanel.tsx:36`, `QuickAdd.tsx:38`, `FlowAdapter.tsx:122`, `graph.ts:49` | `catalogPalette(catalog)` without a document → `'automation'` |

## 5. Graph model and flow code

- `graph.ts:37-38` `catalogPalette(catalog = nodeCatalog, document: GraphDocument = 'automation')` keeps kinds with no `documents` or whose `documents` include `document`; `ft/palette.test.tsx:23-30` pins it (entity-only = `entity_state, entity_command, entity_setup, set_entity_state, set_entity_available, setting`). Callers pass no document (table above).
- `ct:1396-1406` `addGraphNode(graph, kind, id, catalog)`: copies **every** `spec.config[*].default` into `config` (l.1400) and every input default into `config` or `config.arguments` (l.1402), sets `label: spec.label` (except Custom C#). So a `trigger` gets `trackHistory: true`, `entity_state` `false`, `entity_command`/`entity_setup` `true`; every When commanded is labelled "When commanded" (the run's `actionLabel`, `Manager.cs:225`).
- `isRoot` (`ct:191`) = `kind === 'trigger' || spec.root === true`; used only in `ct:1031` and `ct:1062`.

**Every `kind === 'trigger'` / trigger-root assumption outside the validator:**

| File:line | Test | Effect for entity roots |
|---|---|---|
| `graph.ts:144` (`canConnect`) | `destination.kind === 'trigger'` | a step order wire into `entity_command`/`entity_setup`/`entity_state` is allowed by the canvas (validator then says `root.incoming`) |
| `graph.ts:75,83` (`visualRunClasses`, `visualActionLabels`) | triggers only | — |
| `graph.ts:95-110` (`createGraph`) | starter graph | automation starter |
| `flow/flowModel.ts:212` (`unreachable`) | `node.kind !== 'trigger'` | relies on `analysis.scopes` (roots included) |
| `flow/flowModel.ts:245` (`execIn`) | `node.kind === 'trigger' \|\| pure` | entity roots get an execution **input** pin |
| `flow/autoConnect.ts:66` | `if (kind === 'trigger') return` | auto-connect may wire into a new entity root |
| `flow/autoConnect.ts:97` (`running`) | reached from triggers | entity flows count as not running |
| `layout.ts:102,106` (`lead`, `hasTrigger`) | layout leads/ordering | entity roots not treated as flow heads |
| `flow/FlowAdapter.tsx:580` | trace opening focus | first trigger on the path |
| `flow/NodeCard.tsx:374` | `hs-card--trigger` class | look |
| `icons.ts:49` | trigger identity | — |
| `NodeLibraryPanel.tsx:43`, `QuickAdd.tsx:48` | category sample | look |
| `nodeSummary.ts:185` | summary text | entity nodes have no summary case |
| `Inspector.tsx:218` | Overview "Starts when" | lists triggers only |
| `RunNow.tsx:30` | runnable roots | triggers only |
| `automations/model.ts:22`, `automations/GraphThumbnail.tsx:21` | list page | — |
| `connectionIndex.ts:282` | config keys that change outputs | trigger only (fine) |
| `NodeConfig.tsx:179,199,241,264,328` | trigger form | see below |

**Node config forms** (`fe/visual/NodeConfig.tsx`, props l.32-57):
- Generic: `configFields` (l.177-186) = every `spec.config` key except `ownConfig` (`carry, exports, branches`, l.29) and `kindConfig[kind]` (l.30: `service`, `custom`, `select_property`, `object_create`, `array_create`, `loop_result`). Each is a `ConfigField` (l.116-176): `enum`/`enumSource` → `<select>`; `boolean` → switch with "Not set/True/False" text (l.153-156); `condition.state` → `StateWidget`; else `DraftField` free text (number-parsed for `number`/`integer`).
- Inputs: `InputRow` per port (`fe/visual/InputRow.tsx`, l.264-268) → `InputEditor`/widgets; an `entity_id` input → `EntityWidget` (`widgets/pickers.tsx:62-71`) → `EntityAutocomplete` (`components/EntityAutocomplete.tsx:10-19`) → `CatalogPicker` (`components/CatalogPicker.tsx:31-57`, `kind: 'entities' | 'services'`, `items: CatalogItem {id, name?, description?}[]`, `filter`, `multiple`, `manualError`, …). Options come from `useEntities()` = the `hass_sharp/get_entities` registry (`fe/entities.ts:27-30`, `fe/registry.ts:53-65`, refetched when older than 60 s, l.61).
- Special: `TriggerConfig` (`visual/TriggerConfig.tsx:235-481`, props `node, onChange, onRetype?, diagnostics?`) owns a trigger's whole config (`NodeConfig.tsx:178-179, 241-248`) and `TriggerAdvanced` (l.483-513: `manual`, `startup`, JSON). **`trackHistory`** is shown in `TriggerConfig.tsx:471-474` as `checked={config().trackHistory !== false}` with "Record automatic runs and their traces. Run now is always recorded."; for `entity_state`/`entity_command`/`entity_setup` it is a generic boolean `ConfigField` labelled from the catalog.
- Server default for a definition's trigger: `EA:95-99` `TrackHistory` = explicit `trackHistory`, else `false` for `trigger`, else the spec default, else `true`. Automations: `VisualApi.cs:71` `?? true`.

## 6. The entity nodes in the catalog

| Kind (cat lines) | Label · category | Flags | Config (type, default) | Inputs | Outputs / execution |
|---|---|---|---|---|---|
| `entity_state` (1684-1719) | State · Entity | `root`, `documents:["entity"]`, `dynamicInputs:"entity"` | `entity` string required; `trackHistory` boolean default **false** | `{}` + domain state ports | `{}`; `execution: []` |
| `entity_command` (1720-1763) | When commanded · Entity | `root`, `documents:["entity"]` | `entity` string req.; `command` string req. ("such as turn_on or turn_off"); `trackHistory` bool default true | `{}` | `{}`; `["next"]` (no data output: phase 4) |
| `entity_setup` (1764-1795) | When started · Entity | `root`, `singleton`, `documents:["entity"]` | `trackHistory` bool default true | `{}` | `["next"]` |
| `set_entity_state` (1796-1825) | Set state · Entity | `documents:["entity"]`, `dynamicInputs:"entity"` | `entity` string req. | `{}` + domain state ports | `["next"]` |
| `set_entity_available` (1826-1869) | Set available · Entity | `documents:["entity"]` | `entity` string req. | `available` boolean req., default literal `true` | `["next"]` |
| `setting` (1870-1903) | Setting · Entity | `pure`, `documents:["entity"]` | `key` string req. | `{}` | `value` type `""` (resolved per setting, `ct:320-332`) |
| `entity_is` (1904-1950) | Has state · Hass | `pure`; any document | `{}` | `entity` `["entity_id"]` req. (no default); `state` `["string"]` req., default literal `"on"` | `value` boolean "Result" |
| `entity_current` (1951-1983) | Current state · Hass | `pure`; any document | `{}` | `entity` `["entity_id"]` req. | `value` `global::HassSharp.EntityStateSnapshot` |

`dynamicInputs: 'entity'` (`ct:510-515`): one required port per `entityDomains[<domain of config.entity>].state` key, types from the table, no default.

**How the panel renders them today:** `entity` (State/When commanded/Set state/Set available), `command`, `key` → free-text `DraftField` (no picker); `trackHistory` → generic switch; `entity_is.entity`/`entity_current.entity` → registry entity picker (fine); `entity_is.state` → text input with no state suggestions (only `condition.state` gets `StateWidget`, `NodeConfig.tsx:129,135`); no node summary (`nodeSummary.ts:180-200` has no case); category "Entity" is unknown to `nodeIdentity` (`icons.ts:41-58`) → library icon, hashed hue, label "Entity".

**`entityDomains`** (cat 1985-2051): `binary_sensor` {label "Binary sensor", state `is_on` [boolean] "On", commands {}, options {}}; `sensor` {state `value` [number, string] "Value", commands {}, options `unit` string "Unit", `stateClass` string enum `measurement|total|total_increasing` "State class", `options` array "Options"}; `switch` {state `is_on` [boolean] "On", commands `turn_on` "Turn on", `turn_off` "Turn off", options {}}.

**Validation codes for entity graphs** (`ct:855-863`, messages `ct:745-776`; same codes in C#):

| Code | Where (ct) | nodeId / inputId | Message |
|---|---|---|---|
| `definition.invalid` | 1331, 1336, 1339, 1341 | null | `definitionUnreadable`, `repeatedKey`, `unsupportedDomain`, `unsupportedSettingType` |
| `node.document` | 1203 | node | "<step> only works in the graph of an entity definition; delete it from this automation" / "…only works in an automation…" |
| `node.singleton` | 1357 | node | "A graph can have only one <label> step; delete this one" |
| `root.incoming` | 1031 | target | "<step> starts a flow of its own, so no step order wire can lead into it; remove this wire" |
| `definition.unknownEntity` | 1208 | node / `entity` | "Choose one of this definition's entities in <label>" / "This definition has no entity <key>; …" |
| `definition.unknownCommand` | 1213 | node / `command` | "Entity <key> takes no commands; …" / "Choose <commands> in <label>" / "Entity <key> has no command <c>; choose …" |
| `setting.unknown` | 1217 | node / `key` | "Choose one of this definition's settings in <label>" / "This definition has no setting <key>; …" |
| `state.duplicate` | 1362 | node / `entity` | "Entity <key> already has a State step; …" |
| `command.duplicate` | 1365 | node / `command` | "Entity <key> already has a When commanded step for <c>; …" |
| `state.dynamicSource` (warning) | 1381 | reader node | "<reader> gets its entity over a wire, so <state> is not worked out again…" |
| `command.unhandled` (warning) | 1387 | **null** | "<Domain> <key> does nothing on <command>; add a When commanded step for it" |

Server-only document codes (nodeId null, `InputId` = the entity's or setting's key, `DefinitionGenerator.cs:46`): `definition.name`, `definition.logic`, `definition.entitiesNotAllowed`, `definition.graph`, `definition.entitiesRequired`, `definition.entityKey`, `definition.duplicateEntity`, `definition.entityField`, `definition.entityDomain`, `definition.entityOption`, `definition.settingKey`, `definition.settingType`, `definition.duplicateSetting`, `definition.library`, `definition.unknownType`, `definition.unknownSetting`. Plus `entity.unknown` warnings for entity ids HA lacks (`VisualApi.cs:208-267`, from preview and save).

## 7. Runs and traces

- `components/AutomationRuns.tsx` (`AutomationRuns({automationId})`, l.27-36; admins only): `useQuery(['run-history', id, offset, status])` → `hass_sharp/run_history` page 20 (l.57-68), polls via `runHistoryInterval` (`runDispatch.ts:35`), status filter (l.13, 82-85), pagination (l.99-104). Row (l.110-154): status, `actionLabel ?? action`, cause label from `CAUSES` (l.14-16: `HomeAssistantTrigger`, `Entity`, `Schedule`, `Manual`, `Startup` only; others raw), trigger one-liner `summarizeTrigger`, error first line, relative time, duration; link **`/visual/<visualId>/runs/<runId>`** "View trace"/"View run" (l.131-133); body: error, `TriggerSummary`, Run ID, "Graph revision" (`visualRevision`), "Automation revision", times, raw JSON.
- `components/TriggerSummary.tsx:12-25` `summarizeTrigger(kind)`: `entity`, `ha`, `schedule`, `manual`, `startup`; default → `description` or **"Trigger"**. Fields list l.27-30 (`platform, entityId, from, to, eventType, scheduleId, planned, id, description`). `RunTrigger.kind` type (`types.ts:382-393`) lacks `command`/`setup` and `entity`/`command` fields.
- `views/RunTraceView.tsx` (`/visual/:id/runs/:runId`, l.28-105): `api.runTrace(runId)`, polled every 2 s while `live` (l.34-50); `api.read(visualId)` for "earlier version" (l.55-60, **visual API**); `api.services()`; `editorHref = /visual/<visualId>` and Back → `?drawer=runs` (l.69-71); renders `TracePage` (`visual/trace/TracePage.tsx:42-57`, props `response, currentRevision?, editorHref, onBack, paused?, retrying?, onRetry?, services?, onOpenLibrary?`), which reads `snapshot.graph` and `snapshot.catalog` (l.64-66).
- Run record (`types.ts:398-417` `AutomationRun`): `runId, automationId, revision, action, actionLabel, triggerNodeId, visualId, visualRevision, cause, trigger: RunTrigger|null, plannedUtc, receivedUtc, startUtc, endUtc, durationMs, status, error, hasTrace`. `actionLabel` = node label else method name (`Manager.cs:225`).
- **A definition's runs** (`EGT:674-707`): recorded causes `Command`, `Setup`, and trigger causes (`Entity`, `HomeAssistantTrigger`, …) only for triggers with `trackHistory: true`; States never. `visualId` = the definition id, `triggerNodeId` = the root's node id, `visualRevision` = document revision, `hasTrace` true. Trace `snapshot.graph.definition.entities` present (l.706). `InvocationCause { Startup, Entity, Manual, Schedule, HomeAssistantTrigger, Command, Setup }` (`HassSharp/UserScripts/AutomationConfiguration.cs:6`).
- Payloads (`HassSharp/Visual/TriggerPayload.cs:94-102`): `Command(entity, command, data)` → `{"platform":"command","entity":<key>,"command":<c>,"data"?:…}`; `Setup()` → `{"platform":"setup"}`. Summary (`HassSharp/History/TriggerSummary.cs:37-41,62-66`): `{"kind":"command","entity":"relay","command":"turn_on"}` (data left out) and `{"kind":"setup"}`.
- A command's `error` is `RunOutcome.Error`, the innermost message, no node name (phase 2 hand-off).

## 8. Node libraries in the panel

- `views/NodeLibraryView.tsx` (lazy route `/libraries`): reads `?file=<id>&line=<n>` once (l.16-22), opens that file at the line (l.66-70), else says the file is missing (l.79). `libraryHref(id, line?)` = `/libraries?file=<id>&line=<n>` (`library-api.ts:90`).
- Session `visual/library-session.ts:29-187` over `libraryApi`: `list`, `read`, debounced `preview` per workspace version, `save` (l.109-126: `api.save({id, name, source, revision})` → on success sets `generation`, `activations = result.automations`, `notRebuilt = result.invalid ?? []`, then `librariesChanged()` = window event `hass-sharp-libraries-changed`, `library-api.ts:91`), `remove`/`delete` alike (l.137-149).
- Save answer (`HassSharp/Visual/NodeLibraryApi.cs:282`): `{saved, document, deleted, generation, catalog, automations, invalid, definitions, invalidDefinitions}`; `definitions` = each rebuilt definition that is on: `{id, saved, document: EntityJson with secret values null, active, activationError}` (`Devices/EntityLifecycle.cs:174-184`, masking l.191-198, test `EGT:966-1004`); `invalidDefinitions` = `{id, name, diagnostics, running, message}` (l.153-160). Refused (`validation`) detail: `{diagnostics, automations, invalid, definitions: [{id, name, diagnostics}]}` (`NodeLibraryApi.cs:273`, `EntityLifecycle.cs:151`).
- TS ignores the entity parts: `LibraryMutation` (`library-api.ts:37`) has no `definitions`/`invalidDefinitions`; `LibraryValidationDetail` (l.88) no `definitions`; the refusal text (l.39-44) and `workspaceDiagnostics` (`library-session.ts:22-27`) name automations only.
- Linking a class definition to its source: `EntityTypeDescriptor.SourceId` = the library document id → `libraryHref(sourceId)`; no line is in the descriptor. `type_definition` (`library-api.ts:70`, `TypeDefinition {name, kind, text, origin: {library?, file?, nodeId?, assembly?}, line?}` l.24-33) gives a declaration's library and `line` for a type name.

## 9. Live state

- The panel reads no live HA states today: no `subscribeEntities`/`subscribe_entities`, no reads of `hass.states` outside `context.ts` comments (grep). `liveHass` is deliberately untracked (`context.ts:17-23`, test `ft/live-hass.test.tsx:11-26`). The signal of the latest `hass` exists only inside `HassSharpView` (`hass-sharp.ts:25,41,50`).
- The only websocket subscription is `trigger_platforms/subscribe` (`TriggerConfig.tsx:249-255`) through `hass.connection.subscribeMessage` — the pattern for a subscription with cleanup.
- Registry: `hass_sharp/get_entities` (`utils.py:63-95`) gives per entity `entity_id, name, domain, area_id, device_id, device_class, supported_features, unit, icon, disabled, state_options` — no `unique_id`, no `platform`, no state; cached with `staleTime: Infinity` (`registry.ts:56`).
- Entity API: `liveEntities[] {uniqueId, key, domain, name, entityId}` while loaded (`EA:47`); `status`, `active`, `activationError`, `currentRevisionActive`, `enabled`. No state value, no availability, no last-known entity ids when off or failed (`EAT:206`: a failed definition reads `liveEntities: []`).

## 10. Tests to model on

| Kind of test | Closest file | Setup |
|---|---|---|
| List view | `ft/AutomationsListView.test.tsx` (402 l.) | `setup()` l.20-41: `visualFixture()`, a `callWS` wrapper answering `get_entities` and holding chosen operations, `hass = {...fixture, user:{is_admin}, callWS, callApi}`, fresh `QueryClient({retry:false})`, `createMemoryHistory()`, `MemoryRouter` with stub routes; `saved()` l.46-56 seeds `fixture.documents` |
| Editor view | `ft/VisualAutomationView.test.tsx` (682 l., 45 tests) | `setup(saved, minimal, path)` l.12-26; helpers `button`, `edit`, `chip`, `saved(root, revision)`, `more(root, item)` l.27-40 |
| Editor shell / layout | `ft/editor-shell.test.tsx` (809 l.) | stubs `matchMedia` l.106, `narrowScreen` l.118 |
| Session logic | `ft/visual-session.test.ts` (720 l.) | `setup(handler)` l.16-21: `hass.callWS` answers the library catalog and forwards `request` to `handler`; `createRoot` + `createVisualSession`; `vi.useFakeTimers()` + `advanceTimersByTimeAsync(350)` for the 300 ms preview |
| Websocket API module | `ft/visual-session.test.ts:24-27` (error unwrap), `ft/library-session.test.ts` l.10-22 (`callWS` mock wrapping `{version:1, ok:true, result}`) | — |
| Nav | `ft/MainLayout.test.tsx` (l.8-15) | `MemoryRouter root={MainLayout}` in a shadow root |
| Palette / documents | `ft/palette.test.tsx` l.8-30 | `catalogPalette(nodeCatalog, 'entity')` |
| Node forms | `ft/NodeConfig.test.tsx` (1024 l.), `ft/TriggerConfig.test.tsx`, `ft/editor-inspector.test.tsx` | registry fake l.27-33 (`entity(id, name, state_options)`), `host()` with `.hs-app` |
| Runs | `ft/AutomationRuns.test.tsx` (387 l.) + `ft/run-fixture.ts` (`triggers`, `run(overrides)`, `page(runs)`) | `setup(callWS, admin)` l.16-28 |
| Trace view | `ft/RunTraceView.test.tsx` (616 l.) | stubs `matchMedia` l.431, 448 |
| Validator parity | `ft/visual-contract.test.ts` (shared fixture `tests/fixtures/visual-graph-validation.json`, 419 cases) | — |
| Live hass | `ft/live-hass.test.tsx` | `liveHass(signal)` |

Suite size: 58 test files, ~761 `it(` (grep); durations not measured (the brief forbids running the suites). Fixtures used: `tests/fixtures/visual-services.json`, `visual-rich-catalog.json`, `node-library-template.json`.

**Backend suites if API operations are added.**
- Runtime: `dotnet run --project tests/Runtime --disable-build-servers [flags]`; with no flag every `RunByDefault` suite runs (`tests/Runtime/Program.cs:30-36`); unknown flags are refused (l.15-18). Relevant flags: `--entity-api` (Order 14, `EAT:67`), `--entity-graph` (16, `EGT:18`), `--entity-store` (12), `--entity-types` (12), `--entity-startup` (15), `--entity-host` (13), `--entity-domains`, `--entity-domains-embedded`, `--entity-graph-order`, `--entity-graph-generator`, `--entity-graph-runtime` (11), `--entity-generator` (39), `--entity-schema` (38), `--visual-graph` (11, shared fixture), `--node-library` (10). `EAT` pattern: `Entity(request)` → `JsonNode.Parse(await api.EntityRequest(JsonSerializer.Serialize(request)))`, `Ok`, `Fails` (l.82-95), over `EntityTestHome`; `EGT` uses `Rig.Start(name, states)` with `Save/Read/Ok/Change/Until/History` (l.143-229). Reload-memory suite ~50 s (phase 2 plan l.359).
- Python: `python3 -m unittest discover -s tests/python`; websocket tests in `tests/python/test_visual.py` class `WebsocketReadinessTests` (l.100-206: `send(handler, hass, msg)` with a `SimpleNamespace` connection; `load_routes(manager, hass)`; entities route l.140-190; the internal-error table l.192-206 lists `('entities', 'entity_request_failed', 'Entity')`); `tests/python/test_entity_schema.py` also names `hass_sharp/entities`.

## 11. Where the design does not fit the code as it stands

Each item: evidence → suggestion.

**Backend changes the panel needs**
1. **`preview` answers no `sourceMap`.** `EA:220` `PreviewJson` = `{valid, diagnostics, source}` (pinned by `EAT:191`); `CodePanel` does `result().sourceMap.find(...)` (`BottomDrawer.tsx:248`) and `locate` reads `preview()?.sourceMap` (`VE:276`) → a TypeError in the Generated C# tab. → Add `sourceMap` to `PreviewJson` (and update `EAT:191`), or default it in the TS client.
2. **Secrets in clear.** `list`/`read`/`save` answer setting values as stored (`EGT:1003-1004`); only the library save masks (`EntityLifecycle.cs:191-198`). `null` already means "class default" / "no value" (`EntityDefinition.cs:30`, `DefinitionGenerator.cs:99-103`), so it cannot double as "unchanged". → Mask in `EntityJson` for every answer using `WithoutSecretValues` plus a flag such as `hasValue: true`; accept an explicit marker on save (`{"key":"Password","keep":true}`, or omitting `value` vs `null`) and substitute the stored value under the store lock in `EntityDefinitionStore.Save`. Tests in `EAT`/`EGT`.
3. **Key rename.** The unique id is `<definitionId>:<key>` (`EntityDirectory.cs:6`); `entity_bridge.py` has no rename (no `new_unique_id`); a key renamed through the API is a new entity whose id ends in `_2` (phase 2 plan l.399; the old unique id is dropped, `EGT:552-569`). → Phase 3 minimum: key read-only after the first save (UI only). Explicit rename needs a `renames: {old: new}` save field, a bridge step updating the registry's unique id before the sync, and Python tests.
4. **No `run`.** `EA:237` has no `run`; `Manager.RunNow` refuses definitions (`Manager.cs:240`); `RunNowButton` knows triggers only (`RunNow.tsx:30`). → Add `run {id, nodeId, data?}` dispatching a When started, a When commanded (through `EntityCommand`) or a manual trigger, answering `{runId}`; or leave Run now out of the entity editor in phase 3. Spec lists `run` (`docs/superpowers/specs/2026-11-17-custom-entities-design.md:212-213`).
5. **No list-level run summary.** `list` has no `recentRuns`/`runsToday`/`lastRunUtc`/`problemCount` (`EA:255-260` vs `VisualApi.cs:270-292`). → Add a `view: 'home'` variant reusing `_history.Recent` (`VisualApi.cs:277`) and `_loadProblems`, or show runs only in the editor.
6. **Entities of a class definition are unknown when it is off or failed.** `EntityTypeDescriptor` lists none (`VisualCatalogSnapshot.cs:72-73`); `liveEntities` is `[]` when not loaded (`EAT:206`); `get_entities` has no `unique_id`/`platform` (`utils.py:76-90`), so the panel cannot find them in the registry either. The spec's list ("device · 3 entities", "camera.driveway_faces streaming") needs them. → Answer `registeredEntities` (the registry's entries for `<id>:*`, from Python) or remember the last synced set in the host/store.
7. **No live state or availability in any answer.** `LiveEntity` has no state (`EntityDirectory.cs:11`); see 15 for the frontend side.
8. **Entity graphs skip native-trigger validation and the trigger list.** `authoring.py:38-55,57-63` only for `visual`. A `ha` trigger in a definition is saved unchecked. → Run `native_triggers.async_validate_graph_triggers` on `logic.graph` for `preview`/`save` in `authoring.entities` when `logic.kind == "graph"` (Python test beside `test_visual.py`).
9. **Carried T6 items the UI will meet:** a disabled definition's `document.activationError` is "Not active" while `set_enabled` answers `null` (`EA:45` vs `EA:288`); a failure after the store write answers `invalid_request` although stored (phase 2 plan l.308); `active` in a save answer is `currentRevisionActive` while `status` may be `failed` (`EA:129-132`). → Explain off via `enabled`, not `activationError`; reread after an `invalid_request` on save.
10. **Document errors hide graph diagnostics.** `DefinitionGenerator.cs:65-66` returns before the graph is generated; document codes have `nodeId: null` and `inputId` = an entity **or** setting key, told apart only by the code (`definition.entity*` vs `definition.setting*`). → The definition panel must route `definition.*` by code to its entity/setting row; the canvas shows only local graph diagnostics while document errors exist.

**Frontend: design vs code**
11. **The session's unit of change is the graph.** `dirty`, `baseline`, drafts (`VisualDraft.graph`), undo, `keepMine`, `saveCopy`, the conflict check all compare `serializeGraph` (`ses:54,69,261,308,323-325`; `drafts.ts:11`). A definition also edits name, entities, device, settings. → Either keep the whole body in the editor graph (entities and settings with their extra fields under `graph.definition`, which the TS validator accepts and the server strips, `ct:1328-1342`, `DefinitionGraph.cs:43-48`; device and name in a sibling key) so history/drafts/dirty work unchanged, then split into `DefinitionBody` on save; or generalise `createGraphHistory` to a document. The first is the smaller change; note graph `name` vs definition `name` (TopBar renames `graph.name`, `VE:455`).
12. **Compose/decompose in TS is missing** (phase 2 hand-off). `graphDocument` needs an object `definition` (`ct:193`); without it the graph is read as an automation (`graph.noTrigger`, `node.document` on every entity node). → `entities/model.ts` with pure `compose(body)`/`decompose(graph)` mirroring `DefinitionGraph.cs:31-48`, unit-tested against the shared fixture.
13. **Document kind is not a parameter anywhere.** `catalogPalette` callers (`NodeLibraryPanel.tsx:36`, `QuickAdd.tsx:38`, `FlowAdapter.tsx:122`, `graph.ts:49`) pass no document; → derive it with `graphDocument(props.graph)` inside them, so no new prop is needed.
14. **Root generalisation** (table in section 5): `graph.ts:144`, `flowModel.ts:245`, `autoConnect.ts:66,97`, `layout.ts:102,106`, `FlowAdapter.tsx:580`, `Inspector.tsx:218` → `isRoot(node, catalog)` (`ct:191`); `entity_state` has no execution at all, so it also needs no exec output.
15. **Live state needs a subscription.** No code subscribes; `liveHass` is untracked by design. → An `entities/live.ts` using `hass.connection.subscribeMessage({type: 'subscribe_entities', entity_ids})` (pattern: `TriggerConfig.tsx:249-255`), or expose the `_hass` signal from `hass-sharp.ts:25` through context; the list then needs the entity ids (item 6).
16. **History default.** `addGraphNode` copies `trackHistory: true` into a new trigger (`ct:1400`), and `TriggerConfig.tsx:472` shows `!== false`, so every trigger added in the panel is recorded on each tick, against `EA:95-99`. → `trackHistoryDefault(kind, document)` used by both, skipping the copy in entity graphs (phase 2 hand-off).
17. **Pickers for the entity nodes.** `entity`, `command`, `key` are free text (`NodeConfig.tsx:136-146`). → Add `kindConfig` entries (`NodeConfig.tsx:30`) and pickers built on `CatalogPicker` (items from `catalog.definition.entities`, `entityDomains[domain].commands`, `catalog.definition.settings`); a `setting` node's picker should show the setting's label.
18. **Run views are automation-bound.** Row link `/visual/<visualId>/runs/<runId>` (`AutomationRuns.tsx:131-133`); `RunTraceView` reads the visual document and links `/visual/<id>` (`RunTraceView.tsx:55-71`); `CAUSES` and `summarizeTrigger` lack `Command`/`Setup` (`AutomationRuns.tsx:14-16`, `TriggerSummary.tsx:12-25`, `types.ts:382-393`). → Give `AutomationRuns` a `runHref(run)` prop; reuse `TracePage` (it already takes `editorHref`/`onBack`) under `/entities/:id/runs/:runId`; add the two causes and the `entity`/`command` summary fields. Every When commanded run is labelled "When commanded" (`ct:1405`) → label new command nodes "When commanded ▸ <key> <command>" or show `trigger.command`.
19. **Library save results for definitions are dropped.** `library-api.ts:37,39-46,88`, `library-session.ts:121,146`. A library save also rebuilds an open definition, which the open entity editor must notice (the session rereads through the visual API on `hass-sharp-libraries-changed`, `ses:134,171`). → Type and show `definitions`/`invalidDefinitions`; the entity session rereads through the entity API.
20. **Class definition editor inputs.** Settings form from `types[].settings` (`string|boolean|integer|number|enum`, `minimum/maximum`, `secret`), which differ from graph setting types (`string|number|boolean`, `DefinitionDocument.cs:55`); the class's own device metadata is not in `types` (only document overrides). Library link `libraryHref(type.sourceId)` has no line (section 8); `type_definition` could supply one.
21. **Smaller mismatches.** "Entity" category has no identity (`icons.ts:41-58`, `tokens.css:38-43`); `command.unhandled` and other null-node problems land under "This automation" (`BottomDrawer.tsx:160`); `entity_is.state` has no state suggestions; `/devices` still renders `TODOView` (spec builder's call: redirect and delete it, design doc l.262); `createGraph` starter and `selectedId 'trigger-1'` (`VE:62,171`) need an entity starter; definition ids: `visual-<uuid>`-style ids fit the id pattern but are permanent parts of unique ids.

**Files too large or too automation-specific to parametrise cleanly** (estimate)

| File | Lines | Automation-specific points | Estimate |
|---|---|---|---|
| `visual/VisualEditor.tsx` | 668 | ~25 (section 4 table) + run/enable/history wiring | Parametrise with a `kind` adapter (API, labels, routes, run group, runs tab) **or** extract the shell (canvas+inspector+drawer+keys+fullscreen, ~350 l.) and keep two thin pages; adding branches in place would push it past 800 l. |
| `visual/session.ts` | 576 | 8 API calls, `VisualDocument`, graph-only baseline | Accept an `api` object `{catalog, read, preview, save}` + `toGraph(document)`; feasible if item 11's "body inside the graph" approach is taken |
| `visual/flow/FlowAdapter.tsx` | 1387 | 2 (l.122, 580) | Too large to touch broadly; only the two lines |
| `visual/contract.ts` | 1457 | none new | Add compose/trackHistory helpers elsewhere (`entities/model.ts`) |
| `visual/NodeConfig.tsx` | 340 | trigger branches only | Small additions (`kindConfig`, pickers) |
| `visual/TriggerConfig.tsx` | 513 | `trackHistory` l.472 | One-line change |
| `visual/Inspector.tsx` | 353 | Overview l.216-250 | Overview needs a definition variant (entities, roots) |
| `components/AutomationRuns.tsx`, `views/RunTraceView.tsx` | 171, 105 | links, causes, `api.read` | Props for href/read; reuse |
| `visual/RunNow.tsx`, `automations/enabled.ts`, `HelpDialog.tsx` | 179, 29, ~25 | wholly automation | Entity variants rather than parameters |
