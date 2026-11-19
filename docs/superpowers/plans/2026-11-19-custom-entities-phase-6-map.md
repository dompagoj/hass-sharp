# Phase 6 code map (MCP tools and docs)

Repo `/Users/dompa/dev/HassSharp`, branch `custom-entities`, HEAD `725c768`. Paths are under
`custom_components/hass_sharp/` unless they start with `tests/` or `docs/`. Every line number was read at HEAD `725c768`.
Short names: `L` = `llm.py`, `A` = `authoring.py`, `WS` = `websocket.py`, `D` = `HassSharp/Devices/`, `EA` = `D/EntityApi.cs`,
`ED` = `D/EntityDefinition.cs`, `DD` = `D/DefinitionDocument.cs`, `DG` = `D/DefinitionGenerator.cs`, `DGr` = `D/DefinitionGraph.cs`,
`EL` = `D/EntityLifecycle.cs`, `M` = `HassSharp/Manager.cs`, `VA` = `HassSharp/Visual/VisualApi.cs`, `NLA` = `HassSharp/Visual/NodeLibraryApi.cs`,
`cat` = `visual_node_catalog.json`, `ex` = `authoring_examples.json`, `fe` = `frontend/src/`, `ft` = `frontend/tests/`,
`tp` = `tests/python/`, `tr` = `tests/Runtime/`, design = `docs/superpowers/specs/2026-11-17-custom-entities-design.md`,
P2..P5 = `docs/superpowers/plans/2026-11-1{7,8}-custom-entities-phase-{2,3,4,5}.md`, guide-design =
`docs/superpowers/specs/2026-11-15-mcp-authoring-guide-design.md` (untracked, the user's).

What the design asks of phase 6: "`llm.py`: `entities_*` MCP tools" (design:234); `EntityRequest(json)` "(list, read,
preview, save, delete, set_enabled, types, run)" (design:220); phase list "6. **MCP tools and docs.**" (design:257);
every phase ends with all `AGENTS.md` suites and a live check in the local container (design:246).

## 1. The existing MCP surface (`L`, 408 lines)

**Registration.** `HassSharpAPI(llm.API)` id `hass_sharp`, `requires_admin=True` (L:383-386); `async_get_api_instance`
never raises: non-admin or not ready gives `UNAVAILABLE_PROMPT` and no tools (L:388-400, L:113), else `PROMPT` and one
`HassSharpTool` per `SPECS` key (L:401-402). `register` (L:405-408) is called once ready; `EntryState` (L:31-67) holds the
`AuthoringService`, `ready`, an in-flight counter that unload waits on (`command()` L:43-53).

**Declaration.** `SPECS = {operation: (probatio schema dict, description)}` (L:115-138). Tool name `hass_sharp__<op>`
(L:148); `parameters = p.Schema(schema)` (L:150); annotations: read-only iff op not in `MUTATIONS` (L:139:
`save, set_enabled, delete, run, library_save, library_delete`), mutations are `destructive`, `open_world` (L:151-153).
Shared schema pieces: `OBJECT` open dict (L:79); `NODE {id, kind, config, position?, label?}` (L:80-82, no `size`);
`EDGE {id, source, target, kind?, port?, sourcePort?, targetPort?}` (L:83-85); `GRAPH {version, name, nodes, edges}` plus
`imports`/`members` let through for the backend to refuse (L:86-91) — any other graph key (so a `definition`) is refused;
`ID` `^[a-z0-9][a-z0-9_-]{0,79}$` (L:92); `REVISION` int ≥0 (L:93); `PAGE` offset≥0 default 0, limit 1..100 default 50 (L:94);
`SERVICE_ID` (L:97).

**Call path.** `async_call` (L:155-179): `require_admin` (L:70-74) → `not_ready` → explicit int check for
`expectedRevision/offset/limit` (bool refused, L:162-164) → schema → `with state.command(): _call` → `ToolResult(result,
error=result.ok is False)`. Errors: `unauthorized`, `invalid_request` "Arguments do not match the tool parameter schema:
<probatio text>" (L:171-174) or without text (L:175-176), `internal` "Request failed; check integration logs" + log (L:177-179).
Error envelope `{version:1, ok:false, error:{code, detail}}` from `A.error` (A:10-11).

**`_call` dispatch** (L:181-236): `library_*` → `node_library({operation: <op minus prefix>, **args})`, a returned
`result.catalog` replaced by `automation_catalog` (L:184-191). `get_capabilities`/`get_node_schema` → `node_library
{operation: catalog}` (L:192-196); node schema with args → `node_schema` (L:197-199), without → whole catalog through
`automation_catalog` (L:200); capabilities reads `ex` from disk each call and returns `{graphVersions:[5], envelopeVersion:1,
minimumHomeAssistant, catalog, generation, guidance: PROMPT, semantics: SEMANTICS, examples}` (L:201-204). `discover` →
`discover()` (L:205-206). `history` → `visual read {id}` for its `automationId`, then `service.history(...)`, errors scrubbed,
byte-paged (L:207-220). `trace` → `service.trace(runId)` → `trace_view` (L:221-225). Everything else (`list, read, preview,
save, set_enabled, delete, run`) → `service.visual({operation, **args})`, with `deriveSignatures: True` for preview/save
(L:226-228); `list` answers `summary()` per document (L:231-233); `read`/`save` documents lose node `size` (L:234-235, L:262-266).

