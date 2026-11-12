"""Entry-owned native HA trigger leases, using only public trigger helpers.

All mutable state and unsubscribe calls belong to HA's event loop. Attachment
is isolated from caller cancellation: cancellation invalidates a request, then drains its late cleanup.
"""
import asyncio
import json
import logging
import uuid
from datetime import date, datetime, time, timedelta

from homeassistant.helpers import config_validation as cv, trigger
from homeassistant.helpers.json import JSONEncoder
from homeassistant.helpers.template import Template

_LOGGER = logging.getLogger(__name__)


class NativeTriggerEncoder(JSONEncoder):
    def default(self, value):
        if isinstance(value, timedelta):
            return value.total_seconds()
        if isinstance(value, (date, datetime, time)):
            return value.isoformat()
        return super().default(value)


async def _async_normalize(hass, configuration):
    return await trigger.async_validate_trigger_config(hass, cv.TRIGGER_SCHEMA(configuration))


async def async_validate_graph_triggers(hass, graph):
    """Validate native declarations without attaching or executing user code."""
    diagnostics = []
    for node in graph.get('nodes', []):
        config = node.get('config', {})
        if node.get('kind') != 'trigger' or config.get('type') != 'ha':
            continue
        try:
            await _async_normalize(hass, config.get('trigger'))
        except Exception:
            diagnostics.append({'nodeId': node.get('id'), 'inputId': 'trigger',
                                'severity': 'error', 'message': 'Invalid Home Assistant trigger configuration'})
    return diagnostics


def _remove_all(callbacks):
    errors = []
    for remove in reversed(callbacks):
        try:
            remove()
        except Exception as error:
            errors.append(error)
            _LOGGER.exception('Cannot remove native trigger')
    if errors:
        raise RuntimeError('Native trigger cleanup failed') from errors[0]


class NativeTriggerBridge:
    def __init__(self, hass):
        self.hass = hass
        self._closed = False
        self._pending = set()
        self._leases = {}

    async def async_register(self, generation, configuration_json, dispatch):
        """Acknowledge only fully attached leases; never cancel helper tasks."""
        if self._closed:
            raise RuntimeError('Native trigger bridge is closed')
        invalidated = asyncio.Event()
        task = asyncio.create_task(self._async_attach(generation, configuration_json, dispatch, invalidated))
        self._pending.add(task)
        task.add_done_callback(self._pending.discard)
        try:
            await asyncio.wait({task})
            return await task
        except asyncio.CancelledError:
            invalidated.set()
            # The helper may have already installed listeners. Drain, don't cancel.
            try:
                await asyncio.wait({task})
                lease = await task
                await self.async_remove(lease)
            except Exception:
                pass
            raise

    async def _async_attach(self, generation, configuration_json, dispatch, invalidated):
        declarations = json.loads(configuration_json)
        if not isinstance(declarations, list):
            raise ValueError('Triggers must be a list')
        configs = []
        ids = set()
        # Validate the whole batch before any helper can attach a callback.
        for declaration in declarations:
            identity = declaration['Id']
            if not isinstance(identity, str) or not identity.strip() or identity in ids:
                raise ValueError('Triggers require unique nonempty IDs')
            ids.add(identity)
            normalized = await _async_normalize(self.hass, json.loads(declaration['ConfigurationJson']))
            if len(normalized) != 1:
                raise ValueError('Each trigger declaration must contain one trigger')
            conf = dict(normalized[0])
            conf['id'] = identity
            enabled = conf.get('enabled', True)
            if isinstance(enabled, Template):
                enabled.hass = self.hass
                enabled = enabled.async_render(None, limited=True)
            conf['enabled'] = bool(enabled)
            configs.append(conf)
        lease = uuid.uuid4().hex
        callbacks = []

        def action_for(index):
            async def action(run_variables, context=None):
                if self._closed or invalidated.is_set() or lease not in self._leases:
                    return
                payload = dict(run_variables)
                if isinstance(payload.get('trigger'), dict):
                    # Per-trigger attachment must not turn every native idx into 0.
                    payload['trigger'] = {**payload['trigger'], 'idx': str(index)}
                if context is not None:
                    payload.setdefault('context', context.as_dict())
                await dispatch(json.dumps(payload, cls=NativeTriggerEncoder, allow_nan=False))
            return action

        try:
            for index, conf in enumerate(configs):
                if not conf['enabled']:
                    continue
                if self._closed or invalidated.is_set():
                    raise RuntimeError('Native trigger generation is inactive')
                errors = []

                def log_cb(level, message, *args, **kwargs):
                    if level >= logging.ERROR:
                        errors.append(message)
                    _LOGGER.log(level, message, *args, **kwargs)

                unsubscribe = await trigger.async_initialize_triggers(
                    self.hass, [conf], action_for(index), 'hass_sharp', generation, log_cb)
                if unsubscribe is not None:
                    callbacks.append(unsubscribe)
                if errors or unsubscribe is None:
                    raise RuntimeError('Native trigger attachment failed')
            if self._closed or invalidated.is_set():
                raise RuntimeError('Native trigger generation is inactive')
            self._leases[lease] = callbacks
            return lease
        except BaseException:
            _remove_all(callbacks)
            raise

    async def async_remove(self, lease):
        callbacks = self._leases.pop(lease, [])
        _remove_all(callbacks)

    def close(self):
        self._closed = True

    async def async_shutdown(self):
        """Invalidate first, drain pending registration, attempt every removal."""
        self._closed = True
        if self._pending:
            await asyncio.gather(*list(self._pending), return_exceptions=True)
        callbacks = [callback for group in self._leases.values() for callback in group]
        self._leases.clear()
        _remove_all(callbacks)

    def begin_register(self, generation, configuration_json, dispatch, success, error):
        async def register():
            try:
                lease = await self.async_register(generation, configuration_json, dispatch)
            except Exception:
                _LOGGER.exception("Native trigger registration failed")
                error('Native trigger registration failed')
            else:
                success(lease)
        self.hass.loop.call_soon_threadsafe(lambda: self.hass.async_create_task(register()))

    def begin_remove(self, lease, done, error):
        async def remove():
            try:
                await self.async_remove(lease)
            except Exception:
                error('Native trigger cleanup failed')
            else:
                done()
        self.hass.loop.call_soon_threadsafe(lambda: self.hass.async_create_task(remove()))
