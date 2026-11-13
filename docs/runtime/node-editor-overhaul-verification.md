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
   - Python on the host: 40 run, 10 skipped, 1 known error (`test_native_triggers` cannot import `homeassistant` on the host).
   - Python inside the container: 51 OK, 0 skipped.
   - Frontend: 763/763 in 36 files; `bun run build` and `build:preview` both succeed.
2. **Publish, build, restart:** `dotnet publish`, `bun run build` and `docker compose restart hass` all succeeded. The final restart was at 22:54:18Z.
   - The logs show no load errors. The only warning is HA's standard "custom integration not tested" message.
   - The `device_registry.devices` deprecation warning seen at 22:50 is gone after the `utils.py` fix, even after a `get_entities` call.
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
   - **View trace:** a read-only canvas with the green path and a "Done n ms" chip on each card.
     - Values show on the cards.
     - The run line reads "Entity changes · Started manually".
     - The trigger data includes `manual: true`.
   - **After restart:** Run now on the reference automation succeeded in 32 ms after the 22:49:39Z restart. The panel loaded again after the 22:54:18Z restart.
   - **360 px and dark theme:** checked in dark theme at 360 CSS px, using a 360-px iframe over the panel.
     - `scrollWidth` is 360 in every view, so there is no horizontal overflow.
     - Views checked: the editor (two-row top bar, Add step, peek bar, drawer), the bottom-sheet inspector, full screen (compact bar), run history, and the trace view ("This run" peek and sheet, Timeline).
     - The dark theme was also checked at desktop width in every other view.

## Dispatch checks

1. **SQLite smoke:** as in plan check 3.
2. **McpTests:** 10/10 pass inside the container, as part of the 51-test suite. All 10 are skipped on the host, where `homeassistant` is not installed.
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
     - A non-object response is refused by HA core (`core.py` accepts only dict responses). It reached the step's error output, and the run shows "Recovered".
     - A top-level `null` response cannot come from real HA, so that case was not made.
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
6. **Entity and field filters, compared with HA's picker:**
   - **Target domain:** the `light.turn_on` target matches HA: lights only.
   - **Feature bits:** the any-bit feature rule could not be compared on `light.turn_on`, because its target declares no `supported_features`. The rule is unit-tested in `registry.ts`.
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
   - **Panel:** "Done, 3 times" and "Showing its last run: Iteration 3 of 3".
     - Times shows "From Each number · Item" = 3, so iterations are 1-based.
     - The response shows "Hello Ana", times 3, and `api_token: Hidden`.
   - **Redaction:** the stored trace holds `"***"` and not the secret.
9. **Restart, then run:** as in plan check 4, "After restart".
10. **360 px and dark theme:** as in plan check 4. Covered the editor, bottom sheet, full screen, run history and trace view.

## Fixes made during verification

- **Service labels:** `services.py` merges HA's service translations (service, field and section names and descriptions) into a copy of the descriptions before sending them to .NET.
  - Built-in services previously showed keys.
  - Covered by `tests/python/test_services.py`.
- **Duplicate problems:** `frontend/src/visual/session.ts` listed a step-level problem twice, because the server sends `inputId: null` while local diagnostics leave the field out.
  - The top-bar count, badge and drawer now agree.
  - Covered by `tests/visual-session.test.ts`.
- **Trigger names in summaries:** the overview "Starts when" line, the peek-bar line and the Run now menu named trigger entities by their id in words. They now use registry names, as the cards do.
  - Changed files: `RunNow.tsx`, `Inspector.tsx` and `VisualEditor.tsx`.
  - Covered by `tests/editor-shell.test.tsx`.
- **Device registry deprecation:** `utils.py` now looks devices up with `async_get` and only iterates `devices`, which removes HA 2026.11's warning.
  - Covered by `tests/python/test_entities.py`, whose fake registry is now iterate-only.

## Open observations

- **Field filters:** service field `filter`s are not applied, so Add field offers fields the chosen target cannot use (dispatch check 6).
- **Trace labels:** the trace view labels service fields from the field key ("Hvac mode", "Brightness pct", "Who", "Times"). Snapshots carry no services section (contracts D2/D3), so the translated names are not available there.
- **Trigger switching:** switching a trigger from Entity to native State keeps a stale `config.entity` in the native config, and keeps the label "Entity changes".
- **Raw values:**
  - Enum option labels are raw values ("heat", "off").
  - Selector option translations are not applied.
  - The required-field message uses the service id ("notify.persistent_notification needs a value for Message").
- **360 px layout:**
  - "Add step" overlaps the canvas in full screen.
  - The trace canvas is not fitted to the width.
- **Catalog refresh:** the services catalog is not re-sent when the HA language changes.
- **`last_updated`:** manual trigger payloads carry `last_updated: null`, by design (`TriggerPayload.cs`).