**Helpers.** `automation_catalog` drops node kinds whose `documents` lacks `automation` (L:239-245; today 12 kinds, pinned in
`tp/test_mcp_adapter.py:403`); every other catalog key is kept, so `get_capabilities`' catalog **does** carry `entityDomains`
(cat top-level keys: `version, graphVersion, runtimeSupport, limits, types, parametricTypes, operandKinds, nodes, entityDomains,
triggerOutputs, edgeKinds, structures, scopeContract`). `summary` (L:248-259) reads `document.graph` and `runnable_triggers`
(A:53-57: nodes of kind `trigger` only). `node_schema` (L:269-288): `{kind, node, types, parametricTypes, limits, generation}`,
`triggerOutputs` only when `dynamicOutputs == "trigger"` (L:278-279), `service` ports via `service_ports` (L:280-287);
**no `entityDomains`**, nothing for `dynamicOutputs: "command"` / `dynamicInputs: "entity"`. `scrub` replaces `token=` values
(L:103, L:291-293). `byte_page` caps a page at `PAGE_BYTES` 32 KiB (L:101, L:296-304). `trace_view` (L:307-332): snapshot reduced
to `revision, visualId, visualRevision` (+`graph` if asked), never `catalog`; events without values unless `node`; pages of
`TRACE_PAGE` 100 / `TRACE_NODE_PAGE` 10, max 500. `discover` (L:335-380): kinds `entities, services, devices, areas, triggers`;
entities carry only `ENTITY_ATTRIBUTES` (L:106-112).

**Only these results are size-limited:** discover, history, trace (byte_page) and list (summary). `read`, `preview`, `save`,
`get_capabilities`, `get_node_schema` and the library tools return whole answers.

**The 17 tools as an LLM sees them** (descriptions L:116-137; 11 read-only):

| Tool | Params | Does |
|---|---|---|
| get_capabilities | — | catalog (automation kinds), rules, examples (L:116) |
| get_node_schema | kind?, service? | one kind's spec + type tables; service ports (L:117) |
| discover | kind, query, domain?, service?, offset, limit | real ids in HA (L:118-120) |
| list | — | summary of each "saved visual automation" (L:121) |
| read | id | one automation's graph, revision, source (L:122) |
| preview | graph | validate+compile, returns graph with derived pins (L:123) |
| save* | id, expectedRevision, graph | save and activate (L:124) |
| set_enabled* | id, enabled | automation on/off (L:125) |
| delete* | id, expectedRevision | delete automation (L:126) |
| run* | id, triggerId | run a trigger node now → runId (L:127) |
| history | id, status?, offset, limit | runs of "one saved visual automation" (L:128) |
| trace | runId, node?, graph?, offset, limit | per-node trace of any run (L:129-131) |
| library_list / library_read | — / id | library sources (L:132-133) |
| library_preview | id, source, workspaceVersion, generation?, sources? | candidate catalog + diagnostics (L:134-135) |
| library_save* / library_delete* | id, expectedRevision(, name, source) | rebuild every graph (L:136-137) |

**Where the content comes from.** `PROMPT` (L:19), `SEMANTICS` dict of `execution, types, triggers, service, runs, csharp`
(L:21-28), `ex` read at call time (L:201), catalog from .NET `NodeLibraryRequest {operation: catalog}`.

**How tools reach .NET.** `AuthoringService` (A:60-161): `_request(method, request)` → `dotnet_tasks.async_run_dotnet_task(hass,
method, json)` → parsed envelope (A:65-71). `visual()` snapshots the request, runs `_checked_preview` (A:73-90: .NET preview, then
`native_triggers.async_validate_graph_triggers`, refused save answered `validation`) for preview/save, then
`manager.VisualRequest`, adding `triggers` to answered documents (A:92-115). `entities()` (A:120-144): admin check, `ready_entry`
before the first await (A:130), snapshot, `_checked_preview(manager.EntityRequest, …)` only when `logic.kind == "graph"`
(`definition_graph` A:19-25; A:134-137), then `manager.EntityRequest`; on ok `list/read/save` each answered document gains
`registeredEntities` from HA's entity registry (A:139-143; `registered_entities` A:34-50). `history()` → `manager.GetRunHistory`
(A:151-155); `trace()` → `manager.GetRunTrace` (A:157-161); `run_now` → `manager.RunNow` (A:146-149). Websocket commands:
`hass_sharp/visual`, `/node_library`, `/entities` (code `entity_request_failed`) via `envelope_command` (WS:51-77);
`hass_sharp/run_now`, `/run_history` (both keyed `automationId`), `/run_trace` (`runId`) (WS:79-138).

