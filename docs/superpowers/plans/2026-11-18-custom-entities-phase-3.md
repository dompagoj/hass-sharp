# Custom entities, phase 3: the Entities section of the panel

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Every implementer and reviewer runs on Opus 5.5.

**Goal:** A user defines entities and devices in the panel, with nodes or with a class from the node library, without touching code or the API.

**Architecture:** The visual editor is parametrised by an *adapter* (API, document kind, texts, routes); the automation adapter reproduces today's behaviour exactly. An entity definition with graph logic is edited as one *editor graph*: the graph plus the whole definition (entities, settings, device) under `graph.definition`, so the session's dirty tracking, undo, drafts and conflict handling work unchanged; it is split back into a definition body on save. A class definition has a small form editor of its own. The backend gains only what the panel needs: masked secrets with a keep marker, a source map in previews, a run summary in the list, registry entries per definition, and trigger validation.

**Tech Stack:** SolidJS, `@solidjs/router`, TanStack Query, Vitest + happy-dom (frontend); C# (.NET 11) for the entity API; Python for `authoring.py`.

**Spec:** `docs/superpowers/specs/2026-11-17-custom-entities-design.md` (sections "Stored document", "Frontend"). **Code map:** `docs/superpowers/plans/2026-11-18-custom-entities-phase-3-map.md` — every task reads the sections its dispatch names. Phase 2's hand-off: the last section of `docs/superpowers/plans/2026-11-17-custom-entities-phase-2.md`.

## Global Constraints

- Paths are under `custom_components/hass_sharp/` unless they start with `tests/` or `docs/`; `fe` = `frontend/src/`, `ft` = `frontend/tests/`.
- The automation editor, list and run views behave exactly as before: every existing frontend test passes **unmodified**, except tests a task names. Existing automations' generated source stays byte-identical (`tests/fixtures/generated-source-pins.json` is never edited). Both validators keep agreeing on `tests/fixtures/visual-graph-validation.json`.
- A secret setting's value is never sent to the panel, never written to a `localStorage` draft, a log, a diagnostic or a test snapshot. The stored document keeps it.
- The stored document never holds `definition` inside `logic.graph`; the editor graph (with `definition`) exists only in the browser and in requests' `definition` body after it is split.
- An entity's key is permanent once the definition has been saved (unique id `<definitionId>:<entityKey>`); the definition id is permanent from the first save.
- Entity keys match `^[a-z][a-z0-9_]{0,39}$`; definition ids `^[a-z0-9][a-z0-9_-]{0,79}$`; a definition's name is 1–120 characters.
- Run history defaults in a definition: State off, When commanded and When started on, a trigger off (`EntityApi.TrackHistory`); the panel shows and writes the same defaults.
- User-facing text: plain sentences, sentence case, no internal names (no "definition document", "root", "host"); the noun is "entity definition" where one is needed, "entities" for the section. Follow the wording of neighbouring automation screens.
- CSS: rules scoped under `.hs-app`, colours from `fe/styles/tokens.css` only, both colour schemes, narrow layout at `max-width: 700px` (699 px where the editor shell uses it). Keyboard and labels as the automation screens do them (every control has an accessible name).
- No new runtime dependencies. Only strings cross the Python/.NET boundary.
- Suites, from the repository root: `dotnet run --project tests/Runtime --disable-build-servers` (388 PASS lines before this phase, exit 0); `dotnet test custom_components/hass_sharp/HassSharp.Editor.Tests` (22); `PYTHONPATH=tests/python python3 -m unittest discover -s tests/python` (152 OK, 41 skipped on the host); `python3 -m unittest discover -s tests` (5); `(cd custom_components/hass_sharp/frontend && bun run test)` (1366) and `bun run build` (type check).
- Git: plain `git diff` uses an external tool here — pass `--no-ext-diff`. Never stage `docs/superpowers/specs/2026-11-15-mcp-authoring-guide-design.md`, `out/`, `dotnet/` or `www/` build output that is git-ignored. Commit on `custom-entities`; no push.
- Containers: the user's `hass` container may only be restarted (once per publish or frontend build), have its logs read, and run the smokes and the Python suite through `docker exec`; no API calls to it, nothing read or written under its `/config` except the bind-mounted integration directory. All live checks use a throwaway `hass_wt` container on port 5002. Never use `mcp__hass__*` tools.

