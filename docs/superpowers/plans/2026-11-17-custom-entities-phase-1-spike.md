# Custom entities, phase 1 spike: findings

Verified on 2026-11-17 by running code in a throwaway container: Home Assistant 2026.11.1 (Python 3.14.10,
Alpine, arm64), Python.NET 3.1.0 with the runtime loaded as HassSharp loads it. Items marked **unverified**
were not run. The spike source was throwaway and is not in the repository.

## Rules for the bridge

1. **Threading.** Every Home Assistant call runs on the event loop. Calls from .NET threads go through
   `hass.loop.call_soon_threadsafe` and never wait for an answer. `async_write_ha_state`,
   `er.async_update_entity` and `dr.async_get_or_create` raise `RuntimeError` when called from another thread,
   and Home Assistant's thread check blocks the calling thread first: joining that thread from the loop hangs
   Home Assistant. Never block the loop on a .NET call that may call back into Home Assistant.
2. **Adding entities late works**, minutes after platform setup, on the loop. The callback given to
   `async_setup_entry` returns `None` and only logs failures (duplicate unique id, invalid attributes), so use
   the platform object instead: `entity_platform.async_get_current_platform()` inside `async_setup_entry`, then
   `await platform.async_add_entities([...])`. After it returns, an entity that was added has an `entity_id`.
3. **Stale platforms.** A callback or platform stored before a reload still accepts entities and orphans them
   on the unloaded platform. Clear them in `async_unload_entry` before the platforms unload.
4. **Registered but disabled is normal.** An entity the user disabled keeps its registry entry and is not
   added. `entity_registry_enabled_default=False` registers it as disabled by the integration, with no state.
   Enabling an entity reloads the whole config entry about 30 s later.
5. **Names.** With `has_entity_name=True`, always set `_attr_name` explicitly:

   | Case | `entity_id` | Friendly name |
   |---|---|---|
   | Device "Kitchen plug", name "Relay" | `switch.kitchen_plug_relay` | Kitchen plug Relay |
   | Device's main entity, name `None` | `switch.kitchen_plug` | Kitchen plug |
   | Standalone, name "Any motion" | `binary_sensor.any_motion` | Any motion |
   | Standalone, name `None` | `binary_sensor.<platform>_<unique id>` | none |
   | Name left unset, device class set | `binary_sensor.motion` | the device class name |

   A colon in the unique id is accepted. The `entity_id` is fixed at creation: collisions get `_2`, and moving
   an entity onto a device does not rename it.
6. **Updating a live entity.** Change `_attr_*` and call `async_write_ha_state()`: the state machine follows,
   and Home Assistant itself updates the registry's `original_name`, `original_device_class`, capabilities and
   supported features (it warns if that happens too often per hour, so do not change them on every push). It
   does not update `original_icon`, `entity_category` or `unit_of_measurement`: set those with
   `er.async_update_entity(entity_id, original_icon=…, entity_category=…, unit_of_measurement=…)`.
7. **Sensor units stick.** For a device class that converts units, the first unit is stored in the registry
   options; a later unit change is converted back to it (`1 kW` shows as `1000.0 W`).
8. **Devices.** `_attr_device_info` is read only when an entity is added. To change a device:
   `dr.async_get_or_create(config_entry_id=…, identifiers=…, name=…, manufacturer=…, …)` updates it in place
   and live entities' friendly names follow. Clear a field with `dr.async_update_device(id, field=None)`. Move
   a live entity onto or off a device with `er.async_update_entity(entity_id, device_id=id_or_None)`; no
   re-add. A via device that does not exist yet is silently ignored. `suggested_area` is accepted by
   `async_get_or_create` but is not a field of `DeviceEntry`. `DeviceEntry.config_entries` is deprecated
   (use `config_entry_id`).
9. **Removal.**
   - `entity.async_remove()`: the registry entry stays; the state becomes `unavailable` with `restored: true`.
     Adding the same unique id again gives the same `entity_id`.
   - `er.async_remove(entity_id)`: registry entry and state go, and Home Assistant removes the live object.
   - A device survives the removal of its last entity. `dr.async_remove_device(id)` removes the device and
     every entity on it, live ones included.
   - `entity.hass` stays set after removal: track liveness with the bridge's own flag.