**Automation-only by construction (would mislead or fail for a definition):**
- `PROMPT` "HassSharp builds Home Assistant automations as typed node graphs" (L:19); `UNAVAILABLE_PROMPT` neutral.
- Descriptions say "saved visual automation" (list L:121, read L:122, delete L:126, history L:128), "graph" everywhere;
  `get_capabilities` "complete example graphs" (L:116); `library_save` "Every saved graph is revalidated" (L:136) — .NET also
  rebuilds definitions and answers `definitions`, `invalidDefinitions` (NLA:277, NLA:286), passed through untouched.
- `SEMANTICS["triggers"]` speaks of manual runs of an automation; `SEMANTICS["runs"]` names no `Command`/`Setup`/`Frame` cause
  (`InvocationCause` at `HassSharp/UserScripts/AutomationConfiguration.cs:6`: `Startup, Entity, Manual, Schedule,
  HomeAssistantTrigger, Command, Setup, Frame`).
- `automation_catalog` hides the 12 entity-only kinds from capabilities, whole-catalog schema and library answers (L:239-245).
- `node_schema` carries `triggerOutputs` only (L:278); not `entityDomains`; nothing about `dynamicOutputs: "command"`
  (`entity_command`), `dynamicInputs: "entity"` (`entity_state`, `set_entity_state`), `setting`'s output typed by the setting.
- `GRAPH` schema refuses a `definition` key (L:90-91); a definition needs `{name, device, settings, entities, logic}` instead.
- `summary` and `runnable_triggers` read `document.graph` (definition graph is under `logic.graph`) and `kind == "trigger"`.
- `drop_node_sizes` reads `document.graph` (L:264).
- `history` resolves the id through **`visual read`** (L:208): a definition id gives `not_found`, or — automations and
  definitions are separate stores and may share an id (EA:14-18) — the automation's runs.
- `run` goes to `VisualRequest` `run` (VA:127, `DispatchVisual` VA:77-88); `manager.RunNow` refuses a definition's script
  ("Automation is not active", M:250-251).
- `ex` has only automation graphs + one library (`{graphs: [10], library: {id, name, source, graph}}`).
- `trace` already works for a definition's run (no kind filter in `M.GetRunTrace` M:217-230); its snapshot `visualId` is the
  definition id and `graph` is the composed graph whose `definition.settings` hold only `{key, type}` (DGr:33-42).

## 2. The entity API the tools would call (`EA`)

`HassSharpManager.EntityRequest(string json)` (EA:325-445), under `_visualGate`. Operations accepted (EA:333):
`types, list, read, preview, save, set_enabled, delete`; anything else → `invalid_request` "Unknown entity operation"
(pinned `tr/EntityApiTests.cs:96-101`). **No `run`.** Envelope `{version:1, ok:true, result}` (EA:433).

| Op | Request | Result |
|---|---|---|
| types | — | `{types: EntityType[] sorted by id, generation}` (EA:342-343); `EntityType {id "library:<src>:<ns.Type>", sourceId, typeExpression, kind device\|entity, domain, title, description, settings[{key,label,description,type string\|boolean\|integer\|number\|enum,secret,options,minimum?,maximum?}]}` (`fe/entities/model.ts:113-136`) |
| list | view? (`home`), todayUtc? | documents via `EntityJson` minus `source`,`sourceMap` (EA:352-365); `home` adds `recentRuns, runsToday, failedLastDay, lastRunUtc` (EA:288-300) |
| read | id | `EntityJson` (EA:369) |
| preview | definition | `{valid, diagnostics, source, sourceMap}` (EA:316, EA:344-349) — **no `graph`**, so no derived pins returned |
| save | id, expectedRevision, definition, enabled? (new only) | `{id, saved:true, document, active, activationError, diagnostics?}` (EA:119-125, EA:396-407) |
| set_enabled | id, enabled | `{id, enabled, active, activationError}` (EA:370-394) |
| delete | id, expectedRevision | `{deleted:true}`; also deletes history identity, stored states, HA entities (bounded 30 s) (EA:408-429) |