## Decisions taken for this phase

1. **One editor graph.** `editorGraph(body)` = the logic graph with `name = body.name` and `definition = { entities, settings, device }` carrying every field of each (not only key/domain/type). `definitionBody(graph)` is its inverse. If the TypeScript validator refuses the extra `device` member, it is kept in a sibling graph key `definitionDevice`; the implementer of Task 4 settles it with a test.
2. **Secrets.** Answers mask a secret setting as `value: null` plus `hasValue`; a save sends `keep: true` for a secret the user did not retype. Drafts drop secret values.
3. **No Run now and no `run` operation for definitions in this phase.** Commands are tried from Home Assistant; the editor links to each entity. `run` moves to phase 6 with the MCP tools.
4. **Keys are read-only after the first save;** before it, changing a key in the panel renames the references in the graph's nodes. An explicit rename of a saved key is not built.
5. **Definition ids come from the name** (`Any motion` → `any_motion`, then `any_motion_2`…), fixed at the first save.
6. **A new graph definition starts with one binary sensor named after the definition and no steps.**
7. **Entities of a definition that is off or failed** come from the entity registry (`registeredEntities`, added by Python), not from the host.
8. **The class editor edits name, class, settings and device details only**; it has no canvas, no runs tab (a class has no recorded runs) and links to the class's source in the node library.
9. `via` (parent device) has no form in this phase; a stored `via` is preserved untouched.

## Review Focus

1. A definition changed elsewhere while it is open with unsaved changes (a node library save rebuilds it; a second tab saves it): the conflict dialog appears and keep-mine, save-a-copy and discard all work for definitions. — Task 6.
2. A secret setting through a full round trip: open, change something else, save → the stored secret is unchanged; retype → replaced; clear → removed; restore a draft → no secret in `localStorage`. — Tasks 1, 4, 7.
3. A definition that is off, failed in setup, or invalid after a library change: the list and the editor show which, with the error text, and still list its entities. — Tasks 2, 8, 9.
4. Removing an entity, or changing its type, while steps still use it: the steps show a problem that names the entity; nothing crashes; the save is refused until fixed. — Task 7.
5. A user who is not an administrator, and a backend that is not ready: the section shows the same messages the automation screens show, not a blank page. — Task 9.

---

### Task 1: Entity API for the panel (C#)

**Files:**
- Modify: `HassSharp/Devices/EntityApi.cs`, `HassSharp/Devices/EntityLifecycle.cs` (`WithoutSecretValues`), `HassSharp/Devices/EntityDefinition.cs` (store: keep marker), `HassSharp/Devices/DefinitionGenerator.cs` / `DefinitionDocument.cs` (preview accepts `keep`)
- Test: `tests/Runtime/EntityApiTests.cs`, `tests/Runtime/EntityGraphTests.cs`, `tests/Runtime/EntityStoreTests.cs`

**Interfaces:**
- Produces:
  - `preview` answers `{ valid, diagnostics, source, sourceMap }` (`sourceMap` as `read` gives it; `[]` when invalid).
  - Every document in a `list`, `read` or `save` answer masks secret settings: `{ "key", …, "secret": true, "value": null, "hasValue": true|false }` (`hasValue` only on secret settings). A class definition's secrets are known from its type's settings; a graph definition's from the document. The library answers (phase 2) use the same shape.
  - `save` and `preview` accept a setting `{ "key": "<key>", "keep": true }` (no `value`): `save` stores the value the stored document has for that key (none → no value); `preview` checks nothing about its value. `keep` and `hasValue` are never stored.
  - `list` with `"view": "home"` and `"todayUtc": "<ISO instant>"`: each entry also has `recentRuns` (at most 10, newest last), `runsToday`, `failedLastDay`, `lastRunUtc`, with the shapes the automation home list uses (`Visual/VisualApi.cs`, the `home` view).

