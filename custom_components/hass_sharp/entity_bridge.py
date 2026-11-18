# pyright: reportMissingImports=false, reportUnknownArgumentType=false, reportUnknownVariableType=false
"""Home Assistant's side of the custom entities: the entities and device of each .NET definition.

.NET asks, with JSON text, to sync a definition's entities, remove a definition, prune the definitions that no
longer exist, and write a batch of states; Home Assistant's commands go back through `EntityCommand`, and its
requests for a camera's picture through `CameraImage`. .NET also asks for pictures of Home Assistant's cameras
(`begin_camera_snapshot`). The rules come from the spike findings
(docs/superpowers/plans/2026-11-17-custom-entities-phase-1-spike.md).

Every Home Assistant call runs on the event loop (spike rule 1): .NET's calls arrive on its own threads and are
marshalled there without waiting. Sync, remove and prune run one at a time in the order .NET asked for them, and
each answers .NET exactly once. Pushes do not wait behind them: a state for an entity that is not live yet is kept
(the latest only) and written when it is added. Nothing here removes registry entries on unload: they stay,
unavailable, until the next sync. A sync removes the entries of the entities it no longer describes only once it
succeeded: a sync that fails leaves the revision running as Home Assistant showed it.
"""
import asyncio
import json
import collections

from homeassistant.exceptions import HomeAssistantError
from homeassistant.helpers import device_registry as dr, entity_registry as er

from . import dotnet_tasks
from .const import DOMAIN, logger

CLOSED = "closed"
# How long, in seconds, unload waits for an add Home Assistant had in flight when the bridge closed.
CLOSE_WAIT = 10

# How many different state write failures are logged for one entity between syncs of its definition; then one line
# says the rest are not.
WRITE_FAILURES_LOGGED = 5
# In an entity's record of failures once that line was logged.
_SILENCED = object()

# How long, in seconds, Home Assistant has to give a picture .NET asked for, as its own snapshot gives it.
SNAPSHOT_TIMEOUT = 10
SNAPSHOT_STOPPED = "Home Assistant is stopping, so no picture was taken"
SNAPSHOT_CLOSED = "HassSharp is stopping, so no picture was taken"

# The device fields of a sync request by the keyword Home Assistant's device registry takes; a null clears one.
DEVICE_FIELDS = {"name": "name", "manufacturer": "manufacturer", "model": "model", "swVersion": "sw_version",
                 "hwVersion": "hw_version", "configurationUrl": "configuration_url"}


class _Closed(Exception):
    """The bridge closed while an operation ran; close() answered it, and the rest of it is skipped."""


class _Refused(Exception):
    """An operation cannot complete for a reason the message gives in full: logged without a traceback."""


class _Operation:
    def __init__(self, action, run, success, error):
        self.action, self.run, self.success, self.error = action, run, success, error
        self.answered = False

    def answer(self, callback, *args):
        """Call one of .NET's callbacks unless the operation was answered; an exception from it stays here."""
        if self.answered:
            return
        self.answered = True
        try:
            callback(*args)
        except Exception:
            logger.exception("Cannot answer .NET after the request to %s", self.action)


def _camera():
    """Home Assistant's camera component, imported when a picture is first asked for: by then HassSharp's own camera
    platform has loaded it, and importing it with this module would block the event loop."""
    from homeassistant.components import camera
    return camera


def _dotnet_bytes(data):
    """A System.Byte[] holding `data`: allocated by .NET and filled through a memoryview, which copies at once; the
    implicit conversion of `bytes` copies element by element (spike rule 16)."""
    from System import Array, Byte

    array = Array.CreateInstance(Byte, len(data))
    memoryview(array)[:] = data
    return array


def _message(error):
    """The message of a .NET exception (its text also names the type and the stack), else the exception's text."""
    message = getattr(error, "Message", None)
    return message if isinstance(message, str) and message else str(error) or type(error).__name__