`EntityJson` (EA:42-57) adds to the stored fields: `automationId` (`_history.Identity(path)`, the run-history key), `active`,
`currentRevisionActive`, `activationError`, `status` (`running|setup|failed|off`), `liveEntities [{uniqueId,key,domain,name,
entityId}]`; Python adds `registeredEntities [{uniqueId,entityId,domain,name,disabled}]` (A:139-143).
Errors (EA:435-443): `storage`, `validation` (detail = preview JSON), `conflict`, `not_found` ("Entity definition not found"),
`invalid_request` (Argument/Json/InvalidOperation/Overflow message), `internal`. Python adds a `validation` refusal from HA
trigger checks (A:88-89); websocket wraps exceptions as `entity_request_failed` (WS:71).
Save refusals in words: `enabled` on an existing definition (ED:207-208), id `new` reserved (ED:204), invalid id (ED:113),
setting with both a value and `keep` (EA:274-281).

**Panel client** `fe/entities/api.ts:70-95`: `callWS({type:'hass_sharp/entities', request:{operation, ...}})`; `types`, `list`,
`home(todayUtc)`, `read(id)`, `preview(body)` → `{definition: body}`, `save(id, body, expectedRevision, enabled?)`,
`setEnabled`, `remove`. Pinned by `ft/entity-api.test.ts`.

**Stored document** `EntityDefinitionDocument` (ED:73-89), camelCase (`VisualStore.Json`, `HassSharp/Visual/VisualStore.cs:33`):
`{version:1, id, revision, name, device, settings[], entities, logic, source, sourceMap, enabled, savedUtc, catalogGeneration}`.
Server fills `version, id, revision (+1), source, sourceMap, catalogGeneration, enabled (old ?? request ?? true), savedUtc`,
missing node positions (`FillMissingLayout`, ED:213-217), strips any client `definition` from the graph (ED:211-215, DGr:45-50).
Body a client sends (`DefinitionBody` ED:66-67; TS `fe/entities/model.ts:1-62`):
- `name` (1..120 chars, DG:48); `device` null or `{manufacturer, model, swVersion, hwVersion, configurationUrl, suggestedArea,
  via: {definition} | {deviceId}}` (ED:9-23; `via` has no form in the panel, `model.ts:50`; read by `D/DefinitionHost.cs:905`,
  `entity_bridge.py:400-414`);
- `settings[]`: graph `{key, label?, type: string|boolean|number, value, secret?}` (DD:56-68); class `{key, value}` only, checked
  against the class's settings (DG:86-97); request-only markers `keep`, `hasValue` (ED:39-50);
- `entities[]` (graph only, ≥1, DD:39): `{key ^[a-z][a-z0-9_]{0,39}$, domain ∈ cat.entityDomains, name?, icon?, deviceClass?,
  entityCategory? config|diagnostic, enabledByDefault?, options?}` (DD:26-157); class definitions refuse entities (DG:52);
- `logic`: `{kind:"graph", graph:{version:5, name, nodes, edges}}` or `{kind:"class", type:"library:<src>:NodeLib.<Type>"}` (ED:53-58).
Minimal graph example (the acceptance case) as the panel tests build it: `ft/EntityDefinitionView.test.tsx:105-119` —
`entity_is` ×2 (`config.entity` literal `entity_id`, `config.state` literal `"on"`) → `or` (left/right) → `entity_state`
(`config.entity: "any_motion"`, input port `is_on` from `cat.entityDomains.binary_sensor.state`). Labels in the catalog:
`entity_is` "Has state", `or` "Or", `entity_state` "State" (the design's "Is on" is now "Has state", P2:378).

**Secrets.** Every answer of a definition passes `WithoutSecretValues` (EL:219-244, called in EntityJson EA:49 and library
rebuild answers EL:203-216): `value:null, secret:true, hasValue`. Round trip: `keep:true`, or `hasValue:true` with no value,
keeps the stored value (ED:236-252, EA:269-281); clearing is `value:null` without `hasValue` (P3:253). Panel: `settingToSend`,
`maskedSetting`, `draftGraph` (`fe/entities/model.ts:162-200`). Traces: secret-derived values recorded `"***"`, `redacted:true`
(P3:255). Preview answers carry no settings.

**Pictures.** No API operation returns picture bytes: `CameraImage(uniqueId)` (EA:472-477) is only for HA's camera platform;
traces and logs write an image as text (`HassSharp/Visual/TraceJson.cs:132,140,348`; `VisualRuntime.cs:328-330,522`).

**Runs and traces of a definition.** Same history store as automations: read with `hass_sharp/run_history` and the document's
`automationId` (P2:394; `fe/components/AutomationRuns.tsx:70-73`); traces by `runId` through `run_trace`. Recorded: command and
setup runs; trigger flows only with `trackHistory: true`; State never (EA:97-107, P2:384). The MCP `history` tool does not work for
a definition (see §1); `trace` does.

**Signatures.** The entity API has no `deriveSignatures` (only `VA:105-109`); a definition's Custom C# node must carry its
`config.signature` or fail validation, and preview returns no graph to copy it from.

## 3. Items addressed to phase 6 by earlier phases (deduplicated)

**MCP tool / schema gaps**
- "`llm.py`: `entities_*` MCP tools" (design:234); `EntityRequest(json)` "(list, read, preview, save, delete, set_enabled,
  types, run)" (design:220).