- [ ] **Step 1: Write the failing tests:** `PreviewAnswersASourceMap`; `SecretSettingsAreMaskedInEveryAnswer` (class and graph definition; `list`, `read`, `save`; a non-secret value is still answered; the stored file still holds the secret); `ASaveKeepsASecretItWasNotGiven` (`keep: true` → stored value unchanged; a new value replaces it; `value: null` without `keep` removes it; `keep` for a key with nothing stored stores no value); `KeepIsNeverStored`; `ThePreviewAcceptsAKeptSecret`; `TheHomeListSummarisesRuns` (a definition with two command runs, one failed: counts and order; a definition without runs: empty list, zeros, null).
- [ ] **Step 2: Run** `dotnet run --project tests/Runtime --disable-build-servers -- --entity-api --entity-graph --entity-store`. Expected: the new tests FAIL.
- [ ] **Step 3: Implement.** Mask in the one place every answer's document is built (`EntityJson`), reusing `WithoutSecretValues`; resolve `keep` under the store's lock, before the document is validated and written.
- [ ] **Step 4: Run** the full runtime suite. Expected: PASS, exit 0.
- [ ] **Step 5: Commit** `Mask secret settings, map previews to their source and summarise runs in the entity API`.

### Task 2: Registry entries and trigger checks for definitions (Python)

**Files:**
- Modify: `authoring.py` (`entities()`), `utils.py` only if a registry helper belongs there
- Test: `tests/python/test_entity_authoring.py` (new; model on `tests/python/test_visual.py`)

**Interfaces:**
- Produces:
  - For `preview` and `save` with `definition.logic.kind == "graph"`: the graph's Home Assistant triggers are validated by `native_triggers.async_validate_graph_triggers` and merged exactly as `visual()` does for automations (same diagnostics shape; an error refuses the save with the `validation` form).
  - Every document in a `list`, `read` or `save` answer gains `registeredEntities`: the entity registry's entries of this config entry whose unique id starts with `<definition id>:`, as `[{ "uniqueId", "entityId", "domain", "name", "disabled" }]` sorted by `uniqueId` (`name` = the registry's name or original name; `disabled` = disabled by user or integration). `[]` when there are none.

- [ ] **Step 1: Write the failing tests:** `test_graph_definition_triggers_are_validated_on_preview_and_save` (an invalid `ha` trigger config yields the diagnostic; a class definition is passed through untouched), `test_documents_list_their_registered_entities` (list, read, save; another definition's and another platform's entries are left out; a disabled entry says so), `test_other_operations_are_passed_through` (`types`, `set_enabled`, `delete`, error answers).
- [ ] **Step 2: Run** `PYTHONPATH=tests/python python3 -m unittest tests.python.test_entity_authoring` (and in the container if the tests need Home Assistant, as `AGENTS.md` describes). Expected: FAIL.
- [ ] **Step 3: Implement** in `authoring.entities()`, sharing the trigger-merge code with `visual()` rather than copying it.
- [ ] **Step 4: Run** the Python suite on the host and in the container. Expected: OK; none skipped in the container.
- [ ] **Step 5: Commit** `List a definition's registered entities and validate its triggers`.

### Task 3: Roots, palette and history defaults by document (frontend)