class EntityBridge:
    def __init__(self, hass, entry, manager):
        self.hass = hass
        self.entry = entry
        self.manager = manager
        self._closed = False
        self._platforms = {}  # domain -> (EntityPlatform, entity class)
        # unique id -> entity added and not removed by the bridge since: its own record, not `entity.hass`, which stays
        # set after removal (spike rule 9), and not the entity's removal callback, which may also run when Home
        # Assistant re-adds the same object (a changed entity id; unverified).
        self._live = {}
        self._pending = {}  # unique id -> latest snapshot pushed while the entity was not live
        # unique id -> the messages of the state writes that failed for it since its definition was last synced: each is
        # logged once, so a sensor given a value it cannot show does not log a traceback on every push, and at most
        # WRITE_FAILURES_LOGGED of them, as the message holds the value.
        self._write_failures = {}
        self._queue = collections.deque()
        self._current = None
        self._runner = None

    def attach_platform(self, domain: str, platform, factory) -> None:
        """On the loop, from a platform's `async_setup_entry`: where entities of `domain` are added, and their class."""
        if not self._closed:
            self._platforms[domain] = (platform, factory)

    def begin_sync(self, definition_json, success, error) -> None:
        self._begin("sync a definition's entities", lambda: self._async_sync(definition_json), success, error)

    def begin_remove(self, definition_id, done, error) -> None:
        self._begin(f"remove the entities of {definition_id}", lambda: self._async_remove(definition_id), done, error)

    def begin_prune(self, definition_ids_json, done, error) -> None:
        self._begin("remove the entities of deleted definitions", lambda: self._async_prune(definition_ids_json),
                    done, error)

    def push(self, batch_json) -> None:
        """From any thread: write a JSON array of snapshots to the live entities, keep the others'. Never raises."""
        try:
            snapshots = json.loads(batch_json)
        except Exception:
            logger.exception("Cannot read entity states from .NET")
            return
        try:
            self.hass.loop.call_soon_threadsafe(self._apply_push, snapshots)
        except Exception:
            pass  # The loop has closed: there is nothing left to write to.

    async def async_command(self, unique_id: str, command: str, data: dict) -> None:
        """Run a command Home Assistant sent to an entity; a failure fails the service call with .NET's message."""
        try:
            await dotnet_tasks.async_run_dotnet_task(self.hass, self.manager.EntityCommand, unique_id, command,
                                                     json.dumps(data))
        except Exception as error:
            # Only HomeAssistantError keeps its message in the service call's answer (spike rule 12).
            raise HomeAssistantError(_message(error)) from error

    async def async_camera_image(self, unique_id: str) -> bytes | None:
        """The current frame of a camera of a definition, as JPEG, for Home Assistant; None when there is none.

        .NET has logged why it gave none. A viewer that leaves cancels only this wait: the frame being made is shared
        with the other viewers, and .NET finishes it.
        """
        try:
            picture = await dotnet_tasks.async_run_dotnet_task(self.hass, self.manager.CameraImage, unique_id)
            return bytes(picture)
        except Exception as error:
            logger.debug("No picture from %s: %s", unique_id, _message(error))
            return None

    def begin_camera_snapshot(self, definition_id, entity_id, on_bytes, on_error) -> None:
        """From a .NET thread: take a picture with one of Home Assistant's cameras, for the definition `definition_id`
        (empty for none). Answers exactly once, on the loop: `on_bytes` with the JPEG as a System.Byte[], or
        `on_error` with one sentence. A camera of the asking definition itself is refused at once, as it would wait for
        itself. Never raises.
        """
        operation = _Operation(f"take a picture with {entity_id}", None, on_bytes, on_error)
        try:
            self.hass.loop.call_soon_threadsafe(self._start_snapshot, definition_id, entity_id, operation)
        except Exception:
            # The loop has closed, so nothing on it will answer.
            operation.answer(operation.error, SNAPSHOT_STOPPED)

    def close(self) -> None:
        """On the loop, before the platforms unload. Later calls are dropped and pending ones answer error("closed").

        A picture being taken (begin_camera_snapshot) is not among them: it answers when its own task ends, within
        SNAPSHOT_TIMEOUT (10 s). One asked for after this answers SNAPSHOT_CLOSED. Forgets the platforms and the live
        entities, so nothing is added to an unloaded platform (spike rule 3). Registry entries and devices stay.
        """
        self._closed = True
        self._platforms.clear()
        self._live.clear()
        self._pending.clear()
        self._write_failures.clear()
        operations = ([self._current] if self._current is not None else []) + list(self._queue)
        self._queue.clear()
        for operation in operations:
            operation.answer(operation.error, CLOSED)

    async def async_wait_closed(self, timeout: float = CLOSE_WAIT) -> None:
        """After close(), before the platforms unload: wait until an add that was in flight has finished.

        Home Assistant finishes an add it started even after the platform unloaded, and that entity then holds its
        unique id until a restart: the next sync could not add it again. Once closed, the bridge's own task only ever
        waits on such an add, and stops after it. Never raises.
        """
        if self._runner is None or self._runner.done():
            return
        _, pending = await asyncio.wait({self._runner}, timeout=timeout)
        if pending:
            logger.warning("Home Assistant was still adding a HassSharp entity after %s s while the integration "
                           "unloaded; if that entity stays unavailable after the next start of HassSharp, restart "
                           "Home Assistant", timeout)

    # The queue

    def _begin(self, action, run, success, error):
        operation = _Operation(action, run, success, error)
        try:
            self.hass.loop.call_soon_threadsafe(self._enqueue, operation)
        except Exception:
            # The loop has closed, so nothing on it will answer.
            operation.answer(operation.error, CLOSED)

    def _enqueue(self, operation):
        if self._closed:
            operation.answer(operation.error, CLOSED)
            return
        self._queue.append(operation)
        # A drain that started eagerly may have finished before its task was stored.
        if self._runner is None or self._runner.done():
            drain = self._async_drain()
            try:
                self._runner = self.hass.async_create_task(drain, name="HassSharp entity bridge")
            except Exception as error:
                drain.close()
                logger.exception("Cannot start the entity bridge's task")
                for waiting in self._queue:
                    waiting.answer(waiting.error, _message(error))
                self._queue.clear()

    async def _async_drain(self):
        while self._queue:
            operation = self._current = self._queue.popleft()
            try:
                result = await operation.run()
            except _Closed:
                pass
            except _Refused as error:
                logger.error("Cannot %s: %s", operation.action, error)
                operation.answer(operation.error, str(error))
            except Exception as error:
                logger.exception("Cannot %s", operation.action)
                operation.answer(operation.error, _message(error))
            except BaseException:
                # Cancelled: Home Assistant is stopping. Nobody else would answer these.
                for waiting in [operation, *self._queue]:
                    waiting.answer(waiting.error, "cancelled")
                self._queue.clear()
                raise
            else:
                operation.answer(operation.success, *result)
            finally:
                self._current = None

    def _check_open(self):
        if self._closed:
            raise _Closed

    # Pictures

    def _start_snapshot(self, definition_id, entity_id, operation):
        if self._closed:
            operation.answer(operation.error, SNAPSHOT_CLOSED)
            return
        prefix = definition_id + ":"
        if definition_id and entity_id.startswith("camera.") and any(
                unique_id.startswith(prefix) and entity.entity_id == entity_id
                for unique_id, entity in self._live.items()):
            operation.answer(operation.error, f"{entity_id} is a camera of this definition; a definition cannot take a "
                                              "picture of its own camera")
            return
        snapshot = self._async_snapshot(entity_id, operation)
        try:
            # Not waited for by Home Assistant's startup; cancelled when it stops.
            self.hass.async_create_background_task(snapshot, f"HassSharp picture of {entity_id}")
        except Exception as error:
            snapshot.close()
            operation.answer(operation.error, _message(error))

    async def _async_snapshot(self, entity_id, operation):
        try:
            image = await _camera().async_get_image(self.hass, entity_id, timeout=SNAPSHOT_TIMEOUT)
            content_type = image.content_type or ""
            # As Home Assistant tells a JPEG it can scale.
            if "jpeg" not in content_type and "jpg" not in content_type:
                operation.answer(operation.error, f"{entity_id} gives {image.content_type} pictures; only JPEG "
                                                  "pictures can be used")
                return
            picture = _dotnet_bytes(image.content)
        except Exception as error:
            # Home Assistant's message for a camera that is missing, off or gives nothing.
            operation.answer(operation.error, _message(error))
        except BaseException:
            operation.answer(operation.error, SNAPSHOT_STOPPED)
            raise
        else:
            operation.answer(operation.success, picture)

    # Operations

    def _apply_push(self, snapshots):
        if self._closed:
            return
        for snapshot in snapshots:
            unique_id = None
            try:
                unique_id = snapshot["uniqueId"]
                entity = self._live.get(unique_id)
                if entity is None:
                    self._pending[unique_id] = snapshot
                    continue
                entity.apply_state(snapshot)
                entity.async_write_ha_state()
            except Exception as error:
                self._write_failed(unique_id, snapshot, error)

    def _write_failed(self, unique_id, snapshot, error):
        """Log a failed state write once per unique id and message until the definition is synced again: the first for
        the entity with its traceback, a later different one without. Home Assistant puts the value into the message,
        so after WRITE_FAILURES_LOGGED messages one line says the entity's further failures are not logged."""
        key = unique_id if isinstance(unique_id, str) else None
        failures = self._write_failures.setdefault(key, set())
        message = _message(error)
        if message in failures:
            return
        if len(failures) >= WRITE_FAILURES_LOGGED:
            if _SILENCED not in failures:
                failures.add(_SILENCED)
                logger.warning("Further state write failures of %s are not logged until its definition is synced again",
                               key)
            return
        if failures:
            logger.error("Cannot write the entity state %s: %s", snapshot, message)
        else:
            logger.exception("Cannot write the entity state %s", snapshot)
        failures.add(message)

    async def _async_sync(self, definition_json):
        """Make Home Assistant match one definition's description (spike rules 2, 5, 6, 8, 9).

        Refused before anything changes when a platform is not set up or cannot show one of the entities
        (`HassSharpEntity.refusal`)."""
        definition = json.loads(definition_json)
        definition_id = definition["definitionId"]
        descriptions = definition["entities"]
        missing = sorted({d["domain"] for d in descriptions} - self._platforms.keys())
        if missing:
            raise _Refused(f"The {', '.join(missing)} platform of HassSharp is not set up")
        refusals = [refused for d in descriptions
                    if (refused := self._platforms[d["domain"]][1].refusal(d)) is not None]
        if refusals:
            raise _Refused("; ".join(refusals))
        entities, devices = er.async_get(self.hass), dr.async_get(self.hass)
        described = {(d["domain"], d["uniqueId"]) for d in descriptions}
        unique_ids = {unique_id for _, unique_id in described}
        prefix = definition_id + ":"
        # A failed sync leaves the revision running and must leave what it shows in place: only an entry whose unique
        # id is described again under another domain goes first, as its entity cannot be updated into the new one
        # (`_live` is keyed by unique id alone). The entries no longer described go once everything else succeeded.
        self._remove_entries(entities, definition_id, keep=lambda entry: entry.unique_id not in unique_ids
                             or (entry.domain, entry.unique_id) in described)
        for unique_id in [u for u in self._write_failures if isinstance(u, str) and u.startswith(prefix)]:
            del self._write_failures[unique_id]
        device_info, device_id = self._sync_device(entities, devices, definition_id, definition.get("device"))
        result, failed = {}, []
        for description in descriptions:
            entity_id = await self._async_sync_entity(entities, description, device_info, device_id,
                                                      definition["name"])
            if entity_id is None:
                failed.append(description["uniqueId"])
            else:
                result[description["uniqueId"]] = {"entityId": entity_id}
        if failed:
            raise _Refused(f"Home Assistant did not add {', '.join(failed)}; its log says why")
        self._remove_entries(entities, definition_id, keep=lambda entry: entry.unique_id in unique_ids)
        for unique_id in [u for u in self._pending if u.startswith(prefix) and u not in unique_ids]:
            del self._pending[unique_id]
        return (json.dumps({"entities": result}),)

    def _sync_device(self, entities, devices, definition_id, device):
        """Create, update or remove the definition's device; returns its device info for new entities and its id."""
        identifiers = {(DOMAIN, definition_id)}
        if device is None:
            existing = devices.async_get_device_by_identifier((DOMAIN, definition_id), self.entry.entry_id)
            if existing is not None:
                # Off it first: removing a device removes the entities on it (spike rule 9).
                for entry in self._entries(entities, definition_id):
                    if entry.device_id == existing.id:
                        entities.async_update_entity(entry.entity_id, device_id=None)
                devices.async_remove_device(existing.id)
            return None, None
        fields = {keyword: device.get(name) for name, keyword in DEVICE_FIELDS.items()}
        info = {"identifiers": identifiers, **{k: v for k, v in fields.items() if v is not None}}
        if device.get("suggestedArea") is not None:
            info["suggested_area"] = device["suggestedArea"]
        via = self._via(devices, device.get("via"))
        if via is not None:
            info["via_device_id"] = via
        entry = devices.async_get_or_create(config_entry_id=self.entry.entry_id, **info)
        # Updates in place; a field described as null is cleared separately (spike rule 8).
        cleared = {k: None for k, v in fields.items() if v is None and getattr(entry, k, None) is not None}
        if via is None and entry.via_device_id is not None:
            cleared["via_device_id"] = None
        if cleared:
            devices.async_update_device(entry.id, **cleared)
        return info, entry.id

    def _via(self, devices, via):
        """The id of the parent device, or None when there is none or it does not exist (Home Assistant refuses an
        unknown `via_device_id`; it deprecates `via_device` and the lookup by identifiers alone)."""
        if not via:
            return None
        if via.get("definition") is not None:
            parent = devices.async_get_device_by_identifier((DOMAIN, via["definition"]), self.entry.entry_id)
        elif via.get("deviceId") is not None:
            parent = devices.async_get(via["deviceId"])
        else:
            return None
        return parent.id if parent is not None else None

    async def _async_sync_entity(self, entities, description, device_info, device_id, definition_name):
        """Update a live entity in place or add a new one; returns its entity id, or None when it was not added."""
        unique_id, domain = description["uniqueId"], description["domain"]
        entity = self._live.get(unique_id)
        if entity is not None:
            # A description Home Assistant refuses (at the apply or the state write) leaves the entity as it was, not
            # half described: its attributes and Home Assistant's cached properties are all in its __dict__.
            before = dict(vars(entity))
            try:
                entity.apply(description, device_info, definition_name)
                entity.async_write_ha_state()
            except BaseException:
                vars(entity).clear()
                vars(entity).update(before)
                raise
            # Home Assistant updates the rest of the registry entry from the state write, not these (spike rule 6). The
            # unit is the one the entity shows, as Home Assistant writes it on an add: a sensor re-added with another
            # unit of its device class keeps the unit stored for it and converts to it (spike rule 7).
            changes = {"original_icon": entity.icon, "entity_category": entity.entity_category,
                       "unit_of_measurement": entity.unit_of_measurement}
            if (entry := entities.async_get(entity.entity_id)) is not None:
                if entry.device_id != device_id:
                    changes["device_id"] = device_id
                entities.async_update_entity(entity.entity_id, **changes)
            return entity.entity_id
        platform, factory = self._platforms[domain]
        entity = factory(self, description, device_info, definition_name)
        if (snapshot := self._pending.get(unique_id)) is not None:
            entity.apply_state(snapshot)  # Its first state, which the add writes.
        await platform.async_add_entities([entity])
        self._check_open()
        # Home Assistant sets `entity_id` before it refuses an entity or skips a disabled entry, and leaves it set:
        # only one it really added is among the platform's entities.
        if entity.entity_id is not None and platform.entities.get(entity.entity_id) is entity:
            self._live[unique_id] = entity
            latest = self._pending.pop(unique_id, None)
            if latest is not None and latest is not snapshot:  # Pushed while it was being added.
                entity.apply_state(latest)
                entity.async_write_ha_state()
            return entity.entity_id
        # Not added: normal for an entry the user or the integration disabled (spike rule 4).
        entry = entities.async_get(entities.async_get_entity_id(domain, DOMAIN, unique_id) or "")
        return entry.entity_id if entry is not None and entry.disabled_by is not None else None

    async def _async_remove(self, definition_id):
        self._purge(er.async_get(self.hass), dr.async_get(self.hass), definition_id)
        return ()

    async def _async_prune(self, definition_ids_json):
        ids = json.loads(definition_ids_json)
        # Anything else read as a set (a string's characters, an object's keys) would purge nearly every definition.
        if not isinstance(ids, list) or not all(isinstance(i, str) for i in ids):
            raise _Refused("The definitions to keep are not a JSON array of strings; nothing was removed")
        keep = set(ids)
        entities, devices = er.async_get(self.hass), dr.async_get(self.hass)
        found = {e.unique_id.split(":", 1)[0] for e in er.async_entries_for_config_entry(entities, self.entry.entry_id)
                 if ":" in e.unique_id}
        found.update(identifier[1] for d in dr.async_entries_for_config_entry(devices, self.entry.entry_id)
                     for identifier in d.identifiers if identifier[0] == DOMAIN)
        for definition_id in sorted(found - keep):
            self._purge(entities, devices, definition_id)
        for unique_id in [u for u in self._pending if u.split(":", 1)[0] not in keep]:
            del self._pending[unique_id]
        return ()

    def _purge(self, entities, devices, definition_id):
        """Remove a definition's device, then the registry entries left with its prefix, and forget its entities."""
        if (device := devices.async_get_device_by_identifier((DOMAIN, definition_id), self.entry.entry_id)) is not None:
            devices.async_remove_device(device.id)
        for entry in self._entries(entities, definition_id):
            entities.async_remove(entry.entity_id)
        prefix = definition_id + ":"
        for store in (self._live, self._pending, self._write_failures):
            for unique_id in [u for u in store if isinstance(u, str) and u.startswith(prefix)]:
                del store[unique_id]

    def _entries(self, entities, definition_id):
        """This config entry's registry entries of a definition."""
        prefix = definition_id + ":"
        return [e for e in er.async_entries_for_config_entry(entities, self.entry.entry_id)
                if e.unique_id.startswith(prefix)]

    def _remove_entries(self, entities, definition_id, keep):
        """Remove the definition's registry entries that `keep(entry)` does not keep; Home Assistant removes the live
        entity of each (spike rule 9)."""
        for entry in self._entries(entities, definition_id):
            if keep(entry):
                continue
            if (entity := self._live.get(entry.unique_id)) is not None and entity.entity_id == entry.entity_id:
                del self._live[entry.unique_id]
            entities.async_remove(entry.entity_id)