- "MCP: `llm.py`'s node schema knows only `dynamicOutputs: "trigger"` (not `"command"`) and does not carry `entityDomains`"
  (P4:327). [`get_capabilities` already carries `entityDomains` inside its catalog — P2:400 "harmless"; P3:245 lists
  "`get_capabilities` carrying `entityDomains`" as not taken.]
- "The MCP tool contract must say: clearing a secret means dropping `hasValue`; `new` is not an id; a definition's runs are
  read with its `automationId`." (P3:279)
- "The history default for trigger flows in a definition, the Stop rule and the 30 s command limit belong in the MCP
  authoring guidance." (P2:417)

**API items**
- "The `run` operation and Run now for definitions" (P3:278; P3:33 "`run` moves to phase 6 with the MCP tools"; P3:245;
  P4:39 "`run` for definitions (phase 6)"; P2:394 "A `run` operation for definitions is not built").
- "the parent device (`via`) has no form" (P3:278; P4:39, P4:298 "the `via` form").
- "API: an overload of `CameraSnapshot` taking a cancellation token (today it is cut only through `Calls`, so turning off can
  wait 10 s)." (P5:232; today `D/SharpComponent.cs:178` `CameraSnapshot(string entityId)`)

**Authoring examples wanted**
- "`http_request` and `download_file` need authoring examples for tools" (P4:327; `http_request` has one, "Read a device over
  HTTP"; `download_file` is entity-only, so needs a definition example).
- "MCP: authoring examples for the five camera steps." (P5:234: `camera_snapshot`, `image_draw_rect`, `image_draw_text`,
  `camera_frame`, `show_frame`).

**Docs facts to state** (target `docs/custom-entities.md` unless noted)
- Keys: "Renaming an entity key creates a new entity (the unique id changes) and the new one may get a _2 entity id while the
  old registry entry still exists during the sync; say so in docs/custom-entities.md." (P2:321); "a saved entity key is
  permanent" (P3:278; P3:245 "→ phase 6 docs say keys are permanent").
- "A command or callback that stops its own definition waits out RunDrainTimeout (10 s); commands answer 'The entity is not
  running' during a replace; Stop() is not called at Home Assistant shutdown." (P2:322)
- "A State updates only for entities chosen on its nodes' pins; a library or Custom C# node that reads an entity through
  `NodeContext.State` does not make it a dependency." (P2:415)
- "Entities of a definition without a device get ids from their name alone (`binary_sensor.any_motion`): name them with
  clashes in mind." (P2:416)
- Traces: "what a trace hides and what it does not (a true/false from a secret is shown; a step that returns what it was given
  can show it); Compare refuses Text against Number and Entity field defaults to Text; Home Assistant restores a deleted
  entity's registry customisation when its unique id comes back." (P3:280)
- "Docs, tools: `Http` keeps no cookies; the host of a secret address is shown; a checksum is of the file as the server sends
  it (not of what a browser saves after decompression); a file already in the cache is trusted without hashing it again and
  unused files are never cleaned up; a redirect to another host fails a step that has headers." (P4:328)
- "Docs, behaviour: a save answers `running` up to about a quarter of a second before Home Assistant shows the entities as
  available, and a service call in that window does nothing; Home Assistant checks number, select and text values and cover
  positions itself, clamps brightness and drops it for a light that cannot be dimmed, before .NET sees the command; a value the
  command did not send but the step shows (brightness 255) appears in the trace as if sent; a class number or text whose `Min`
  is above its `Max` refuses every value; a class light's first turn-on without brightness gives the class no brightness." (P4:329)
- Camera (P5:231, also P5:209): "how a frame is shown (the frame root and Show frame; `Frame()` and `Show` in a class); the
  limits of decision 8; libjpeg-turbo must be on the system (Home Assistant's image has it; unverified on 32-bit ARM, i386 and
  Core in a venv), and without it a picture passes through but drawing fails with one line; pictures from another camera are
  JPEG only; the 8 s limit; a camera's own picture cannot be taken, and two definitions photographing each other time out; each
  camera in a chain keeps its own last good frame, so a chain of N can show a picture N times as old; take pictures in a frame
  flow or a trigger, not in When started (a source that is not up at boot fails the start); frame-flow failures show only in the
  log unless run history is on; after a restart a camera that is pushed its frames has none until its trigger fires; a
  `SharpImage` is for one flow at a time."
- MCP guidance (P2:417, above): trigger-flow history default off, Stop step = success, 30 s command limit.

**Wording items**
- "The catalog's Trigger description says "Start the automation…" inside a definition: override it in the frontend, not in
  the catalog (the catalog is part of generated sources)." (P3:281; again P4:330)