**Files:**
- Modify: `fe/visual/contract.ts` (`trackHistoryDefault`, `addGraphNode`), `fe/visual/graph.ts` (`canConnect`), `fe/visual/flow/flowModel.ts`, `fe/visual/flow/autoConnect.ts`, `fe/visual/layout.ts`, `fe/visual/flow/FlowAdapter.tsx` (l.122, l.580 only), `fe/visual/NodeLibraryPanel.tsx`, `fe/visual/QuickAdd.tsx`, `fe/visual/TriggerConfig.tsx`, `fe/visual/NodeConfig.tsx` (pass the document)
- Test: `ft/palette.test.tsx`, `ft/visual-contract.test.ts`, `ft/TriggerConfig.test.tsx`, new `ft/entity-roots.test.ts`

**Interfaces:**
- Consumes: `isRoot`, `graphDocument`, `catalogPalette(catalog, document)` (exist).
- Produces: `export function trackHistoryDefault(kind: string, document: GraphDocument, catalog: VisualCatalogSnapshot): boolean` — a `trigger` gives `document === 'automation'`; another kind gives its spec's `trackHistory` default, else `true`. `addGraphNode` writes that value for a kind that has `trackHistory`. The palette, quick add and canvas use `graphDocument(graph)`.

- [ ] **Step 1: Write the failing tests:** in an entity graph — no step-order wire can be drawn into `entity_command`, `entity_setup` or `entity_state` (`canConnect` false); those nodes have no execution input pin; auto-connect does not wire into them and treats steps after them as running; layout puts them at the head of their flows; a new trigger gets `trackHistory: false` and `TriggerConfig` shows it off, while in an automation both stay on; the palette and quick add of an entity graph offer the entity kinds and the automation's do not. Existing automation cases unchanged.
- [ ] **Step 2: Run** `(cd custom_components/hass_sharp/frontend && bun run test)`. Expected: the new tests FAIL.
- [ ] **Step 3: Implement** with `isRoot(node, catalog)` at the places the code map's section 5 lists.
- [ ] **Step 4: Run** the frontend suite and `bun run build`. Expected: PASS.
- [ ] **Step 5: Commit** `Treat every root as a flow's head and default run history by document`.

### Task 4: Definition model and API client (frontend)

**Files:**
- Create: `fe/entities/model.ts`, `fe/entities/api.ts`
- Test: `ft/entity-model.test.ts`, `ft/entity-api.test.ts`; extend `ft/visual-fixture.ts` with an in-memory `hass_sharp/entities` backend (`types`, `list` + `home`, `read`, `preview`, `save`, `set_enabled`, `delete`; revisions, conflicts, masking and `keep` as Task 1 defines them)

**Interfaces:**
- Produces (`model.ts`):
  - Types `DefinitionEntity`, `DefinitionSetting` (`key, value?, label?, type?, secret?, hasValue?, keep?`), `DefinitionDevice`, `DefinitionLogic` (`{kind:'graph', graph}` | `{kind:'class', type}`), `DefinitionBody`, `LiveEntity`, `RegisteredEntity`, `EntityDocument` (the body plus `version, id, revision, enabled, source, sourceMap, savedUtc?, catalogGeneration?, automationId, active, currentRevisionActive, activationError, status: 'running'|'setup'|'failed'|'off', liveEntities, registeredEntities`), `HomeDefinition` (a document without `source`/`sourceMap`, plus the run summary), `EntityType`, `EntityTypeSetting` (as the `types` answer).
  - `editorGraph(body: DefinitionBody): Graph`, `definitionBody(graph: Graph): DefinitionBody` (a masked secret the user did not change becomes `{key, keep: true}` with its `label`/`type`/`secret`), `draftGraph(graph: Graph): Graph` (secret values removed), `newGraphDefinition(name: string): DefinitionBody` (decision 6), `definitionId(name: string, taken: string[]): string` (decision 5), `renameEntityKey(graph: Graph, from: string, to: string): Graph` (the entity and every node's `config.entity`).
- Produces (`api.ts`): `entityApi(hass)` with `types()`, `list()`, `home(todayUtc)`, `read(id)`, `preview(body)`, `save(id, body, expectedRevision, enabled?)`, `setEnabled(id, enabled)`, `remove(id, expectedRevision)`; errors thrown as `EntityApiError(code, detail)` with the message rules of `VisualApiError`.

- [ ] **Step 1: Write the failing tests:** round trips (`definitionBody(editorGraph(body))` equals `body` for the derived motion sensor, the plug device with settings, and a body with a stored `via`); the editor graph of the shared fixture's entity cases validates with exactly the diagnostics the fixture expects (decision 1's `device` question is settled here); `draftGraph` holds no secret value; a masked secret becomes `keep`; a retyped one carries its value; `definitionId` slugs, truncates to 80 and avoids taken ids; `renameEntityKey`; the API client's requests and its error unwrapping for `validation`, `conflict`, `not_found` and Home Assistant's own error form.
- [ ] **Step 2: Run** the frontend suite. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the frontend suite and `bun run build`. Expected: PASS.
- [ ] **Step 5: Commit** `Add the entity definition model and API client`.

