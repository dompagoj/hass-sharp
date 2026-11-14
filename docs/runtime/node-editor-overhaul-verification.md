# Node editor overhaul verification

Verified on 2026-11-13 against the local Compose Home Assistant only (`hass`, http://localhost:5001), Home Assistant 2026.11.1 on linux-musl-arm64, at HEAD `4288a8f` plus the working-tree fixes listed at the end. No remote Home Assistant was touched. Before changing anything, `/config` was backed up outside the repository.

Test fixtures added to the local instance only:
- `input_boolean.fake_heater` and `input_boolean.fake_dimmer_state`;
- template sensors `sensor.fake_room_temperature` and `sensor.fake_forecast` (a nested-list `forecast` attribute);
- `light.fake_dimmer`, a template light that supports brightness only;
- `climate.fake_thermostat`, a generic_thermostat with `hvac_modes` heat and off;
- three scripts:
  - `script.hs_task20_greet`, with fields "Who to greet" and "How many times", whose response includes an `api_token`;
  - `script.hs_task20_no_response`;
  - `script.hs_task20_text_response`, which returns a non-object.

## Plan checks

1. **Suites, final tree:**
   - .NET runtime harness: exit 0, 124 PASS.
   - `HassSharp.Editor.Tests`: 18 passed, 0 skipped.
   - Python on the host: 41 run, 10 skipped, 1 known error (`test_native_triggers` cannot import `homeassistant` on the host).
   - Python inside the container: 52 OK, 0 skipped.
   - Frontend: 763/763 in 36 files; `bun run build` and `build:preview` both succeed.
   - Fix round 1 changed only Python, so only the Python suites were run again. The .NET and frontend numbers are from the previous round, on unchanged code.
2. **Publish, build, restart:** `dotnet publish`, `bun run build` and `docker compose restart hass` all succeeded. The last restart was at 23:08:45Z, after the Python-only fix round 1.
   - The logs show no load errors. The only warning is HA's standard "custom integration not tested" message.
   - **Device registry:** the `device_registry.devices` deprecation warning seen at 22:50 is gone after the `utils.py` fix, even after a `get_entities` call (35 entities, 3 devices).
     - This ran against the container's Home Assistant, 2026.11.1. There, iterating `devices` yields device entries.
     - On older versions, such as 2025.12, iterating it yields device ids, so the new code needs 2026.11 or later. `hacs.json` already requires 2026.11.0.
   - "Received 250 Home Assistant service descriptions" came from a SyncWorker thread.
   - `/config/.storage/hass_sharp/visual` contained no stale v4 graphs, so there was nothing to remove.
3. **SQLite smoke** (`docker exec ... sqlite_smoke.py`): PASS on linux-musl-arm64, twice.
   - The live `runs.db`, read without writing, has 4 migrations. The last is `202611130004_RunTracing`, and it contains the `RunTraces` and `GraphSnapshots` tables.
4. **Reference automation** "Heat when motion": native State trigger on `binary_sensor.fake_motion` → `climate.set_hvac_mode` → `notify.persistent_notification` → `light.turn_on`.
   - **`light.turn_on` target and fields:** the target picker offers only lights (Fake Dimmer, Fake Light).
     - Brightness is a typed Number pin with a slider on the card and in the panel.
     - A card edit moved the panel value from 51 % to 15 %.
   - **`hvac_mode`:** a dropdown of the entity's own modes (heat, off). It was set to heat.
   - **Wrong-domain wire:** wiring the trigger's Entity output (Binary sensor) into the climate Target is refused with "Target requires Target / Climate entity; Entity outputs Binary sensor entity". No edge is added.
   - **Manual run:** the toast reads "Run started. Its result is under Runs." The history row reads "Succeeded · Entity changes · Manual run · Started manually · 33 ms · View trace".
     - A real trigger, from toggling the motion helper, is labelled "Home Assistant trigger · binary_sensor.fake_motion: off → on".
     - **Stale label:** the trigger label "Entity changes", in this row and on the trace's run line, is stale. The trigger is a native State trigger.
       - The saved graph still holds `label: "Entity changes"` and `config.entity: "binary_sensor.motion"`, both left over from before the trigger type was switched from Entity to native State.
       - So the run is labelled readably, but not correctly. See "Trigger switching" under Open observations.
   - **View trace:** a read-only canvas with the green path and a "Done n ms" chip on each card.
     - Values show on the cards.
     - The run line reads "Entity changes · Started manually".
     - The trigger data includes `manual: true`.
   - **After restart:** Run now on the reference automation succeeded in 32 ms after the 22:49:39Z restart. The panel loaded again after the 22:54:18Z restart.
   - **360 px and dark theme:** checked in dark theme at 360 CSS px, using a 360-px iframe over the panel.
     - Views checked: the editor (two-row top bar, Add step, peek bar, drawer), the bottom-sheet inspector, full screen (compact bar), run history, and the trace view ("This run" peek and sheet, Timeline).
     - `scrollWidth` is 360 in every view, so there is no horizontal overflow.
     - **Two layout defects at this width:**
       - in full screen, "Add step" overlaps the canvas;
       - the trace canvas is not fitted to the width.
     - **Not checked:** the iframe emulates only the width, not touch or `pointer: coarse`, so touch-only behaviour was not checked.
     - The dark theme was also checked at desktop width in every other view.

## Dispatch checks

1. **SQLite smoke:** as in plan check 3.
2. **McpTests:** 10/10 pass inside the container, as part of the 52-test suite. All 10 are skipped on the host, where `homeassistant` is not installed.
3. **Python.NET bindings:**
   - **Run-history status filter**, where Python passes a list or `None` to C# `string[]?`:
     - Failed → "No failed runs";
     - Succeeded → 1 run;
     - All (`None`) → the run is listed;
     - no errors.
   - **`UnloadAsync(hass.is_stopping)`** with the Python bool `False`, triggered by reloading the integration in Settings at 22:42:40: unload, setup and the description push all completed without errors.
   - **`SetServiceDescriptions`** runs on SyncWorker threads: at startup ("Received 246"), and on `script.reload` through the debounced push ("Received 250", SyncWorker_3, 22:29:20).
   - **Service responses:**
     - An empty-object response shows as "Object · 0 fields", and the log prints `{}`.
     - A script that returns a non-object is refused by HA core itself (`core.py` accepts only dict responses), so the call fails before any response reaches HassSharp. That failure took the step's error output, and the run shows "Recovered".
     - **Not made:** HassSharp's handling of a non-object response value was not exercised, because real HA never delivers one.
     - **Not made:** a top-level `null` response, for the same reason.
4. **Stop listener:** in "Long delay" (manual → Delay 300 s → log), Run now, then `docker compose stop` and `start`.
   - The run is "Cancelled", with the reason "Home Assistant stopping".
   - The Delay shows "Cancelled 16.5 s", and the log step shows "Not reached".
5. **Served services catalog compared with the fixture:** for `climate.set_hvac_mode`, `light.turn_on`, `notify.notify`, `script.turn_on` and `weather.get_forecasts`, the structure is identical.
   - Matched: types, required flags, enums (`color_name` 148 values), sections, min/max, units, selectors, filters, targets (weather feature bits 1, 2 and 4), and response (`only`).
   - **Labels before the fix:** built-in services showed keys (`set_hvac_mode`, `hvac_mode`).
   - **Labels after the fix:**
     - "Set thermostat HVAC mode" / "HVAC mode";
     - "Turn on light" / "Brightness" / "Brightness value";
     - "Send a persistent notification" / "Message".
   - **Runtime schema:** the `script.hs_task20_greet` runtime schema shows "Who to greet" / "How many times". Labels show on the cards and in the panel.
   - **Not made:** a fixture comparison for a `script.<id>`. `tests/fixtures/visual-services.json` has no `script.<id>` entry (its only script item is `script.turn_on`). `script.hs_task20_greet` was checked for labels only, not against a fixture.
6. **Entity and field filters, compared with HA's picker:**
   - **Target domain:** the `light.turn_on` target matches HA: lights only.
   - **Feature bits, not made:** the any-bit feature rule was not compared with HA.
     - `light.turn_on` cannot show it, because its target declares no `supported_features`.
     - `weather.get_forecasts` declares bits 1, 2 and 4, but the local instance has no weather entity.
     - The rule (implemented in `registry.ts`) is covered by `frontend/tests/registry.test.ts`, "filters entities by domain, device class and supported feature bits".
   - **Field filters:** these differ.
     - HA's Developer tools, with target Fake Dimmer (brightness only), show only Brightness, Brightness step and a collapsed "Additional options".
     - HassSharp's Add field also offers Transition, Color, Color temperature and Effect, because the frontend does not apply field `filter`s.
7. **Manual Run now:**
   - **Entity trigger on `sensor.fake_forecast`**, whose attributes hold nested lists: the run succeeded, and the trace shows the nested lists.
   - **Entity and native State triggers on `script.hs_task20_greet`**, which has a datetime `last_triggered`:
     - both appear in the Run now menu;
     - both runs succeeded with `platform`, `entity_id`, `from_state`, `to_state` and `manual`;
     - the datetime came through as an ISO string, with no warning or error.
8. **Run trace with a loop and a parallel branch:** the graph runs a foreach over [1, 2, 3] into greet, with `times` wired from the item. A parallel step follows: one branch logs an empty response, the other turns on the dimmer. Last comes the text-response script, whose error output leads to a log.
   - **Timeline:** 11 events, including "Iteration 1 of 3" to "Iteration 3 of 3", "Branch Left" and "Recovered".
   - **1-based numbering:** the stored trace (read from `runs.db` without writing) records the three greet events in frames `each#0:body`, `each#1:body` and `each#2:body`. The timeline shows those 0-based frames as "Iteration 1 of 3" to "Iteration 3 of 3", so the display is 1-based.
     - The item value cannot show this, because the list [1, 2, 3] holds the same numbers.
   - **Panel:** "Done, 3 times" and "Showing its last run: Iteration 3 of 3".
     - Times shows "From Each number · Item" = 3.
     - The response shows "Hello Ana", times 3, and `api_token: Hidden`.
   - **Redaction:** the stored trace holds `"***"` and not the secret.
9. **Restart, then run:** as in plan check 4, "After restart".
10. **360 px and dark theme:** as in plan check 4. Covered the editor, bottom sheet, full screen, run history and trace view.
    - Two layout defects: "Add step" overlaps the canvas in full screen, and the trace canvas is not fitted.
    - Touch input was not checked.

## Fixes made during verification

- **Service labels:** `services.py` merges HA's service translations (service, field and section names and descriptions) into a copy of the descriptions before sending them to .NET.
  - Built-in services previously showed keys.
  - If the translations cannot be loaded, for example because of a malformed translation file, a warning is logged and the descriptions are still sent with their own names (fix round 1).
  - Covered by `tests/python/test_services.py`.
- **Duplicate problems:** `frontend/src/visual/session.ts` listed a step-level problem twice, because the server sends `inputId: null` while local diagnostics leave the field out.
  - The top-bar count, badge and drawer now agree.
  - Covered by `tests/visual-session.test.ts`.
- **Trigger names in summaries:** the overview "Starts when" line, the peek-bar line and the Run now menu named trigger entities by their id in words. They now use registry names, as the cards do.
  - Changed files: `RunNow.tsx`, `Inspector.tsx` and `VisualEditor.tsx`.
  - Covered by `tests/editor-shell.test.tsx`.
- **Device registry deprecation:** `utils.py` now looks devices up with `async_get` and only iterates `devices`, which removes the warning on HA 2026.11.1. This needs 2026.11 or later; see plan check 2.
  - Covered by `tests/python/test_entities.py`, whose fake registry is now iterate-only.

## Open observations

- **Field filters:** service field `filter`s are not applied, so Add field offers fields the chosen target cannot use (dispatch check 6).
- **Trace labels:** the trace view labels service fields from the field key ("Hvac mode", "Brightness pct", "Who", "Times"). Snapshots carry no services section (contracts D2/D3), so the translated names are not available there.
- **Trigger switching:** switching a trigger from Entity to native State keeps the old `config.entity` and the label "Entity changes". The reference automation shows both (plan check 4, "Manual run").
- **Raw values:**
  - Enum option labels are raw values ("heat", "off").
  - Selector option translations are not applied.
  - The required-field message uses the service id ("notify.persistent_notification needs a value for Message").
- **360 px layout:**
  - "Add step" overlaps the canvas in full screen.
  - The trace canvas is not fitted to the width.
- **Language:**
  - The services catalog is not re-sent when the HA language changes.
  - The server translates in the server language. The service picker and field rows use `hass.localize` in the user's profile language. A user whose profile language differs from the server's can therefore see two languages: one on the cards and in summaries and server messages, another in the picker and the field rows.
- **`last_updated`:** manual trigger payloads carry `last_updated: null`, by design (`TriggerPayload.cs`).

## Phase 2 re-verification (2026-11-14)

Task 28. Run against the local Compose Home Assistant only (`hass`, http://localhost:5001, Home Assistant 2026.11.1, linux-musl-arm64), at HEAD `c2497af` plus the working-tree fixes listed below. No other instance was touched, and no `mcp__hass__*` tool was used. The browser was a real Chrome tab in dark theme.

**Backup.** Before anything was changed, `/config` was copied to `/private/tmp/claude-501/-Users-dompa-dev-HassSharp/a09c8e58-47d3-4c3e-9608-9e7ba968d1e9/scratchpad/hass-config-backup-task28` (1.5 GB, outside the repository).

**Changes made to the instance:**
- Script `hs_task28_wave` (a `how_often` field) was added to `/config/scripts.yaml` and left in place. The original file is `scratchpad/t28/scripts.before.yaml`.
- These automations were deleted through the websocket API after the backup:
  - "Heat when motion" from Task 20, replaced by its rebuild;
  - the three stale documents planted for check 5;
  - a typo test copy.
- Automations created and left on the instance:
  - "T28 Heat when motion" and its copy;
  - "T28 Custom code";
  - "T28 Loop and branches";
  - "T28 Library nodes".
- Node library file `Nodes.cs` was created and left on the instance.

### Checks

1. **Suites.**
   - Start: BE exit 0, 154 PASS; Editor 11; FE 919 in 41 files; PY on the host 46 run, 11 skipped, 1 known error.
   - End, after the fixes:
     - BE exit 0, 154 PASS;
     - Editor 12;
     - FE 929 in 41 files;
     - PY on the host unchanged: 46 run, 11 skipped, 1 known error (`test_native_triggers` needs `homeassistant`).
   - `dotnet publish` and `bun run build` were run after the last source change.
2. **Publish, build, restart.** The log has no load error, no `hass_sharp` traceback, and no "AssemblyLoadContext is unloading".
   - Setup to automations active ("Async setup entry" to "Currently tracking"):

     | Time (UTC) | Duration | Automations | Service schema |
     |---|---|---|---|
     | 06:06:03.231 to .949 | 0.72 s | 2 | none saved yet |
     | 07:01:28.287 to 29.448 | 1.16 s | 6 | persisted |
     | 07:15:04.451 to 05.676 | 1.23 s | 6 | persisted |

   - Memory (`docker stats`):

     | Restart | Before | After |
     |---|---|---|
     | 06:05 | 883.6 MiB | 676.2 MiB |
     | 07:01 | 940.6 MiB | 710.9 MiB, then 719.6 MiB |
     | 07:15 | not recorded | 697.6 MiB |

3. **Packaging smokes in the container.**
   - `sqlite_smoke.py`: PASS (4 migrations, linux-musl-arm64).
   - `visual_values_smoke.py` against `out/HassSharp.dll`: both PASS lines.
4. **Python suite in the container:** 57 run, OK, 0 skipped. This includes the MCP adapter tests for `set_enabled`, `deriveSignatures` and `entity.unknown`.
5. **Saved automations from before.**
   - The instance held two, "Long delay" and "Heat when motion", and both still validated.
   - To test this check, three stale v5 documents were planted:
     - Custom C# statement code;
     - variables, `imports` and `config.output`;
     - a library kind with a parameter list.
   - After a restart, all three cards read "Not active: 1 problem", and the others loaded.
   - The editor and `read` gave messages a person can act on:
     - "Usings are no longer part of an automation; put using lines at the top of a Custom C# node's code";
     - "Message reads a variable, and variables are gone; connect a wire to it instead";
     - "Steps no longer expose values to C# by name; connect a wire from this step to each step that uses its value";
     - "This step is no longer in its node library; delete it and add it again from the node library".
   - The log said only "Cannot load visual document … Graph validation failed", without the reason. This is reported.
   - The planted documents were deleted through the API.
6. **Automations page.**
   - Cards show:
     - a thumbnail;
     - what starts it ("When Fake Motion becomes On", "Run manually");
     - run dots;
     - health ("Not active: 1 problem").
   - Search ("motion", "manually") works, and so do the filters and Sort (Recently run, Name, Recently changed).
   - **Switch off, "Long delay":** the running run was cancelled with "Automation turned off".
   - **Switch off, "T28 Heat when motion":**
     - toggling the motion sensor started no run;
     - Run now still ran it, and the log shows `climate.set_hvac_mode`, `notify.persistent_notification` and `light.turn_on`.
   - **Restart while off:** the switch was still off, and the trigger still did not run it.
   - **Switch on:** the trigger fired again without a save.
   - **Duplicate:** the copy was created off, with the notice "Copy of T28 Heat when motion was created turned off."
   - No "Refresh entity IDs" appears anywhere.
7. **Reference automation, rebuilt from scratch.**
   - Steps: a native State trigger on `binary_sensor.fake_motion` to on, then `climate.set_hvac_mode` (Fake Thermostat, Heat), then `notify.persistent_notification`, then `light.turn_on`.
   - **Value nodes:** Constant 21 fed Compare (> 18), which fed If.
     - They have no step-order pins.
     - The trace lists trigger, constant, compare, if and three services, all ok.
   - **`light.turn_on`:**
     - Only lights were offered.
     - For the brightness-only Fake Dimmer, Add field offered only Brightness, Brightness step, Brightness value, Brightness step value and Profile.
     - HVAC mode options read "Heat" and "Off".
     - The run sent `brightness_pct: 61`.
   - **Trigger type change** on the copy (Time, then Every day, then back to Time, with time zone UTC): the settings reset, and it previewed and saved as revision 2 with `at: "06:45"`.
   - **Mistyped entity** `binary_sensor.fake_motoin` in the trigger: a warning, "Home Assistant has no entity … Check the spelling: a trigger on it never fires."
     - Save works.
     - Websocket `preview` gave the same `entity.unknown` warning. The MCP adapter path is covered by the container Python test.
     - Before the fix, a native trigger's `entity_id` got no warning (fixed).
8. **Custom C# node.**
   - **Template and editor:** the template's pins appear at once, and Edit code opens full screen.
   - **Completion:** works on `context.`, on `context.Services.` (domains), and on `Light.` (Toggle, TurnOff, TurnOn).
   - **Hover:** shows the service description.
   - **Signature help:** shows fields with units and ranges ("From 0 to 100 %"). The text "or a ServiceTarget for areas" lost the cref name before the fix.
   - **Diagnostics:**
     - The mistyped `TurnOnn` gives "'LightServices' does not contain a definition for 'TurnOnn'" on line 4.
     - A missing `await` gives warning CS4014.
   - **Format:** works. Before the fix it indented everything one level.
   - **"Dim a light"** (`string light`, `double brightness`, returning the tuple `(State, Brightness)`):
     - Apply showed the new pins, and both outputs were wired to Logs.
     - The run succeeded in 30.6 ms. The log shows `light.turn_on` with `brightness_pct 35`, and the trace shows the inputs and both outputs.
   - **Second `CustomNode` class:** a second node that also declares `CustomNode` compiles.
   - **`Pure = true`:** the step-order pins go, the neighbours are joined, and the toast says so. Undo restores.
   - **Number wiring:**
     - Constant 2 into an `int` gives 4.
     - An `int` output into `long` and `double` parameters logged "wide 4 4".
     - Constant 2.5 fails at run time with "Twice · Count needs a whole number from -2147483648 to 2147483647, not 2.5".
     - A typed literal 2.5 is refused at once. It now says "Count must be a whole number between …"; before the fix it said "is not a valid Number value".
   - **Error details** inside Repeat plus Parallel name the inner step ("Inner step").
9. **Node library.**
   - `Nodes.cs` holds `LightUp`, which calls `Services.Light.TurnOn`, and a pure `PlusOne`. Both are used in "T28 Library nodes".
   - **Body change** (revision 2): the running automation was replaced without a restart, and "lib v2" was logged.
   - **Renaming a parameter** (`light` to `lamp`): the save is refused.
     - It now names "T28 Library nodes" and points to Workspace diagnostics.
     - Before the fix, it named no automation, listed ids, and left stale "Saved and active" lines.
   - **Planted invalid automation:** it did not block an unrelated library save (revision 3).
     - The "not rebuilt" list is now shown. It was never displayed before the fix.
10. **Typed services under Python.NET.**
    - **After the cold restart:** the first call from a Custom C# node (06:50) and from a library node (06:55:40) worked.
    - **Script reload during a run:** while "T28 Library nodes" waited in its 25 s delay, `hs_task28_wave` was added and `script.reload` called.
      - Service descriptions went from 250 to 251, and the run's second call succeeded.
      - `HsTask28Wave` and `HsTask28WaveWithResponse` appeared in completion.
      - A new library save calling `HsTask28Wave(howOften: 1)` ran, and the log shows `script.hs_task28_wave {"how_often":1}`.
    - **Restart with the persisted schema:** nothing failed to compile.
    - **HassSharp entry reloads:** every reload ended "loaded", with no traceback.
      - Memory after reloads 1, 2, 5, 10 and 15: 749.1, 760.6, 810.3, 838.7 and 869.9 MiB, against a 719.6 MiB baseline.
      - Readings settle within 1 MiB after each burst, but they keep climbing about 6–30 MiB per reload. This is reported.
      - cgroup at the end: anon 721,485,824, file 271,372,288, shmem 146,849,792 bytes.
11. **Runs and trace.**
    - Run history works.
    - "View runs" from a card opens the editor with the Runs drawer, and the address loses `?drawer=runs`.
    - The "T28 Loop and branches" trace (Repeat with a Parallel inside) opens readable and centred. Its timeline shows iterations and branches.
    - Service field labels are the real ones ("Brightness").
    - Null optionals read "Not set".
    - The Custom C# code opens read-only from the trace ("Cannot edit in read-only editor").
12. **Restart.** At 07:14:50 UTC:
    - all 6 automations were active with `currentRevisionActive`;
    - the switches held (Long delay and the copy off, the rest on);
    - Run now on "T28 Loop and branches" gave "Run started. Its result is under Runs.", and "branch a ran" and "Inner step" were logged;
    - the log is clean.
13. **360 px, dark theme.** Tested in a 360 CSS px iframe of the panel. With fullscreen disallowed, the editor uses its CSS full-screen fallback; with the browser Fullscreen API, it takes the whole window.
    - The page never scrolls horizontally (`scrollWidth` 360). The only content past the edge is canvas content, which pans.
    - **Automations page:** cards, filters and Sort fit.
    - **Editor:** the top bar, Add step and the canvas toolbar all fit.
      - In full screen, the toolbar's "Add a step" (+) is reachable.
      - The full-screen bar holds the name, Saved, undo, redo, Save and Exit. Run now and the switch are not in it.
    - **Code editor:** the header, ?, Cancel and Apply fit, and the pins panel scrolls. The "How to write a node" help fits and scrolls.
    - **Trace view:** the header, timeline drawer and canvas fit.
    - **Monaco aria container fix, live:**
      - it sits at `left: -13986px` with `clip-path: inset(50%)`;
      - the page scroll height equals the viewport;
      - no announcement text shows.
    - Light theme and touch input were not checked.
14. **MCP, through the websocket API from the panel console.** The integration's MCP tools could not be reached here without the `mcp__hass__*` tools, which may point at another instance.
    - `list` items carry `enabled`.
    - A Custom C# node given as source only, saved with `deriveSignatures: true`, had its pins derived.
    - `preview` refusals:
      - `imports`: "Usings are no longer part of an automation; put using lines at the top of a Custom C# node's code".
      - `config.output`: "Steps no longer expose values to C# by name; connect a wire from this step to each step that uses its value".
      - A `variable` operand: "Value reads a variable, and variables are gone; connect a wire to it instead".

### Fixes made (each with a test that failed first)

1. **Problems tip:** a card's problems tip was hidden under the next card. Changed `flow.css`; test in `fullscreen-layout.test.ts`.
2. **Native trigger warning:** a native Home Assistant trigger's `entity_id` got no `entity.unknown` warning. Changed `VisualApi.cs`; test in `ServiceCallTests.cs`.
3. **Remove ID:** an ID typed into an entity picker that is not in the list could not be removed. Added a Remove ID button in `CatalogPicker.tsx`; test in `widgets.test.tsx`.
4. **Signature help names:** signature help dropped `<see cref>`, `<see langword>` and `<paramref>` names. Changed `editor-actions.ts`; test in `editor-actions.test.ts`.
5. **Format indentation:** Format indented all code one level, because of the namespace wrapper. Changed `NodeLibraryLanguageServices.cs`; test in `NodeLibraryLanguageServicesTests.cs`.
6. **Monaco aria container:** the container in `document.body` was unstyled.
   - It made the Home Assistant page scroll, so focusing an input shifted the UI, and it showed its announcements at the top.
   - `panel-stylesheet.ts` (`injectGlobalStyle`) and `hass-sharp.ts` now hide it; test in `panel-stylesheet.test.ts`.
7. **Fraction message:** a fraction typed as the CLR int type said "not a valid Number value". It now says "must be a whole number between …".
   - Changed in `VisualGraphValidator.cs` and `contract.ts`.
   - Tests: a new shared fixture case, `CustomNodeTests.cs`, `NodeLibraryTests.cs` (expected text), and the pinned fixture totals in `connection-index.test.ts`.
8. **Value node preview:** a pure library node's preview showed an Execution row. It now reads "Value node". Changed `LibraryPreview.tsx`; test in `NodeLibraryView.test.tsx`.
9. **Library save naming:**
   - A refused save now names the automations it would break.
   - Activations and diagnostics show names, not ids.
   - Stale results clear when a save starts.
   - The "not rebuilt" list is shown.
   - Changed `NodeLibraryApi.cs`, `NodeLibraryView.tsx`, `library-api.ts` and `library-session.ts`.
   - Tests in `NodeLibraryView.test.tsx` and `CustomNodeTests.cs`.
10. **Template warning:** a new library file's template raised warning CS1591. Changed `library-session.ts`; test in `library-session.test.ts`.

### Open observations (Phase 2)

- **Memory grows with each reload:** about 10 MiB per HassSharp entry reload on average (719.6 to 869.9 MiB over 15), with no plateau. This needs investigation.
- **Constant Type list:** it offers 73 types, including CLR interfaces such as "Comparable", "Span parsable" and "Additive identity".
- **Startup log:** "Cannot load visual document X: … Graph validation failed" does not say why.
- **Missing `expectedRevision`:** a websocket `save` without it answers "Object reference not set to an instance of an object."
- **MCP refusal for `imports`:** the MCP schema refuses an `imports` key with a generic "Arguments do not match the tool parameter schema". This happens before the backend's actionable message.
- **Code editor:**
  - Completion on `context.` lists the `ToPython`/`ToPythonAs<>` extension methods.
  - Completion opens after `} ` at the end of a file.
- **Pure toggle toast:** "Log now follows Log." is ambiguous when two steps share a name.
- **Palette:** a step added from the palette is not wired to the selected step.
- **Editor top bar:**
  - Save stays enabled while the document is Saved.
  - At 360 px, the full-screen bar has no Run now and no switch.
- **Entry reload:** reloading the HassSharp entry sends an open panel to Overview.
- **Trace:**
  - In the "T28 Heat when motion" trace, the Compare card overlaps the taller State card.
  - The timeline shows three identical "Call service" rows.

## Reload memory (2026-11-14)

Task 29. This follows up the Phase 2 observation "Memory grows with each reload".

### Causes

1. **Python.NET (the main cause).**
   - `Python.Runtime.AssemblyManager` handles `AppDomain.AssemblyLoad`. It keeps every assembly in its static
     `assemblies` queue and `namespaces` table.
   - Those references kept every automation's collectible assembly, so no `ScriptLoadContext` was ever unloaded.
   - What stayed was the code (`shmem`, double-mapped JIT code), the type data (`anon`) and the image.
   - After 15 reloads, 133 collectible assemblies were loaded, for 7 live automations.
   - `PythonNetAssemblies.IgnoreCollectible` now passes Python.NET's handler only assemblies that are not collectible.
2. **Roslyn's process-wide `SyntaxNodeCache` and `StringTable`.**
   - These caches kept the large graph and catalog literals of each generation of generated source.
   - The literals are now emitted in 64-character pieces (`VisualGenerator.Pieces`).

### Regression test

`tests/Runtime/ReloadMemoryTests.cs` (`--reload-memory`) runs 13 reloads with Python.NET's assembly manager started.
It checks three things:

- every load context and assembly of an earlier generation is collected;
- no collectible assembly is left;
- managed memory grows at most 7 MiB from reload 3 to 13. It plateaus near reload 20.

### Method

Same method as Phase 2:

- **Reload:** `config_entries/reload` from the panel tab, 8 s apart.
- **Memory:** the third of three `docker stats` readings, 10 s apart.
- **Breakdown:** cgroup `anon` and `shmem`.

### Series

MiB, after a restart and then after reloads 1, 2, 5, 10 and 15:

| Build | 0 | 1 | 2 | 5 | 10 | 15 |
|---|---|---|---|---|---|---|
| Before | 715.0 | 762.1 | 801.5 | 846.9 | 883.7 | 881.8 |
| After | 702.2 | 737.5 | 766.2 | 778.7 | 798.4 | 806.9 |

- **Before:** `anon` went from 607.5 to 731.2 MB and `shmem` from 99.7 to 149.8 MB. `shmem` reached 161.9 MB at reload 20.
- **After:** `anon` went from 604.6 to 669.4 MB. `shmem` went from 89.5 to 133.8 MB and was flat from reload 10 (133.5).
- At reload 15, the collectible assemblies went from 133 before to 7 after. Live managed memory went from 52.3 to 44.5 MiB.

### Residual

- After the fixes, the container rises about 1.7 MiB per reload from reload 10 to 15.
- Managed memory and the assembly count are flat over those reloads, so the rise is native memory: the allocator and
  Roslyn's metadata.
- This series does not show whether the rise is bounded.