- "the stored-state warning's "entities start without them"" (P4:330).
- "Wording: log lines name a camera as "name (key)" or by its key alone; the panel has no save-time warning for a definition
  that photographs its own camera." (P5:233)

**Carried as "not taken" from phase 3's lists without a phase named** (P3:245): "`command.unhandled` naming the entity by name
as well as key"; "the entity API's duplicated `currentRevisionActive`/`activationError` formulas and its `invalid_request`
after a completed store write". P4:298 and P5:209 hand phase 6 "the wording items" generically.

**Marked "Not tied to a phase"** (deliberately not phase 6): P2:326-347 (phase 1 review leftovers: VisualStore copy, mirrored
CheckDefinitions, `All` walker, STJ load-context pin, bridge docstring, static log dedupe, Start()/drain races, pump debounce and
push failures, `EntityCommand` non-object data, removal timeout race, colon-in-id assert, loop-stopping op, partial sync ids,
platform unload False, bridge before try); P2:421-423 (off cancels with "reloaded" reason; command reaching the old host during a
replace; `DefinitionGenerator` static fallback); P3:285-287 (Fit to view; palette ranking; review-package script; trailers);
P4:334-339 (class `Setup()` retried on every push; panel drops server option problem; storage-failure logs, Convert message;
`download_file` name/duplicate checksum/`{}` headers/HEAD body; oversized files; split review); P5:238-245 (11 s HA stop; `www/`
not emptied; LICENSE/notices not in zip; CI libturbojpeg unrun; Tidy loops; test hardening; camera refusal microseconds; split
review). Test-and-tooling lists: P2:348-369, P2:425-431.

## 4. The docs as they are

No `docs/custom-entities.md`. No user doc mentions entity definitions, `SharpDevice`/`SharpEntity`, the Entities section,
cameras or libjpeg-turbo (grep over `README.md`, `docs/*.md`, `docs/backend`, `docs/runtime`). No screenshots or images in any doc.

**`README.md`** (130 l.): `# HassSharp` intro "building automations as graphs" (l.3-6); guide links line (l.8-10:
visual automations · node libraries · MCP authoring · scheduling); `## The panel` "It has two pages" Automations, Node library
(l.12-24) — the panel nav is now Automations, Entities, Node library (`fe/layout/MainLayout.tsx:12`); `## The node editor`
(l.26-40); `## Where C# fits` (l.42-57); `## Authoring through MCP` (l.59-66, lists what tools do: "turn automations on and
off…") with `### Security` (l.68-83: "Saving an automation or a node library compiles C#…"); `## Installation` (l.85-111:
storage line l.107 "Automations and node libraries are stored in `.storage/hass_sharp/`"); `## Building from source` (l.113-130).
Voice: short paragraphs, bold UI names, bullet lists, links to guides.

**`docs/mcp-authoring.md`** (219 l.): intro + MCP Server setup (l.1-39: select HassSharp under MCP Server options, admin only,
`/api/mcp` and `/api/mcp/hass_sharp`, read-only only on configured `/api/mcp`, 30 s in-flight on reload); `## Security` (l.41-69:
"`save` and `library_save` compile C#", read-only removes the six mutations l.53-56, what leaves the house, injected text, trace
redaction by name); `## Workflow and tool contract` (l.71-194: numbered steps 1-11 `get_capabilities` … `delete`, each with
argument and result shapes inline in backticks; read-only list again l.184-185); `## Libraries` (l.196-219: library tools;
results `automations:[…]`, no mention of `definitions`).

**`docs/node-libraries.md`** (585 l.): `# Writing C# nodes`; `## Node methods`, `### Flow outputs`; `## Entities` (l.230-345: entity
*handles* `LightEntity` etc. as pins — not entity definitions); `## States and services`; `## Custom C# nodes`; `## Node libraries`
(l.495-562: complete example library, ids `library:<source id>:<type>.<method>`, "Save it with ID `example`… The complete graph JSON
… ship in `authoring_examples.json`" l.535-538, library save rebuilds "all graphs" l.551-562); `## Scope snapshots`. Voice: C# code
blocks with XML doc comments, then prose rules, quoted error messages. Nowhere for `SharpDevice`/`SharpEntity`/`[Setting]`
classes; a class section or a link to `custom-entities.md` would sit beside `## Node libraries`, and l.551-562 would mention
definitions being rebuilt too.

**`docs/writing-visual-automations.md`** (394 l.): headings Turning off (l.23), Control flow (l.39), Types (l.162), Triggers (l.227),
Calling services (l.289), Harmless complete example (l.316, JSON block + list of `ex` contents l.335-341), Run history and traces
(l.344), C# nodes (l.359). Voice: "Choose **Automations → New automation**" steps, JSON blocks.
**`docs/scheduling.md`** (51 l.). **API references:** `docs/backend/visual-automations.md` (376 l.: Shared WebSocket and MCP services
l.10, Graph 5 contract l.80, Library API l.266, Persistence l.329) and `docs/backend/run-history.md` (236 l.: WebSocket commands
l.24, Runs l.43, Traces l.76) — neither mentions `hass_sharp/entities` or definitions. `docs/runtime/*` and
`docs/backend/review-fixes.md` are historical records.

**Existing user-facing wording to reuse:** the editor's help dialog `fe/entities/EntityHelp.tsx:7-29` (what a definition is, State /
When commanded / triggers, history defaults, keys permanent, settings and secrets, cameras, HTTP/downloads/restore, In Home
Assistant).