### Task 5: Editor adapter (frontend)

**Files:**
- Create: `fe/visual/adapter.ts`
- Modify: `fe/visual/session.ts`, `fe/visual/VisualEditor.tsx`, `fe/visual/TopBar.tsx`, `fe/visual/Inspector.tsx`, `fe/visual/BottomDrawer.tsx`, `fe/visual/drafts.ts`, `fe/components/AutomationRuns.tsx` (`runHref`), `fe/views/VisualAutomationView.tsx`
- Test: `ft/editor-adapter.test.tsx`; the existing editor, session and view tests must pass unmodified

**Interfaces:**
- Produces:
  - `EditorDocument` — what the editor needs of a saved document: `id, revision, graph, source, sourceMap, automationId, enabled?, active?, currentRevisionActive?, activationError?, catalogGeneration?, savedUtc?`.
  - `EditorAdapter` — `document: GraphDocument`; `read(id)`, `preview(graph)`, `save(id, graph, expectedRevision, options?)`, `setEnabled(id, enabled)`; `newId(graph)`, `starter(name)`, `initialSelection?`; `draft?(graph): Graph`; `listHref`, `editorHref(id)`, `runHref(id, runId)`; `queryKeys: string[][]` to invalidate after a save; `texts` (every automation-worded string of the code map's section 4 table); `runGroup?(context): JSX` (the top bar's run controls) and `overview?(context): JSX` (the inspector's content when nothing is selected).
  - `automationAdapter(hass): EditorAdapter` — today's API calls, ids (`visual-<uuid>`), starter graph, selection, texts, routes, `RunNowButton` and Overview, unchanged.
  - `createVisualSession(hass, initial, options)` and `VisualEditor` take `adapter` (default: the automation adapter). `AutomationRuns` takes `runHref?: (run) => string`.

- [ ] **Step 1: Write the failing test** `ft/editor-adapter.test.tsx`: a stub adapter with its own texts, ids and routes drives a save, a reload, a conflict (keep mine, save a copy, discard), a draft restore (through `draft`) and the runs tab's links; none of the automation's words or routes appear.
- [ ] **Step 2: Run** the frontend suite. Expected: the new test FAILS.
- [ ] **Step 3: Implement.** Move literals into `automationAdapter`; no behaviour change. Keep `VisualEditor.tsx` from growing: what the adapter replaces leaves the file.
- [ ] **Step 4: Run** the frontend suite and `bun run build`. Expected: PASS, with no existing test edited.
- [ ] **Step 5: Commit** `Drive the visual editor through an adapter`.

### Task 6: Entity nodes in the editor (frontend)

**Files:**
- Modify: `fe/visual/NodeConfig.tsx`, `fe/visual/nodeSummary.ts`, `fe/visual/icons.ts`, `fe/styles/tokens.css` (`--hs-cat-entity`), `fe/visual/contract.ts` (`addGraphNode` label for When commanded only if needed)
- Create: `fe/entities/NodePickers.tsx`
- Test: `ft/entity-node-config.test.tsx`, `ft/node-summary.test.ts` (extend)

**Interfaces:**
- Consumes: `catalog.definition` (lifted by `withGraphTypes`), `entityDomains`.
- Produces: in `NodeConfig`, `config.entity` of `entity_state`, `entity_command`, `set_entity_state`, `set_entity_available` is a picker of the definition's entities (shown as name and type; `entity_command` lists only entities that take commands; `entity_state`/`set_entity_state` re-shape their inputs when the entity changes); `config.command` a picker of the chosen entity's commands by label; `setting.key` a picker of the definition's settings by label. Node summaries: `State ▸ <entity name>`, `When commanded ▸ <entity name>: <command label>`, `Set state ▸ <entity name>`, `Set available ▸ <entity name>`, `Setting ▸ <label>`, `Has state ▸ <entity id> is <state>`, `Current state ▸ <entity id>`. The "Entity" category has an icon and colour. `entity_is.state` offers the chosen entity's known states as `condition.state` does.

- [ ] **Step 1: Write the failing tests** for each picker (options, choosing, an entity that no longer exists shown as missing, keyboard), the summaries, and the category identity.
- [ ] **Step 2: Run** the frontend suite. Expected: FAIL.
- [ ] **Step 3: Implement** on `CatalogPicker`/`SearchSelect` as the existing pickers are.
- [ ] **Step 4: Run** the frontend suite and `bun run build`. Expected: PASS.
- [ ] **Step 5: Commit** `Pick a definition's entities, commands and settings in its steps`.

### Task 7: The definition panel and the graph definition editor (frontend)

**Files:**
- Create: `fe/entities/DefinitionPanel.tsx`, `fe/entities/adapter.ts` (`entityAdapter(hass)`), `fe/views/EntityDefinitionView.tsx`, `fe/styles/entities.css`, `fe/entities/EntityHelp.tsx`
- Modify: `fe/App.tsx` (route `/entities/:id`), `fe/style.css`
- Test: `ft/DefinitionPanel.test.tsx`, `ft/EntityDefinitionView.test.tsx`

**Interfaces:**
- Consumes: Task 4's model and client, Task 5's adapter, Task 6's pickers.
- Produces:
  - `entityAdapter(hass): EditorAdapter` — `document: 'entity'`; `read` → `editorGraph` of the document; `preview`/`save` split with `definitionBody`; `newId` = `definitionId(graph.name, taken ids)`; `starter` = `editorGraph(newGraphDefinition(name))`; `draft` = `draftGraph`; routes `/entities`, `/entities/<id>`, `/entities/<id>/runs/<runId>`; texts for entity definitions; `overview` = `DefinitionPanel`; no `runGroup`.
  - `DefinitionPanel({ graph, catalog, diagnostics, saved, commit, liveEntities })` edits `graph.definition` through `commit`: **Entities** (add, remove with a warning when steps use it, key — read-only when `saved` —, type, name, device class, icon, category, enabled by default, the type's options; a saved definition's rows link to the entity in Home Assistant), **Device** (a switch "Group these entities as a device", then manufacturer, model, software and hardware version, configuration URL, suggested area), **Settings** (key, label, type, value, secret; a masked secret shows "Saved; type to replace" and can be cleared). Problems with codes `definition.entity*` and `definition.setting*` are shown on their rows by `inputId`; other definition-level problems at the top.
  - `/entities/new?logic=graph` opens a new definition; `/entities/:id` opens a saved one; a class definition is handed to Task 8's editor.

- [ ] **Step 1: Write the failing tests.** Panel: each form edits the graph and can be undone; changing a key before the first save renames the steps' references; a saved key is read-only; removing an entity used by a step leaves a problem naming it; a document error lands on its row; a secret is never rendered and its three paths (untouched, replaced, cleared) produce `keep`, the value, and no value. View: building the derived motion sensor from an empty definition entirely through the UI (add nothing but steps and wires; save; the request body equals the phase 2 example); the plug device (three entities, device details, command flows); reload shows what was saved; the enabled switch; delete with a confirmation that says its entities are removed from Home Assistant; the conflict dialog after a change elsewhere; a draft restore without the secret; narrow layout opens the panel as the inspector sheet.
- [ ] **Step 2: Run** the frontend suite. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the frontend suite and `bun run build`. Expected: PASS.
- [ ] **Step 5: Commit** `Edit an entity definition's entities, device, settings and steps in the panel`.

### Task 8: Class definition editor (frontend)

**Files:**
- Create: `fe/entities/ClassDefinitionEditor.tsx`, `fe/entities/classSession.ts`
- Modify: `fe/views/EntityDefinitionView.tsx`
- Test: `ft/ClassDefinitionEditor.test.tsx`

**Interfaces:**
- Consumes: `entityApi.types()`, `read`, `preview`, `save`, `setEnabled`, `remove`; `libraryHref(sourceId)`.
- Produces: `/entities/new?logic=class` and a saved class definition render `ClassDefinitionEditor`: name; a class picker over `types` (title, description, device or single entity; with none, an explanation and a link to the node library); a settings form built from the type's settings (`string`, `boolean`, `integer` with its range, `number`, `enum`; a secret as in Task 7; an empty field keeps the class's default); device details as in Task 7; the definition's entities with their Home Assistant ids (`liveEntities`, else `registeredEntities`); status and the activation error; Save, enabled switch, Delete; "Open the class in the node library". Dirty tracking, the unsaved-changes guard, save conflicts (reload or overwrite) and server diagnostics per setting (`definition.settingType`, `definition.unknownSetting` by `inputId`) are handled by `classSession`.

