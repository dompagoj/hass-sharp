# Custom entities, phase 4: tools, six more entity types, restored state

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Every implementer and reviewer runs on Opus 5.5.

**Goal:** A definition can talk to a device over HTTP and download what it needs at startup, can be a button, number, select, text, light or cover as well as a sensor or switch, and comes back after a restart with the state it had.

**Architecture:** A domain stays one row of four tables that tests pin to each other (typed C# class, `SharpDomains.Graph`, the catalog's `entityDomains`, a Python platform); the catalog row gains optional state values and command data, which both validators, the runtime, the platforms and the panel read. HTTP goes through one process-wide connection handler: a class gets a host-owned `HttpClient`, a graph gets an `http_request` step interpreted by `VisualRuntime` like `call_service`. Downloads are cached by checksum under `.storage/hass_sharp/files/`. Last states are kept by .NET in one JSON file and applied before a definition's setup runs.

**Tech Stack:** C# (.NET 11; `System.Net.Http`, no new packages), Python (Home Assistant 2026.11 entity platforms), SolidJS + Vitest (panel).

**Spec:** `docs/superpowers/specs/2026-11-17-custom-entities-design.md` (sections "C# API", "Hosting", "Catalog", "Python", "Phases" item 4, "Builder's calls"). **Code map:** `docs/superpowers/plans/2026-11-18-custom-entities-phase-4-map.md` — every task reads the sections its dispatch names. Hand-offs: the "For phase 4" lists at the end of the phase 2 and phase 3 plans.

## Global Constraints

- Paths are under `custom_components/hass_sharp/` unless they start with `tests/` or `docs/`; `D` = `HassSharp/Devices/`, `V` = `HassSharp/Visual/`, `cat` = `visual_node_catalog.json`, `fe` = `frontend/src/`, `ft` = `frontend/tests/`.
- Existing automations' generated source stays byte-identical: in `tests/fixtures/generated-source-pins.json` no existing line is ever changed or removed (a line may be added for a new valid automation case, exactly as the pins suite prints it). In `cat`, add node kinds and `entityDomains` entries only; never change `version`, `graphVersion` (5), an existing node's spec, `triggerOutputs`, or any section the embedded catalog keeps whole (`limits`, `operandKinds`, `edgeKinds`, `structures`, `scopeContract`, `parametricTypes`, `runtimeSupport`).
- Both validators (C# and TypeScript) agree on `tests/fixtures/visual-graph-validation.json`; a rule added to one is added to the other in the same task, with fixture cases.
- `tests/Runtime/EntityDomainTests.cs` keeps pinning the typed classes, `SharpDomains.Graph` and `entityDomains` to each other, key for key and in order.
- A secret setting's value is never sent to the panel, never written to a `localStorage` draft, a log, a diagnostic, a run trace, a snapshot or a test snapshot. A step input fed by a secret is traced `"v": "***", "redacted": true` with its source kept; a message the runtime composes says the value is hidden instead of quoting it.
- Nothing a definition owns outlives it: timers, subscriptions, HTTP clients and in-flight requests are stopped or cancelled when it stops, and its load context is collected (`tests/Runtime/ReloadMemoryTests.cs`).
- No type from a NuGet package appears in an API user code sees (`ScriptLoadContext` gives each script private copies); framework types (`HttpClient`, `JsonElement`) are fine. No new runtime dependencies. Only strings and bytes cross the Python/.NET boundary.
- Unique id `<definitionId>:<entityKey>`; entity keys `^[a-z][a-z0-9_]{0,39}$`; definition ids `^[a-z0-9][a-z0-9_-]{0,79}$`, never `new`.
- User-facing text: plain sentences, sentence case, no internal names. CSS under `.hs-app`, colours from `fe/styles/tokens.css`, both colour schemes, narrow layout at `max-width: 700px`.
- Suites, from the repository root: `dotnet run --project tests/Runtime --disable-build-servers` (exit 0; 402 PASS lines before this phase); `dotnet test custom_components/hass_sharp/HassSharp.Editor.Tests` (22); `PYTHONPATH=tests/python python3 -m unittest discover -s tests/python` (155 OK, 41 skipped on the host; 167 and none skipped in the container); `python3 -m unittest discover -s tests` (5); `(cd custom_components/hass_sharp/frontend && bun run test)` (1638 in 69 files) and `bun run build`.
- Git: plain `git diff` uses an external tool here — pass `--no-ext-diff`. Stage only files the task changed; never stage `docs/superpowers/specs/2026-11-15-mcp-authoring-guide-design.md` or git-ignored build output. Commit on `custom-entities`; no push.
- Containers: the user's `hass` container may only be restarted (once per publish or frontend build), have its logs read, and run the smokes and the Python suite through `docker exec`; no API calls to it, nothing read or written under its `/config` except the bind-mounted integration directory. Live checks use a throwaway `hass_wt` container on port 5002. Home Assistant's own source may be read with a one-off container from the local image: `docker run --rm --entrypoint python homeassistant/home-assistant:2026.11.1 -c "import inspect, homeassistant.components.number as m; print(inspect.getsource(m.NumberEntity))"`. Never use `mcp__hass__*` tools.

## Decisions taken for this phase

1. **The domain row** in `entityDomains` gains two optional members: a state value may say `"required": false`, and a command may carry `"data": { name: { types, label, required? } }`. An option may say `"required": true`. The six new rows are given in Task 2 and are the contract for every later task.
2. **State values in a graph.** *Set state* takes any subset of an entity's state values (at least one supplied); what it does not supply is left as it is. *State* needs the values the row marks required and may supply the others. A step naming an entity whose type has no state (a button) is an error.
3. **Command data in a graph.** *When commanded* has one output per data value of its command. A value Home Assistant did not send takes the entity's current state value of the same name when it has one (a light's brightness), else it is absent, and reading it fails the step by name — as an absent trigger output does. *(Amended during the phase: a data value may carry a `default` in its row, used when the entity has no current value of that name and would accept it; `light.turn_on.brightness` has `"default": 255`.)*
4. **Brightness is 0–255 and position 0–100**, as Home Assistant has them. A light is dimmable and a cover has a position only when its option says so. A cover always offers open, close and stop; a command the definition does not handle fails with a message that says so (the validator already warns about an unhandled command).
5. **Restored state is .NET's, not Home Assistant's.** The last state values of every entity that nothing recomputes at start are kept in `.storage/hass_sharp/entity_states.json`, written at most every 10 s and when the integration stops, and applied before a definition's setup runs, so setup may override them. Restored: in a graph, an entity no State step works out; in a class, an entity whose `RestoreState` is true (the default for switch, number, select, text, light and cover; false for binary sensor, sensor and button). Not restored: availability and attributes. A stored value the entity now refuses (an option that no longer exists) is dropped.
6. **HTTP.** One process-wide `SocketsHttpHandler` (certificates always checked, at most 5 redirects, automatic decompression, 10 s to connect). `SharpComponent.Http` is an `HttpClient` over it, created per definition, 30 s timeout, disposed after `Stop()`. `http_request` (any document) has a 10 s default timeout (1–300), reads at most 4 MiB, and treats a status outside 200–299 as a failure unless told not to. Skipping certificate checks is not built. *(Amended during the phase: three process-wide handlers share these settings and keep no cookies — the decompressing one for `Http`, one without decompression for downloads so a checksum is of the bytes the server sends, and one without automatic redirects for the `http_request` step, which follows redirects itself and does not hand the step's headers to another host.)*
7. **A response is not secret unless the step says so.** `http_request` has "Keep the response out of run history": its outputs are then recorded hidden and count as secret for the steps they feed. Inputs fed by a secret setting are hidden as everywhere else.
8. **Downloads.** `SharpFiles.Download(url, sha256, name?)` and the `download_file` step (entity definitions only) return the path of a file under `.storage/hass_sharp/files/<sha256>/`; a file is only ever at that path after its checksum matched. No size limit below 2 GiB, no cleanup of unused files, and time spent receiving a download does not count against the 60 s setup limit (a download that receives nothing for 60 s fails).
9. **Statuses.** A definition that is on but could not start (its setup failed, it does not compile) answers `failed` with the reason, and the failure is logged once as a warning when it is saved. Definitions are still prepared one after another at startup.
10. **Not built in this phase:** more light features (colour, effects), cover tilt, `via` form, `run` for definitions (phase 6), camera (phase 5).

## Review Focus

1. A device that does not answer (refused, timeout, no such host), polled every few seconds: the log gets one line per new reason and one on recovery, not one per tick; the entity shows what the graph says (unavailable or its last state); stopping the definition cancels the request. — Tasks 6, 7, 8.
2. Home Assistant restarted while entities hold states set by commands: they come back with them; a stored value the changed definition no longer allows starts the entity unknown instead of failing the definition; a corrupt state file is logged once and ignored. — Task 5.
3. A command whose data the entity does not allow (a number beyond its maximum by a service call, brightness for a light that cannot dim, an option not in the list): refused with a message that names the entity and the value, state unchanged. — Tasks 2, 4.
4. A download that is interrupted, has the wrong checksum, is wanted by two definitions at once, or was half-written when the process died: never a file at the final path that does not match its checksum. — Task 7.
5. A definition replaced or turned off while a request or a download is in flight: cancelled, nothing published afterwards, nothing leaked. — Task 7.

---

### Task 1: One place for labels and the first save's retry (frontend)

**Files:**
- Modify: `fe/visual/contract.ts`, `fe/entities/NodePickers.tsx` (l.17-21, l.196-197), `fe/components/TriggerSummary.tsx` (l.19-27), `fe/visual/nodeSummary.ts` (l.251-256), `fe/visual/session.ts` (l.399-419), `fe/entities/classSession.ts` (l.167-181), `fe/views/RunTraceView.tsx` (l.96-98), `fe/visual/NodeConfig.tsx` (l.37, l.204), `ft/visual-fixture.ts` (l.245, l.253)
- Test: `ft/visual-contract.test.ts`, `ft/EntityDefinitionView.test.tsx`, `ft/RunTraceView.test.tsx` (or the file that holds its tests), `ft/entity-api.test.ts`

**Interfaces:**
- Produces: in `contract.ts` — `entityTypeLabel(catalog, domain): string` (the row's `label`, else the domain in words) and `entityCommandLabel(catalog, domain, command): string` (the command's `label`, else the command in words): the only places a domain or command label is worked out; no hardcoded label table remains. `firstSave<T>(newId: () => Promise<string>, save: (id: string) => Promise<T>, isConflict: (e: unknown) => boolean): Promise<{ id: string; saved: T }>` (one retry with a fresh id on a conflict), used by both sessions. The in-memory backend answers `disabled` from the seeded registry entry and can be seeded with host statuses `setup` and `stopped`, mapped as the C# API maps them.

- [ ] **Step 1: Write the failing tests:** labels for a known domain/command and for one the catalog does not have ("Fan speed" for `fan_speed`); the three call sites show the catalog's label; `firstSave` retries once and gives up on a second conflict (both sessions' existing first-save tests pass unchanged); the run page keeps showing its run when a re-read fails while a document that proves the run is its own is cached, and shows the error state when none is; the backend's `disabled` and statuses.
- [ ] **Step 2: Run** `(cd custom_components/hass_sharp/frontend && bun run test)`. Expected: the new tests FAIL.
- [ ] **Step 3: Implement**; rename the shadowing `own` in `NodeConfig.tsx`.
- [ ] **Step 4: Run** the frontend suite and `bun run build`. Expected: PASS.
- [ ] **Step 5: Commit** `Work out entity labels and the first save's retry in one place`.

### Task 2: Six entity types in .NET and the catalog (C#)

**Files:**
- Modify: `D/SharpEntities.cs` (split the six new classes into `D/SharpControls.cs` if it passes ~400 lines), `D/SharpEntity.cs` (only if a shared helper for reading command data belongs there), `D/DefinitionDocument.cs` (l.79-105), `cat` (`entityDomains`), `tests/Runtime/EntityDomainTests.cs`, `tests/Runtime/SharpEntityTests.cs`, `tests/Runtime/EntityStoreTests.cs`, `tests/Runtime/EntityHostTests.cs`
- Read: map sections 3, 4, 5, 9.

**Interfaces:**
- Produces — the catalog rows, verbatim (after `switch`, in this order):

```json
"button": {"label": "Button", "state": {}, "commands": {"press": {"label": "Press"}}, "options": {}},
"number": {"label": "Number", "state": {"value": {"types": ["number"], "label": "Value"}},
  "commands": {"set_value": {"label": "Set value", "data": {"value": {"types": ["number"], "label": "Value"}}}},
  "options": {"min": {"type": "number", "label": "Minimum"}, "max": {"type": "number", "label": "Maximum"}, "step": {"type": "number", "label": "Step"},
    "mode": {"type": "string", "enum": ["auto", "box", "slider"], "label": "Shown as"}, "unit": {"type": "string", "label": "Unit"}}},
"select": {"label": "Select", "state": {"option": {"types": ["string"], "label": "Option"}},
  "commands": {"select_option": {"label": "Select option", "data": {"option": {"types": ["string"], "label": "Option"}}}},
  "options": {"options": {"type": "array", "label": "Options", "required": true}}},
"text": {"label": "Text", "state": {"value": {"types": ["string"], "label": "Text"}},
  "commands": {"set_value": {"label": "Set text", "data": {"value": {"types": ["string"], "label": "Text"}}}},
  "options": {"min": {"type": "number", "label": "Shortest length"}, "max": {"type": "number", "label": "Longest length"},
    "pattern": {"type": "string", "label": "Pattern"}, "mode": {"type": "string", "enum": ["text", "password"], "label": "Shown as"}}},
"light": {"label": "Light", "state": {"is_on": {"types": ["boolean"], "label": "On"}, "brightness": {"types": ["number"], "label": "Brightness", "required": false}},
  "commands": {"turn_on": {"label": "Turn on", "data": {"brightness": {"types": ["number"], "label": "Brightness", "required": false}}}, "turn_off": {"label": "Turn off"}},
  "options": {"dimmable": {"type": "boolean", "label": "Can be dimmed"}}},
"cover": {"label": "Cover", "state": {"is_closed": {"types": ["boolean"], "label": "Closed", "required": false}, "position": {"types": ["number"], "label": "Position", "required": false}},
  "commands": {"open": {"label": "Open"}, "close": {"label": "Close"}, "stop": {"label": "Stop"}, "set_position": {"label": "Set position", "data": {"position": {"types": ["number"], "label": "Position"}}}},
  "options": {"positionable": {"type": "boolean", "label": "Has a position"}}}
```

- Produces — typed classes, each with the two constructors, `Domain`, `Commands`, `Command`, `Values()`, `DomainOptions()` as `SharpSwitch` has them, every command as a `protected virtual Task` method plus an `init` delegate (neither → `NotSupportedException` naming the entity, as `SharpSwitch`), every state setter publishing:
  - `SharpButton`: `Press()`, `Func<Task>? OnPress`. No state.
  - `SharpNumber`: `double? Value`; `double Min = 0`, `Max = 100`, `Step = 1`; `SharpNumberMode Mode` (`Auto|Box|Slider`); `string? Unit`; `SetValue(double value)`, `Func<double, Task>? OnSetValue`.
  - `SharpSelect`: `string? Option`; `IReadOnlyList<string> Options`; `SelectOption(string option)`, `Func<string, Task>? OnSelectOption`.
  - `SharpText`: `string? Value`; `int Min = 0`, `Max = 100`; `string? Pattern`; `SharpTextMode Mode` (`Text|Password`); `SetValue(string value)`, `Func<string, Task>? OnSetValue`.
  - `SharpLight`: `bool? IsOn`; `int? Brightness` (0–255); `bool Dimmable`; `TurnOn(int? brightness)`, `Func<int?, Task>? OnTurnOn`; `TurnOff()`, `Func<Task>? OnTurnOff`.
  - `SharpCover`: `bool? IsClosed`; `int? Position` (0–100); `bool Positionable`; `Open()`, `Close()`, `StopMoving()` (the command on the wire is `stop`), `SetPosition(int position)` and their delegates.
- Command data is checked before the method runs: a number within `Min..Max`; an option that is one of `Options`; a text within `Min..Max` characters and matching `Pattern`; brightness 0–255 and only for a dimmable light; position 0–100 and only for a positionable cover; a missing required value. A refusal is an `ArgumentException` whose message names the entity and the value ("Target temperature takes a number from 5 to 30, not 45"), and the state is unchanged. A state a class sets is checked the same way where it has limits.
- `SharpDomains.Graph` has a row for each (state check and keep per state key, with the same limits; `Create` reads the options).
- Document checks: options of type `number` and `boolean`; an option marked required must be present and, for an array, not empty; `min` must be below `max`; an empty `options` array is refused for a sensor too. State check: a sensor with a device class other than `date`, `timestamp` or `enum` and no options takes a number or numeric text.
- `EntityDomainTests` also pins: every command's `data` names to what the typed class reads, and `required: false` state values to nullable ones the class may leave unset.

- [ ] **Step 1: Write the failing tests:** for each class — describe JSON (domain, commands, options), snapshot values, each command reaching override and delegate with its data, each refusal by message, state unchanged after a refusal; `EntityDomainTests` for the six rows; document checks (each new refusal; a valid document per domain); a host test that runs a class definition holding one of each type through commands with data.
- [ ] **Step 2: Run** `dotnet run --project tests/Runtime --disable-build-servers -- --sharp-entities --entity-domains --entity-store --entity-host`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the full runtime suite. Expected: exit 0; the generated-source pins unchanged.
- [ ] **Step 5: Commit** `Add button, number, select, text, light and cover entities to .NET and the catalog`.

### Task 3: Optional state values and command data in graphs (C#, TypeScript, fixture)

**Files:**
- Modify: `V/VisualNodeCatalog.cs` (l.311, l.432-439), `V/VisualGraphValidator.cs` (entity checks), `V/VisualRuntime.cs` (l.591, l.745-786), `V/TriggerPayload.cs` / `D/DefinitionHost.Graph.cs` (`RunFlow`: filling absent data), `fe/visual/contract.ts` (l.71, l.446, l.532-537, entity checks), `tests/fixtures/visual-graph-validation.json`
- Test: `tests/Runtime/EntityGraphRuntimeTests.cs`, `tests/Runtime/EntityGraphTests.cs`, `tests/Runtime/VisualGraphContractTests.cs` (runs the fixture), `ft/visual-contract.test.ts`, `ft/entity-roots.test.ts`
- Read: map sections 4, 5, 9.

**Interfaces:**
- Consumes: the rows of Task 2.
- Produces:
  - `entity_state`: one input per state value; required unless the row says `required: false`; at least one input supplied. `set_entity_state`: one input per state value, none required, at least one supplied (`entity.no_value`: "Set state needs at least one value"). Either on an entity whose type has no state: `entity.stateless` ("A button has no state to set"). The runtime passes only supplied values to `IDefinitionRuntime.SetState`.
  - `entity_command`: one output per data value of `config.command`, named as in the row, typed by its `types`; none for a command without data. The run reads it from the trigger payload at `/data/<name>`. `RunFlow` fills an absent value from the entity's current state value of the same name when it has one; otherwise the output is absent and reading it fails the step with the wording an absent trigger output has.
  - TypeScript `EntityDomainSpec` carries `required?` on state values and options, and `data?` on commands; `dataOutputs` and `dataInputs` mirror the C# rules.
- Existing graphs for binary sensor, sensor and switch validate and run exactly as before (their only state value stays required for State; a Set state with its one value is unchanged).

- [ ] **Step 1: Add fixture cases** (valid and invalid, both validators must agree): a light's State with only `is_on`; a cover's State with neither value (invalid); Set state with one of two values; Set state with none (invalid); State and Set state on a button (invalid); When commanded for `set_value` wired from its `value` output into a Set state; an output name the command does not have (invalid); `turn_on` → `brightness` output typed number.
- [ ] **Step 2: Write the failing runtime tests:** `SetStateKeepsWhatItIsNotGiven` (light: brightness set, then `is_on` alone → brightness kept); `ACommandHandsItsDataToTheFlow` (number `set_value` 21.5 → Set state → pushed value 21.5; select; text; cover `set_position`); `AbsentBrightnessIsTheCurrentOne` (turn_on without data → the flow reads the light's brightness; with none stored → the step fails by name and the command is refused); `AButtonRunsItsFlow`; a secret wired into a Set state is still traced hidden.
- [ ] **Step 3: Run** the runtime suite and the frontend suite. Expected: the new cases FAIL in both.
- [ ] **Step 4: Implement** in both validators and the runtime.
- [ ] **Step 5: Run** the runtime suite, the frontend suite and `bun run build`. Expected: PASS; pins unchanged.
- [ ] **Step 6: Commit** `Let a graph set part of an entity's state and read a command's data`.

### Task 4: Six Home Assistant platforms (Python)

**Files:**
- Create: `button.py`, `number.py`, `select.py`, `text.py`, `light.py`, `cover.py`; `tests/python/test_entity_contract.py`
- Modify: `const.py` (`PLATFORMS`, in the catalog's order), `entity_base.py` only if a shared helper is needed, `tests/python/test_entry_lifecycle.py` (l.10), `tests/python/test_entity_bridge.py` (fakes for the new base classes), `tests/python/test_entity_platforms.py`, `tests/packaging/entity_bridge_smoke.py` (one class entity and one graph entity of a new type)
- Read: map sections 13, 14; the base classes in Home Assistant 2026.11.1 with the `docker run` command in Global Constraints (`ButtonEntity`, `NumberEntity`, `SelectEntity`, `TextEntity`, `LightEntity` and `ColorMode`, `CoverEntity` and `CoverEntityFeature`).

**Interfaces:**
- Consumes: the sync description's `options` and the snapshot's `values`, as `sensor.py` reads them; `HassSharpEntity.command(name, **data)`.
- Produces, per module (thin, as `switch.py`):
  - `button`: press → `command("press")`.
  - `number`: options → native min, max, step, mode, unit; `values["value"]` → native value; set → `command("set_value", value=value)`.
  - `select`: `options["options"]` → options; `values["option"]` → current option; select → `command("select_option", option=option)`.
  - `text`: options → native min, max, pattern, mode; `values["value"]` → native value; set → `command("set_value", value=value)`.
  - `light`: `options["dimmable"]` → colour mode brightness, else on/off; `values["is_on"]`, `values["brightness"]`; turn on → `command("turn_on", brightness=…)` with the key only when Home Assistant gave one; turn off.
  - `cover`: features open, close, stop, and set position when `options["positionable"]`; `values["position"]` → current position; closed = `values["is_closed"]`, else position 0 when a position is known, else unknown; open/close/stop/set position → `command("open" | "close" | "stop" | "set_position", position=…)`.
- `tests/python/test_entity_contract.py` (runs without Home Assistant): for every domain of the catalog's `entityDomains` there is a platform module, it is in `PLATFORMS`, the command names it sends are exactly the row's commands, and every data keyword it sends is one of that command's data names.

- [ ] **Step 1: Write the failing tests:** the contract test; in `test_entity_bridge.py`, each platform's `apply` and `apply_values` against fakes; in `test_entity_platforms.py` (real Home Assistant), for each platform: the entity appears with the right features and attributes, a state push shows, each service call reaches the command with its data, a refused command surfaces as a `HomeAssistantError` with the .NET message.
- [ ] **Step 2: Run** the Python suite on the host (the platform tests skip) and in the container as `AGENTS.md` describes. Expected: the new tests FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the Python suite on the host and in the container, and `python3 -m unittest discover -s tests`. Expected: OK; none skipped in the container.
- [ ] **Step 5: Commit** `Add the button, number, select, text, light and cover platforms`.

### Task 5: Restored state (C#)

**Files:**
- Create: `D/EntityStateStore.cs`
- Modify: `D/DefinitionHost.cs` (`Publish`, class prepare, stop), `D/DefinitionHost.Graph.cs` (`PrepareGraph`, `Carry`), `D/SharpEntity.cs` (`RestoreState`, an internal `Restore(JsonObject values)`), the typed classes (their `Restore`), `D/EntityApi.cs` / `D/EntityLifecycle.cs` (forget on delete, on an entity leaving a definition, on prune; flush on unload), `Manager.cs` (the store's lifetime), `V/NodeLibraryApi.cs` (the path beside `AuthoringRoot`)
- Test: `tests/Runtime/EntityStateStoreTests.cs` (new, flag `--entity-restore`), `tests/Runtime/EntityGraphTests.cs`, `tests/Runtime/EntityHostTests.cs`, `tests/Runtime/EntityStartupTests.cs`
- Read: map sections 5, 6.

**Interfaces:**
- Produces:
  - File `.storage/hass_sharp/entity_states.json`: `{"version": 1, "states": {"<uniqueId>": {"values": {…}, "utc": "<ISO>"}}}`.
  - `EntityStateStore`: `Remember(string uniqueId, JsonObject values)`, `bool TryGet(string uniqueId, out JsonObject values)`, `Forget(IEnumerable<string> uniqueIds)`, `Task FlushAsync()`; `static TimeSpan FlushInterval` (10 s; tests shorten it). Written through a temporary file and a rename; a file that cannot be read is logged once as a warning and treated as empty.
  - `SharpEntity.RestoreState` (`bool`, `init`; default by type as decision 5).
- Behaviour:
  - Remembered: the values of a restorable entity each time it publishes while its definition runs and it is available.
  - Graph: after `Carry`, each restorable entity that got nothing from the running revision takes its stored values, key by key, through the domain's check; a refused value is dropped (debug log). Then the When started flow runs and may set another.
  - Class: before `Setup()`, each entity with `RestoreState` takes its stored values without publishing; `Setup()` and `Start()` see them and may change them.
  - Forgotten: when a definition is deleted, when an entity leaves a definition, and for ids no definition has at startup.
  - The two gaps of the carry between revisions (phase 2 hand-off): a value the old revision sets while the new one is being prepared reaches the new one unless the new one has set that value itself; availability is not carried for an entity the old revision worked out with a State.

- [ ] **Step 1: Write the failing tests:** store — round trip through a new instance, write-behind (one write for many changes, a write on `FlushAsync`), a corrupt file, `Forget`; `AGraphSwitchComesBackOn` and `ANumberSurvivesARestart` (save, command, dispose the manager, start a new one over the same directory: the first pushed state is the stored one, before any command); `WhenStartedOverridesARestoredState`; `ARefusedStoredValueIsDropped` (a select whose stored option was removed starts unknown and the definition runs); `AStateStepsEntityIsNotRestored`; class — a `SharpNumber` comes back, a sensor does not unless `RestoreState = true`, `Setup()` sees the restored value; forgetting on delete and on a removed entity; the two carry cases.
- [ ] **Step 2: Run** `dotnet run --project tests/Runtime --disable-build-servers -- --entity-restore --entity-graph --entity-host --entity-startup`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the full runtime suite, including `--reload-memory`. Expected: exit 0.
- [ ] **Step 5: Commit** `Bring entities back with the state they had`.

### Task 6: Failures that say so once, statuses, setting ranges and titles (C#, small panel changes)

**Files:**
- Modify: `D/DefinitionHost.Graph.cs` (`FlowFailed`, a slow-State warning), `D/DefinitionHost.cs` (`Every` failures l.510-521), `D/EntityApi.cs` (l.27-48 status, l.145-188 the log on save), `D/EntityLifecycle.cs` (a library save's `definitions`), `D/SharpComponent.cs` (`SettingAttribute.Minimum`/`Maximum`), `V/NodeLibraryCompiler.cs` (l.691-772), `D/DefinitionSettings.cs`, `D/DefinitionGenerator.cs` (l.86-122), `V/NodeAttribute.cs` (allow it on a class), `fe/views/NodeLibraryView.tsx` (the rebuilt line), `fe/entities/DefinitionCard.tsx` and the editors' status texts only if `failed` needs a word they lack
- Test: `tests/Runtime/EntityGraphTests.cs`, `tests/Runtime/EntityHostTests.cs`, `tests/Runtime/EntityApiTests.cs`, `tests/Runtime/EntityTypeTests.cs`, `ft/NodeLibraryView.test.tsx`, `ft/EntitiesListView.test.tsx`
- Read: map sections 7, 8.

**Interfaces:**
- Produces:
  - A trigger flow or a class's `Every` callback that fails logs an error once per new reason (naming the definition and the flow), repeats at debug, and logs one info line when it works again — the pattern `StateEnded` has, keyed by flow.
  - A State that has not finished after 30 s logs one warning naming the entity; none when it then finishes late (debug).
  - Status: `failed` with `activationError` when a definition is on and has no running host because it could not be prepared (setup failed, does not compile, its library is broken); `off` only when it is turned off. The failure is logged as a warning when a save or a library save meets it (once per reason), as an error at startup as today.
  - A library save's `definitions` lists every definition it rebuilt, each with `enabled`; the panel says "Rebuilt N entity definitions" and, when some are off, "(M are off)".
  - `[Setting(Minimum = 1, Maximum = 3600)]` on `int`, `long` and `double` properties (`double.NaN` = not set): answered by `types`, checked on save and when applied. An `int` without them keeps today's bounds.
  - A class's title: `[Node("Shelly plug")]` on the class, else the type's name in words ("Shelly plug" for `ShellyPlug`).

- [ ] **Step 1: Write the failing tests:** `AFailingPollLogsOncePerReason` (three failures, one error line; a second reason, a second line; success, one info line); the same for `Every`; `ASlowStateWarnsOnce`; `ADefinitionThatCannotStartIsFailed` (graph When started failing, class `Setup()` throwing, a compile error: `status`, `activationError`, one warning) and `…IsOffWhenTurnedOff`; the library save listing a definition that is off; ranges (types answer, save refusal by message, apply) and titles; the panel's two texts.
- [ ] **Step 2: Run** the runtime suite and the frontend suite. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** all five suites. Expected: PASS.
- [ ] **Step 5: Commit** `Log a failing poll once, call a definition that cannot start failed, and let settings declare a range`.

### Task 7: HTTP and downloads for classes (C#)

**Files:**
- Create: `D/SharpHttp.cs` (the shared handler and client factory), `D/SharpFiles.cs`, `tests/Runtime/LoopbackServer.cs` (a test HTTP server on `HttpListener`, 127.0.0.1, a free port; scripted responses, delays, dropped connections; records requests), `tests/Runtime/EntityToolsTests.cs` (flag `--entity-tools`)
- Modify: `D/SharpComponent.cs` (`IComponentHost.Http`, `.Files`, `protected HttpClient Http`, `protected SharpFiles Files`), `D/DefinitionHost.cs` (ownership, disposal after `Stop()` in `StopCoreAsync`'s `finally`, the setup clock), `V/NodeLibraryApi.cs` (the `files` directory), `tests/Runtime/SharpEntityTests.cs` (`RecordingHost`), `tests/Runtime/ReloadMemoryTests.cs` (the device also makes a request to the loopback server and holds a request open across a reload)
- Read: map sections 2, 6, 8, 12.

**Interfaces:**
- Produces:
  - `SharpHttp.Handler` — one process-wide `SocketsHttpHandler`: `MaxAutomaticRedirections = 5`, `AutomaticDecompression = All`, `ConnectTimeout = 10 s`, `PooledConnectionLifetime = 2 min`, no certificate callback. `SharpHttp.Create()` → `HttpClient` over it (`disposeHandler: false`), `Timeout = 30 s`, `User-Agent: HassSharp`.
  - `SharpComponent.Http` — the definition's client: created on first use, the same instance for the device and its entities, disposed after the last `Stop()`; using it after the definition stopped throws `ObjectDisposedException`. Requests should pass `Stopping`.
  - `SharpFiles.Download(string url, string sha256, string? name = null, CancellationToken cancellationToken = default)` → `Task<string>` (the file's path). `sha256` is 64 hex characters (any case); `name` defaults to the URL's last path segment, else `file`, and must be a plain file name. Path: `.storage/hass_sharp/files/<sha256 lower case>/<name>`. Present → returned without a request. Otherwise downloaded to a temporary file in the same directory, hashed while written, renamed into place only when the hash matches; a mismatch deletes it and throws `InvalidDataException` ("The file from <host> does not match its checksum"). One download per checksum at a time across the process; the others wait and then find the file. Receives nothing for 60 s → `TimeoutException`. Larger than 2 GiB → refused. Logs one info line when a download starts and one when it ends (size, seconds). Leftover temporary files in a checksum's directory are removed before a download starts.
  - The setup limit (60 s) does not count time during which a `Download` is receiving.
- `SharpFiles` exposes no other member in this phase.

- [ ] **Step 1: Write the failing tests** (against `LoopbackServer`): a class device polls with `Http` from `Every` and publishes what it read; a request in flight when the definition stops is cancelled and nothing is published after; the client is disposed after `Stop()` (a request made in `Stop()` still works); `Download` — fetches once and caches (second call makes no request), wrong checksum leaves nothing at the path, a dropped connection leaves nothing and a retry succeeds, two concurrent downloads of one checksum make one request, a stalled server times out (the stall limit shortened by the test), a bad `sha256` or `name` is refused before any request, a download slower than the setup limit (shortened) does not fail setup while one that receives nothing does; `ReloadMemoryTests` still collects every generation.
- [ ] **Step 2: Run** `dotnet run --project tests/Runtime --disable-build-servers -- --entity-tools --sharp-entities`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the full runtime suite including `--reload-memory`. Expected: exit 0.
- [ ] **Step 5: Commit** `Give definitions an HTTP client and checksum-verified downloads`.

### Task 8: The HTTP request and Download file steps (C#, TypeScript, fixture)

**Files:**
- Modify: `cat` (two node kinds), `V/VisualRuntime.cs` (two cases beside `call_service`; a `Guarded` helper replacing the repeated secret-guard idiom; `Compare`'s ordered fallback behind it; secrecy sources), `V/VisualGraphValidator.cs` and `fe/visual/contract.ts` (the two rules below), `tests/fixtures/visual-graph-validation.json`, `authoring_examples.json` (one automation using `http_request`), `fe/visual/nodeSummary.ts`, `fe/visual/icons.ts` if a new icon is needed
- Test: `tests/Runtime/EntityToolsTests.cs`, `tests/Runtime/EntityGraphRuntimeTests.cs`, `tests/Runtime/AuthoringAdapterTests.cs` (runs the examples), `ft/visual-contract.test.ts`, `ft/node-summary.test.ts`
- Read: map sections 10, 11, 12.

**Interfaces:**
- Consumes: `SharpHttp.Handler`, `SharpFiles.Download` (Task 7); `LoopbackServer`.
- Produces — `http_request` (label "HTTP request", category `Data`, any document, `execution: ["next", "error"]`):
  - config: `method` enum `GET|POST|PUT|PATCH|DELETE|HEAD` (default `GET`); `responseType` enum `string|object|array` shown as "Text", "JSON object", "JSON list" (default `string`); `timeout` number of seconds 1–300 (default 10); `failOnStatus` boolean (default true; label "Fail on an error status"); `hideResponse` boolean (default false; label "Keep the response out of run history").
  - inputs: `url` string, required; `headers` object, optional; `body` string, object or array, optional (an object or array is sent as JSON with `Content-Type: application/json` unless a header says otherwise; a body with `GET` or `HEAD` is an error).
  - outputs: `status` number; `body` typed by `responseType` (as Get field's `type` does it); `headers` object (lower-case names, repeated headers joined with ", ").
  - behaviour: `http` or `https` only; cancelled with the run; the response body is read up to 4 MiB (more is a failure); a status outside 200–299 fails the step with "HTTP 404 from 192.168.1.40" when `failOnStatus`; a body that is not the JSON shape asked for fails with a message that does not quote it; failures name the host, never the full URL or a header value. With an On error wire a failure takes it, as for any step.
  - secrecy: inputs fed by a secret are hidden (trace, messages); with `hideResponse`, `body` and `headers` are recorded hidden and are secret for the steps they feed, `status` is not.
- Summaries (`nodeSummary.ts`): "GET 192.168.1.40/rpc/Switch.GetStatus" (method, host and path of a literal URL; never a query string or a hidden value; "HTTP request" for a wired URL), "Download model.tflite".
- Produces — `download_file` (label "Download file", category `Entity`, `documents: ["entity"]`, `execution: ["next", "error"]`): inputs `url` string required, `sha256` string required; config `name` string optional; output `path` string. It is `SharpFiles.Download` with the run's cancellation.
- Rules in both validators: a literal `url` that is not `http://` or `https://` (`http.url`); a literal `sha256` that is not 64 hex characters (`download.checksum`).
- Every guard site of `VisualRuntime` goes through one helper; behaviour and messages unchanged (the existing secret tests pass unmodified); `Compare`'s ordered fallback cannot quote a secret.

- [ ] **Step 1: Add fixture cases** for both kinds (valid; bad literal url; bad literal checksum; body wired on a GET; `download_file` in an automation is invalid).
- [ ] **Step 2: Write the failing tests:** GET text, POST JSON object (the server sees the content type and body), JSON list, headers out; a 404 fails by message and takes On error when wired, and with `failOnStatus` off continues with status 404; timeout; a body over 4 MiB; not-JSON; a secret setting wired into `url` and into a header → trace and error hide it and name only the host; `hideResponse` → body hidden in the trace and in a Get field fed by it, status shown; a run cancelled mid-request; a graph plug: a time-pattern trigger polls the loopback server and sets a sensor, a command posts and sets the switch; `download_file` in a When started flow caches and hands its path to a later step; `Compare` on a secret text against a number says the value is hidden.
- [ ] **Step 3: Run** the runtime suite and the frontend suite. Expected: FAIL.
- [ ] **Step 4: Implement.**
- [ ] **Step 5: Run** all five suites and `bun run build`. Expected: PASS; pins unchanged.
- [ ] **Step 6: Commit** `Add the HTTP request and Download file steps`.

### Task 9: The six entity types and the new steps in the panel (frontend)

**Files:**
- Modify: `fe/entities/EntityRows.tsx` (l.17-21, l.62-209), `fe/entities/formFields.tsx`, `fe/entities/model.ts`, `fe/entities/NodePickers.tsx`, `fe/entities/liveState.ts`, `fe/entities/DefinitionCard.tsx`, `fe/entities/EntityHelp.tsx`, `fe/components/CatalogPicker.tsx` (l.10-28 icons), `fe/visual/NodeConfig.tsx` / `InputRow.tsx` (optional inputs, command outputs — only where the generic rendering falls short), `fe/visual/trace/traceWords.ts`, `fe/styles/entities.css`
- Test: `ft/EntityDefinitionView.test.tsx`, `ft/EntitiesListView.test.tsx`, `ft/NodePickers.test.tsx` (or the file that holds the pickers' tests), `ft/ClassDefinitionEditor.test.tsx`
- Read: map section 15; the phase 3 plan's "For phase 4" panel items.

**Interfaces:**
- Consumes: the rows (Task 2), the contract (Task 3), `entityTypeLabel`/`entityCommandLabel` (Task 1), the two node kinds (Task 8).
- Produces:
  - The entity form offers all nine types. Options render by type: number (text field read as a number, as the class page's number fields), boolean (switch), enum (select), array (one per line), string; a required option says so and blocks Save while empty; hints for every option. Changing an entity's type drops options the new type does not have (same undo step, same notice as today).
  - Set state and State show optional values as optional ("Leave empty to keep it as it is" / "Optional"); When commanded shows its command's data as outputs; a step on a button says it has no state.
  - Cards and the class page show a state per type: a light "On · 60 %" (brightness as a percentage of 255) or "Off"; a cover "Open · 40 %", "Closed"; a number with its unit; a select's option; a text's value (a password-mode text as dots); a button "Pressed 5 minutes ago" or "Never pressed". `liveState` keeps the attributes this needs.
  - The help text names the nine types and has a short paragraph on HTTP requests, downloads (where files are kept) and restored state.
  - Handed-over panel items: a problem without a form row is shown at the top of the panel while a step is selected; the pickers' "Choose an entity first." and "no entities yet" hints point at the form; a stored state value hidden after its entity was removed is cleared with it.

- [ ] **Step 1: Write the failing tests:** a definition with one entity of each new type built in the form and saved (the request's options have the right types); a required option blocks Save; retyping drops options; the pickers for a light's Set state (optional brightness) and a number's When commanded (value output) wire into a valid graph; card states for each type from live events; summaries; the three handed-over items.
- [ ] **Step 2: Run** the frontend suite. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the frontend suite and `bun run build`. Expected: PASS.
- [ ] **Step 5: Commit** `Edit buttons, numbers, selects, texts, lights and covers in the panel`.

### Task 10: Build, live check and suites

**Files:**
- Create (scratchpad, not the repository): `livecheck5.py`, `mock_plug.py`
- No product code. A defect found here is reported, not fixed.

- [ ] **Step 1:** Publish (`dotnet publish -c Release ./custom_components/hass_sharp/HassSharp -o ./custom_components/hass_sharp/out`), build the frontend, restart `hass` once and read its log; start a fresh `hass_wt` and onboard it as the earlier live checks did.
- [ ] **Step 2: `livecheck5.py`** over the websocket against `hass_wt`, with `mock_plug.py` (a small HTTP server run inside the `hass_wt` container: a relay, a power reading, a counter of requests, an endpoint that fails on demand, a file to download with a known checksum):
  - a graph plug polled by `http_request` every 2 s: the sensor follows the mock's power; turning the switch on posts to the mock; with the mock failing, one error line per reason and one recovery line in the log;
  - the same plug as a class using `Http`;
  - one graph entity and one class entity of each new type: each service call (`number.set_value`, `select.select_option`, `text.set_value`, `light.turn_on` with and without brightness, `cover.set_cover_position`, `button.press`) reaches the definition and the state shows in Home Assistant; a value out of range is refused with the .NET message;
  - restore: set a number, a select, a light and a graph switch, restart `hass_wt`, and read their states before sending anything: unchanged; then reload the integration: unchanged;
  - `download_file` in a When started flow and `Files.Download` in a class's `Setup()`: the file is at the path with the right checksum, a second start makes no request (the mock's counter), a wrong checksum leaves the definition `failed` with the reason and no file;
  - statuses `failed` / `off`; a `[Setting(Minimum…)]` range answered by `types`; a class's title.
- [ ] **Step 3:** All five suites, the Python suite in the `hass` container, both packaging smokes. Save the outputs to the scratchpad.
- [ ] **Step 4: Browser walk-through** in `hass_wt` (by the coordinating session): build the HTTP plug with nodes only; add a number and a light to it; watch the cards; narrow width; the automation editor offers HTTP request and still saves.
- [ ] **Step 5:** Report. No commit.

## Carried forward

Stays in the earlier plans' lists until its phase: phase 6 (MCP tools and docs, `run`, the `via` form, the wording items) and the untied and test items of phases 2 and 3. Not taken by this phase, from the "For phase 4" lists: preparing definitions off the startup path (decision 9); cleaning up unused downloads; more light and cover features; `ValueJson`'s rare number types; the trace page's opening-focus test and the pickers' test gaps beyond what Task 9 touches.

## Outcome of phase 4, and what it hands on

Phase 4 ended at `73f78c9` (20 commits after `6acc701`: Tasks 1–9 with their fix rounds, one fix wave after the whole-phase review and one follow-up to it; Task 10 was the build and live check). All five suites pass (runtime 483 PASS lines with no compiler warnings, editor 22, host Python 176 with 50 skipped, packaging 5, frontend 1754 tests in 70 files); in the Home Assistant 2026.11.1 container the Python suite passes with none skipped (188) and both packaging smokes pass. A scripted live check over the websocket and a walk-through in the browser against a real Home Assistant passed: a plug built from nodes alone (a 2 s trigger, an HTTP request to a mock plug, Get field, Set state) and one built from a class with `Http`; a number, a select, a text, a light and a cover commanded from Home Assistant; a number and a light that keep their state across a restart and across a save; a file downloaded at setup with its checksum, and the same definition failing in plain words when the server is down.

Where the text above differs from what was built, this section is right:

- **Names (Task 2).** `NumberMode`, `TextMode` and the cover's `Stop()` are `SharpNumberMode`, `SharpTextMode` and `StopMoving()`/`OnStopMoving`; the command on the wire is still `stop`. The first two would have collided with the generated `HassSharp.Entities.NumberMode`/`TextMode`, the third with the lifecycle `Stop()`.
- **Decision 3, command data.** A command's data value may carry a `default` in its catalog row. A value Home Assistant did not send takes the entity's current state value of that name, else the row's default when the entity accepts it, else it is absent. `light.turn_on.brightness` has `"default": 255`, so a dimmable light turned on for the first time comes on full. A graph entity's command data is checked like a class's before its flow runs.
- **Decision 5, restore.** Stored state is applied on every start of a definition, not only after a restart, so a class definition keeps its values across a save as a graph does. A value the entity's options no longer accept (a removed select option, a narrowed range, a light that is no longer dimmable) is dropped, in .NET and on the Home Assistant side.
- **Decision 6, HTTP.** Three process-wide handlers share the settings (certificates checked, 10 s to connect, no cookies): one that decompresses and follows redirects for `Http`; one without decompression for downloads, so a checksum is of the bytes the server sends; one without automatic redirects for the HTTP request step, which follows them itself (at most 5; a 303, and a 301 or 302 after a POST, become a GET without the body; 307 and 308 keep method and body). A redirect to the same host keeps the step's headers; one to another host is followed only when the step supplied no headers, and otherwise fails saying so. With "Keep the response out of run history" on, the other host is not named.
- **Secrets.** The host and port of an address that comes from a secret setting are not secret: failures, traces and the download log line name them. Its path, query and user info, and the headers and body, are never shown. A file whose address came from a secret is stored under the name `file`. A password-mode text is not a secret (Home Assistant keeps it in clear); messages still do not quote it and the panel never renders it.
- **Decision 9, statuses and logging.** A definition that is on and cannot start answers `failed` with the reason and logs one warning, `Entity definition "<name>" (<id>) cannot start: <reason>`, once per reason — at save, at startup, on reload and on a retry, and for a definition that does not compile as for one whose setup fails. The reason is the plain outer sentence, never a stack or the generated file's path; the exception goes to Debug.
- **Global constraint on pins.** In `tests/fixtures/generated-source-pins.json` no existing line is changed or removed; a line is added for a new valid automation case, as the pins suite prints it (one was, for the HTTP request step).
- **Document checks added.** A number's step must be above 0, a text's longest length at most 255 and its shortest at least 0, a pattern must be one both .NET and Python accept. Option problems name the option (`portId`) and speak in labels and the entity's name; `tests/fixtures/entity-option-problems.json` pins the sentences for C#, the panel and the panel's test backend.
- **Set state in the panel.** What is drawn is what is saved: an on/off value that is not set shows "Not set" with "Set value", never a switch; choosing an entity for a step that sets nothing yet sets its first on/off value to On in the same edit.

### For phase 5 (camera)

- The camera is the tenth row of the four tables `tests/Runtime/EntityDomainTests.cs` pins (typed class, `SharpDomains.Graph`, the catalog's `entityDomains`, the platform module) and of `tests/python/test_entity_contract.py`; the panel's form, cards and live state read the catalog row.
- Image steps that fetch use the HTTP request step's handler and its rules (redirects, size limit, secrets); a model or font downloaded at setup uses `download_file` / `Files.Download`.
- Ask the user for the project's licence and add `LICENSE` before ImageSharp goes in.
- First, two leftovers of this phase's last review, in `HassSharp/Devices/EntityLifecycle.cs`: `Located` cuts `"$library:"` off a source-map id without checking that the id starts with it (a graph step's id shorter than nine characters would throw inside the startup catch and stop the integration from starting; reachable only by a compile error the preview did not see), and the startup warning for a definition with many compiler errors is no longer capped at three and "and N more".

### For phase 6 (MCP and docs)

In addition to the earlier plans' lists:

- MCP: `llm.py`'s node schema knows only `dynamicOutputs: "trigger"` (not `"command"`) and does not carry `entityDomains`; `http_request` and `download_file` need authoring examples for tools.
- Docs, tools: `Http` keeps no cookies; the host of a secret address is shown; a checksum is of the file as the server sends it (not of what a browser saves after decompression); a file already in the cache is trusted without hashing it again and unused files are never cleaned up; a redirect to another host fails a step that has headers.
- Docs, behaviour: a save answers `running` up to about a quarter of a second before Home Assistant shows the entities as available, and a service call in that window does nothing; Home Assistant checks number, select and text values and cover positions itself, clamps brightness and drops it for a light that cannot be dimmed, before .NET sees the command; a value the command did not send but the step shows (brightness 255) appears in the trace as if sent; a class number or text whose `Min` is above its `Max` refuses every value; a class light's first turn-on without brightness gives the class no brightness.
- Wording still in keys or misleading: the Trigger step's description says "Start the automation…" inside a definition (override in the frontend, not in the catalog); the stored-state warning's "entities start without them".

### Not tied to a phase

- A failing `Setup()` of a class definition is retried on every push of services or entity classes (it may make a network call each time; it is quiet now).
- The panel drops a server option problem when it has its own for the same option, even when the two say different things (the server's comes back once the panel's is fixed). `sensor.py` drops a live sensor's value on any `ValueError` when its description changes.
- For a definition, a storage failure while its source is regenerated and an unexpected failure in a retry still log the exception with its stack and path at Error; turning on a definition that does not validate records the reason without the warning; a failing Convert step says "Cannot convert conv.value to System.Double." (a port id and a .NET type; shared with automations).
- `download_file`'s name has no save-time check; one checksum under two names downloads twice; `{}` as headers still counts as headers for a redirect to another host; a HEAD step's body read by a step that runs when it did not says the wrong thing.
- Files that grew past comfort: `HassSharp/Visual/VisualRuntime.cs` (HTTP mechanics could move out), `HassSharp/Devices/DefinitionHost.Graph.cs`, `HassSharp/Devices/SharpControls.cs`. Test hardening the reviews listed (pattern anchoring cases, the download's missing `Accept-Encoding`, the `wait_until` secret guard, `WhatSetupSetsWins` for an entity added in `Setup()`).
- The whole-phase review was split in two (backend, panel) because the phase's diff was too large for one reader, and its fix wave got one follow-up round.