Places custom entities must be mentioned: README intro (l.3), guide links (l.8-10), The panel (l.14-24), MCP paragraph (l.63-66),
Security (l.73-76), storage (l.107-108); mcp-authoring intro/Security l.46-56/workflow/read-only lists l.54, l.184 and Libraries
l.213-216; node-libraries `## Node libraries` (l.495-562); writing-visual-automations example list (l.335-341) if `ex` grows.

## 5. Tests that pin this area

- `tp/test_mcp_adapter.py` (476 l.): skipped unless `probatio` importable (`HAS_NEW_HA` l.14, `@skipUnless` l.16); CI asserts the
  import first (`.github/workflows/tests.yaml:29-31`). Fakes: `service = SimpleNamespace(visual, node_library, history, trace)`
  (l.33-35) — **no `entities`**; `CATALOG` = the real `cat` (l.39). Pins: 17 tools and the read-only set (l.56-70); 11 tools in
  MCP read-only mode (l.190-204); prompt sentences (l.142-149); capability examples `examples.library.id == 'example'` (l.150-189);
  entity-only kinds set and their absence from capabilities/whole schema, presence by kind (l.397-412); library answers filtered
  (l.414-429); history via `visual read` → `automationId` (l.439-443); trace view (l.446-475); run contract → `visual run`
  (l.346-354); node schema service/trigger outputs (l.356-377); entity-handle pins (l.378-396).
- `tp/test_entity_authoring.py` (248 l.): `AuthoringService.entities` — graph trigger validation on preview/save (l.140-188),
  `registeredEntities` (l.189-223), pass-through (l.224-248); imports `test_visual` helpers, runs without HA.