- [ ] **Step 1: Write the failing tests:** create a definition from a class with two settings; a second definition of the same class with other settings; an out-of-range integer shows the server's message on its field; a secret round trip; the class no longer exists (`definition.unknownType`) is explained with a way to pick another; a conflict; leaving with unsaved changes asks.
- [ ] **Step 2: Run** the frontend suite. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the frontend suite and `bun run build`. Expected: PASS.
- [ ] **Step 5: Commit** `Create entity definitions from a node library class`.

### Task 9: The Entities list, navigation and live state (frontend)

**Files:**
- Create: `fe/views/EntitiesListView.tsx`, `fe/entities/DefinitionCard.tsx`, `fe/entities/liveState.ts`
- Modify: `fe/layout/MainLayout.tsx` (nav link "Entities" between "Automations" and "Node library"), `fe/App.tsx` (`/entities` → the list; `/devices` → redirect to `/entities`), `fe/styles/entities.css`; delete `fe/views/TODOView.tsx`
- Test: `ft/EntitiesListView.test.tsx`, `ft/MainLayout.test.tsx` (extend), `ft/entity-live-state.test.ts`

**Interfaces:**
- Consumes: `entityApi.home`, `setEnabled`, `remove`; `RunDots`, `EnabledSwitch`.
- Produces:
  - `liveState(hass, entityIds: () => string[])` → an accessor of `{ [entityId]: { state: string, available: boolean } }`, fed by one `subscribe_entities` subscription through `hass.connection.subscribeMessage`, resubscribed when the ids change and closed on cleanup.
  - The list: a card per definition — name, "Nodes" or the class's title, a device badge, status (Running, Starting, Failed with the error, Off), the enabled switch, each entity (name, type, Home Assistant id, live state or "Unavailable"; from `liveEntities`, else `registeredEntities`), run dots and "N runs today" for graph definitions, a menu with Open and Delete. "New entity definition" offers "Build with nodes" and "Use a class from the node library". Empty, loading, error, not-administrator and not-ready states as the automations list has them. The list refreshes as the automations list does.