10. **Unload and restart.** After a reload, or a restart where nothing is added, every enabled registry entry
    is `unavailable` with `restored: true`. Adding it again with the same unique id keeps the user's
    `entity_id`, name, area, and hidden and disabled flags.
11. **State writes.** `available=False` gives `unavailable`. Writing state before the entity is added raises
    `RuntimeError`; writing after removal is ignored. A changed value costs about 53 µs and an unchanged one
    about 16 µs with no event: 100 writes a second is about 0.5 % of the loop.
12. **Commands.** Methods to implement: switch `async_turn_on(**kwargs)` / `async_turn_off(**kwargs)`; number
    `async_set_native_value(value)`; select `async_select_option(option)`; button `async_press()`; text
    `async_set_value(value)`; light `async_turn_on(**kwargs)` / `async_turn_off(**kwargs)`; cover
    `async_open_cover`, `async_close_cover`, `async_stop_cover`, `async_set_cover_position(**kwargs)` (all
    `**kwargs`, position in `kwargs["position"]`). Home Assistant validates select options, number range and
    text length before calling. Raising `HomeAssistantError("msg")` fails the call with that message (REST 500,
    websocket `home_assistant_error`); a plain exception loses the message; `ServiceValidationError` is a 400.
13. **Attributes a domain requires before add.** Light: `supported_color_modes` and `color_mode`. Cover:
    `supported_features` and `is_closed`. Text: `native_value` (None is fine).
14. **Restore (phase 4).** `async_get_last_state()` returns the previous state for late-added entities after a
    restart. `RestoreSensor.async_get_last_sensor_data()` and `RestoreNumber.async_get_last_number_data()` give
    native values. Home Assistant does not apply the value itself.
15. **Camera (phase 5).** Implement only `async_camera_image(self, width=None, height=None) -> bytes | None`;
    `Camera.__init__` must run. The default MJPEG stream pulls `async_camera_image()` every
    `_attr_frame_interval` (default and minimum 0.5 s), writes a frame only when its bytes changed, and ends
    when a frame is `None`. Pulls stop when the viewer disconnects, with `CancelledError` delivered into a
    pending call. `camera.async_get_image(hass, entity_id, timeout=10, width=…, height=…)` returns
    `Image(content_type, content)`. Pillow 13 is in the image.
16. **Bytes through Python.NET (phase 5).** A .NET `byte[]` reaches Python as `System.Byte[]`; `bytes(arr)`
    takes 0.03 ms for 200 KB and 0.15 ms for 1 MB. Python `bytes` to .NET: implicit conversion works but copies
    element by element (10–30 ms for 200 KB); instead allocate `Array.CreateInstance(Byte, n)` and fill it with
    `memoryview(arr)[:] = data` (0.07 ms). Base64 costs about 0.4 ms per 200 KB each way and is only a fallback.

## Erratum (found while building the bridge)

The rules above stand, with these corrections from the bridge (`entity_bridge.py`) and its tests against Home
Assistant 2026.11.1 (`tests/python/test_entity_platforms.py`, which fail on any warning Home Assistant logs):

- **Rule 8, device lookups and via.** `DeviceRegistry.async_get_device` and the `via_device` device-info key are
  deprecated (Home Assistant reports their use; removal is due in 2027.8). The bridge looks a definition's device up
  with `async_get_device_by_identifier((domain, definition_id), config_entry_id)` and links a parent with
  `via_device_id`; neither logs a warning.
- **Rule 8, a via device that does not exist** is refused, not ignored: `async_get_or_create` raises
  `DeviceInfoError` ("via_device_id … is not a registered device id"). The bridge leaves an unknown parent out, so
  the device has no `via_device_id` (`test_via_device_links_and_unlinks`).
- **Rule 7, sticky units,** holds when the entity is added again, as after a reload: it keeps the unit stored in
  its registry entry and converts to it (`1.5 kW` shows as `1500.0 W`; `test_unit_change_across_a_reload_keeps_the_first_unit`).
  A unit changed while the entity stays live shows the new unit, in the state and the registry
  (`test_unit_change_of_a_live_sensor_shows_the_new_unit`), and a unit with nothing to convert is taken on a re-add
  too (`test_unit_change_without_conversion_shows_the_new_unit`).

## Unverified

- What the Home Assistant UI shows for a restored registry entry.
- Whether implementing `async_remove_config_entry_device` enables device delete in the UI.
- That restore data expires after 7 days (read in the source, not waited out).