- `tp/test_authoring_services.py:61-81`: `entities()` envelope via `ast`-loaded `authoring.py`.
- `tp/test_entity_contract.py`: platform modules vs `cat.entityDomains` (l.1, l.15).
- `ex`: validated **only** by `tr/AuthoringAdapterTests.cs:18-70` (suite `--authoring-adapter`): each `examples.graphs[*]` through
  `VisualGenerator.DeriveSignatures` (Custom C# nodes must carry only `source` and `arguments`, l.36-42) and `VisualGenerator.Preview`
  with and without service schemas, zero diagnostics (l.28-33, l.43-44); named examples mutated ("Motion lights", "Sunset lights",
  "Light named in an event", l.48-64); `examples.library` compiled and its graph previewed (l.65-70). Not checked by the TS
  validator, by any Python test (packaging only checks the file ships: `tests/test_release_package.py:28,71`;
  `scripts/package_release.py:37`), nor by docs.
- A definition example would not pass this machinery as is: the loop treats every `graphs` entry as an automation graph. The
  validators to call are `DefinitionGenerator.Preview(DefinitionBody, snapshot)` (DG:43-68; used directly in
  `tr/EntityStoreTests.cs:138,176,202,307`) or `manager.EntityRequest({operation: preview})` (needs a started manager, as
  `tr/EntityApiTests.cs` does); entity-only kinds need the libraries snapshot `NodeLibraryCompiler(...).Preview([]).Snapshot`
  as l.27 builds.
- No test checks docs (no test reads `docs/*.md`); `ft/custom-code.test.tsx:439` only cites `docs/node-libraries.md` in a comment.

## 6. The untracked guide design (never modify or stage it)

Proposes shipping an MCP "skill" without the Skills extension: new `guide.py` with `render_guide(catalog) -> str` (hand-written
workflow + visual-nodes-first rule + node reference generated from `catalog["nodes"]`); a read-only tool `hass_sharp__get_guide`
returning `{format:"markdown", generation, guide}`; `PROMPT` starts by telling the agent to call `get_guide` then
`get_capabilities`; `skills/hass-sharp/SKILL.md` generated by `scripts/generate_skill.py` with a staleness test; `docs/mcp-authoring.md`
gains `get_guide` and skill install steps; tests in `tp` (guide-design:44-70). Overlaps: both change `L` `PROMPT`, the tool count
pinned at 17/11 (`tp/test_mcp_adapter.py:58,196`), `docs/mcp-authoring.md`'s workflow; its node reference from
`catalog["nodes"]` would list or hide entity-only kinds depending on whether it uses `automation_catalog`; it speaks only of
"automations". It keeps `get_capabilities` as the source of semantics and examples (guide-design:41-42).
Status: **not started** — no `guide.py`, `get_guide`, `skills/` or `scripts/generate_skill.py` exists; the last 15 commits on
`L`/`docs/mcp-authoring.md` (`888e011` … `56c53ae`) contain none of it (latest to `docs/mcp-authoring.md`: `a1bb5c7`, 2026-11-14).

## 7. Open questions for the planner

1. Tool shape: separate `entities_*` tools (design:234) versus a `kind`/`document` parameter on `list/read/preview/save/
   set_enabled/delete/run/history`; tool count and read-only set change either way (`MUTATIONS` L:139, tests l.58/l.196,
   docs l.54/l.184).
2. Tool names: `hass_sharp__entities_list` etc. as the design says, or `entity_*`/`definition_*`; is `types` its own tool or a
   `discover` kind (`discover` kinds L:118)?
3. The `run` operation: which roots are runnable (When started, a trigger, When commanded with what command data, When a frame is
   needed, State?), its id (`rootId`/`triggerId`), whether it needs `currentRevisionActive`, and whether it goes through
   `EntityRequest` or `RunNow`/`DispatchManual` (M:244-262 refuses definitions; VA:77-88 is the automation form).
4. History for a definition: a new tool, `history` taking a kind, or `history` resolving via `entities read` — and how to stop an
   automation and a definition sharing an id from being confused (L:208).
5. Catalog for definitions: does the entity flavour of `get_capabilities`/`get_node_schema` include the 12 entity-only kinds
   (L:239-245 filters them for all MCP answers today), and does `node_schema` add `entityDomains` and `definition`-aware port
   resolution for `entity_state`/`set_entity_state` (`dynamicInputs: "entity"`), `entity_command` (`dynamicOutputs: "command"`),
   `setting` (output type from the setting)?
6. Custom C# nodes in a definition: add `deriveSignatures` to `EntityRequest` (only VA:105-109 has it) and return the previewed
   graph from entity preview (EA:316 omits it), or document that signatures must be sent?
7. List size: definition `list` answers full graphs (EA:357-362) — a summary like L:248-259 (and of which fields: status,
   activationError, entities, registeredEntities, liveEntities)? `read` and `preview` stay unbounded as for automations?
8. Secrets: the server masks every answer (EL:219-244); does the MCP layer add anything (e.g. refuse a secret `value` coming back
   from a model, or drop `hasValue`)? How is "clearing a secret means dropping `hasValue`" (P3:279) worded in the tool description?
   Do secret values a model sends in `save` need a security note (they reach the model provider)?
9. Pictures: confirm no new tool returns bytes (none today, EA:472-477); does a camera's entity state/`entity_picture` in `discover`
   stay token-free (L:103-112 already).
10. Schema strictness: a probatio schema for `definition` (strict like `GRAPH`, or open object passed to .NET)? `device.via`
    shape (`{definition}` or `{deviceId}`, ED:12) — exposed to MCP (no panel form)?
11. Examples: a new top-level key in `ex` (e.g. `definitions`) and a new check in `tr/AuthoringAdapterTests.cs` (and/or Python),
    which examples (derived motion sensor, HTTP poll with `download_file`, camera with the five steps, a class definition — that
    needs a library source too), and whether automation `get_capabilities` returns them.
12. `library_save`/`library_delete` descriptions and docs: mention `definitions`/`invalidDefinitions` (NLA:277,286).
13. Prompt/semantics: one `PROMPT` for both kinds or a definition section in `SEMANTICS` (history defaults, Stop rule, 30 s
    command limit, keys permanent, `new` reserved, State dependency rule)? Coordination with the guide design's `PROMPT` change.
14. Which API items are in scope: `CameraSnapshot(entityId, CancellationToken)` (P5:232), a `via` form in the panel (P3:278), the
    Trigger description override in the frontend (P3:281), the wording items (P4:330, P5:233), `command.unhandled` by name (P3:245).
15. Docs layout: `docs/custom-entities.md` as one guide (graph and class definitions, cameras, tools, limits) and what moves into
    `node-libraries.md` (SharpDevice/SharpEntity/[Setting] class authoring) vs `mcp-authoring.md` (tools); whether
    `docs/backend/visual-automations.md`/`run-history.md` gain the entity API.
16. Live check (design:246; acceptance "derived sensor built through the MCP tools against the local instance"): `mcp__hass__*`
    reaches production and is forbidden (P5:26); the check needs an MCP client against the local container's `/api/mcp`
    (`http://localhost:5001/api/mcp`, mcp-authoring.md:19-20) or a throwaway `hass_wt` on 5002 (P5:26), with an admin token
    and HassSharp selected in the MCP Server options there.