- [ ] **Step 1: Write the failing tests:** the cards for a running graph definition, a failed class definition (entities from the registry) and one that is off; live state changing through the subscription and the unsubscribe on leave; turning one off and on; delete after confirmation; both "new" choices navigate; the nav link is active under `/entities/...`; `/devices` redirects; the non-admin and not-ready messages; narrow layout.
- [ ] **Step 2: Run** the frontend suite. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the frontend suite and `bun run build`. Expected: PASS.
- [ ] **Step 5: Commit** `Add the Entities section with its list and live states`.

### Task 10: Runs of a definition, and library saves that rebuild definitions (frontend)

**Files:**
- Modify: `fe/components/AutomationRuns.tsx` (`CAUSES`), `fe/components/TriggerSummary.tsx`, `fe/types.ts` (`RunTrigger`), `fe/views/RunTraceView.tsx` (adapter: how the current document is read, `editorHref`, back), `fe/App.tsx` (route `/entities/:id/runs/:runId`), `fe/visual/library-api.ts`, `fe/visual/library-session.ts`, the library view where a save's results are shown, `fe/visual/session.ts` only if the reread on a library change is not already through the adapter
- Test: `ft/AutomationRuns.test.tsx`, `ft/RunTraceView.test.tsx`, `ft/library-session.test.ts` (extend), `ft/EntityDefinitionView.test.tsx` (runs tab)

**Interfaces:**
- Consumes: Task 5's `runHref`/adapter; run records as the code map's section 7 gives them.
- Produces: causes `Command` → "Command" and `Setup` → "Started"; a command run's summary "<entity key>: <command>" from `trigger.entity`/`trigger.command`, a setup run's "Definition started"; the entity editor's Runs tab lists the definition's runs with links to `/entities/<id>/runs/<runId>`, whose trace page reads the definition through the entity API and returns to its editor. A node library save shows "N entity definitions rebuilt" beside the automations, lists the ones not rebuilt with their messages and links, and a refused save names the definitions that block it; an open entity editor rereads its definition after a library change exactly as an automation's does.

- [ ] **Step 1: Write the failing tests.**
- [ ] **Step 2: Run** the frontend suite. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the frontend suite and `bun run build`. Expected: PASS.
- [ ] **Step 5: Commit** `Show a definition's runs and what a library save rebuilt`.

### Task 11: Build, live check and suites

**Files:**
- Modify: `AGENTS.md` only if a command changes; `docs/` nothing (user docs are phase 6)

- [ ] **Step 1: Build and publish:** `dotnet publish …`, `bun run build`; restart `hass` once; clean log; both packaging smokes and the Python suite in the container.
- [ ] **Step 2: API regression in `hass_wt`** (script beside the earlier live checks in the scratchpad): masked secrets and `keep` over the websocket, `registeredEntities` for a definition that is off, a `home` list, an invalid trigger refused, phase 2's `livecheck3.py` still passing.
- [ ] **Step 3: Browser walk-through in `hass_wt`** (done by the coordinating session with the browser tools; the implementer prepares the instance and two `input_boolean` helpers): build the derived motion sensor and the plug device entirely in the panel; create two definitions from one class with different settings; flip the helpers and watch the list's live state; open a command run's trace; narrow width (≤ 700 px) and dark theme screenshots of the list, the editor and the class editor; the automation editor still opens and saves an automation.
- [ ] **Step 4: Run all five suites and `bun run build`.** Expected: all pass.
- [ ] **Step 5: Commit** anything the checks changed (`Check the Entities section against Home Assistant`), or nothing.

---

## Carried forward

Sections for phase 4, phase 6, the untied items and the test items stay in the phase 2 plan until their phase. Not taken by this phase, from the phase 3 lists: the `run` operation and Run now for definitions (→ phase 6); an explicit rename of a saved entity key (→ phase 6 docs say keys are permanent); `command.unhandled` naming the entity by name as well as key; a form for a parent device (`via`); `get_capabilities` carrying `entityDomains`; the entity API's duplicated `currentRevisionActive`/`activationError` formulas and its `invalid_request` after a completed store write.
