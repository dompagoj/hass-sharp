"""Bridge ownership/transaction tests; no services or devices are actuated."""
import asyncio
import importlib.util
import json
import logging
import sys
import unittest
from datetime import date, datetime, time, timedelta, timezone
from pathlib import Path
from unittest.mock import patch

if importlib.util.find_spec('homeassistant') is None:
    # The bridge imports Home Assistant's trigger helpers; run these inside the HA container or a venv with it.
    raise unittest.SkipTest('Requires Home Assistant (the homeassistant package is not installed)')

spec = importlib.util.spec_from_file_location('native_triggers', Path('custom_components/hass_sharp/native_triggers.py'))
native = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = native
spec.loader.exec_module(native)

async def accept_config(hass, configs):
    configs = [dict(c) for c in configs]
    for config in configs:
        if isinstance(config.get('enabled'), str):
            config['enabled'] = native.cv.template(config['enabled'])
    return configs

class NativeTriggers(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        from homeassistant.core import HomeAssistant
        self.hass = HomeAssistant('/tmp/hass-sharp-native-test')
        self.bridge = native.NativeTriggerBridge(self.hass)

    async def test_preview_validates_normalized_platform_without_attachment(self):
        validated = []
        async def validate(hass, configs):
            self.assertIs(hass, self.hass)
            validated.extend(configs)
            if configs[0]['platform'] == 'invalid': raise ValueError('unknown platform')
            return configs
        with patch.object(native.trigger, 'async_validate_trigger_config', validate), patch.object(native.trigger, 'async_initialize_triggers', side_effect=AssertionError('preview attached')):
            graph = {'nodes': [{'id': 'ok', 'kind': 'trigger', 'config': {'type': 'ha', 'trigger': {'trigger': 'event', 'event_type': 'test'}}}, {'id': 'bad', 'kind': 'trigger', 'config': {'type': 'ha', 'trigger': {'trigger': 'invalid'}}}]}
            diagnostics = await native.async_validate_graph_triggers(self.hass, graph)
        self.assertEqual(validated[0]['platform'], 'event')
        self.assertEqual([(d['nodeId'], d['inputId']) for d in diagnostics], [('bad', 'trigger')])

    async def test_second_attachment_failure_rolls_back_and_attempts_all_cleanup(self):
        removed = []
        async def attach(hass, configs, action, domain, name, log_cb, **kwargs):
            if configs[0]['event_type'] == 'bad':
                log_cb(logging.ERROR, 'failed %s', 'attach', exc_info=None)
                return None
            def remove(): removed.append(configs[0]['event_type'])
            return remove
        with patch.object(native.trigger, 'async_validate_trigger_config', new=accept_config), patch.object(native.trigger, 'async_initialize_triggers', attach):
            with self.assertRaises(Exception):
                await self.bridge.async_register('generation', self.configs('first', 'bad'), lambda _: None)
        self.assertEqual(removed, ['first'])

    async def test_shutdown_drains_late_success_and_stale_dispatch(self):
        entered, release = asyncio.Event(), asyncio.Event()
        removed, payloads = [], []
        async def attach(hass, configs, action, domain, name, log_cb, **kwargs):
            entered.set()
            await release.wait()
            await action({'trigger': {'platform': 'event', 'event': 'early'}})
            return lambda: removed.append('late')
        async def dispatch(payload): payloads.append(payload)
        with patch.object(native.trigger, 'async_validate_trigger_config', new=accept_config), patch.object(native.trigger, 'async_initialize_triggers', attach):
            registration = asyncio.create_task(self.bridge.async_register('generation', self.configs('late'), dispatch))
            await asyncio.wait_for(entered.wait(), 2)
            shutdown = asyncio.create_task(self.bridge.async_shutdown())
            await asyncio.sleep(0)
            self.assertFalse(shutdown.done())
            release.set()
            with self.assertRaises(Exception): await registration
            await shutdown
        self.assertEqual(removed, ['late'])
        self.assertEqual(payloads, [])

    async def test_disabled_and_template_enabled_flags(self):
        attached = []
        async def attach(hass, configs, action, domain, name, log_cb, **kwargs):
            attached.extend(configs)
            return lambda: None
        configs = json.dumps([{'Id': str(i), 'ConfigurationJson': json.dumps({'trigger': 'event', 'event_type': 'test', 'enabled': enabled})} for i, enabled in enumerate([False, '{{ false }}', '{{ true }}'])])
        with patch.object(native.trigger, 'async_validate_trigger_config', new=accept_config), patch.object(native.trigger, 'async_initialize_triggers', attach):
            lease = await self.bridge.async_register('generation', configs, lambda _: None)
            await self.bridge.async_remove(lease)
        self.assertEqual([c['id'] for c in attached], ['2'])

    async def test_complete_native_payload_serialization_and_remove_is_idempotent(self):
        from homeassistant.core import Context, Event, State
        payloads, callbacks, removed = [], [], []
        async def attach(hass, configs, action, domain, name, log_cb, **kwargs):
            callbacks.append(action)
            return lambda: removed.append(1)
        async def dispatch(payload): payloads.append(json.loads(payload))
        with patch.object(native.trigger, 'async_validate_trigger_config', new=accept_config), patch.object(native.trigger, 'async_initialize_triggers', attach):
            lease = await self.bridge.async_register('generation', self.configs('test'), dispatch)
            context = Context()
            await callbacks[0]({'trigger': {'to_state': State('sensor.test', 'on'), 'event': Event('synthetic', {'x': 1}), 'now': datetime(2026, 1, 1, tzinfo=timezone.utc), 'for': timedelta(seconds=2.5), 'at': time(12, 30), 'date': date(2026, 1, 1)}, 'extra': [1, True]}, context)
            await self.bridge.async_remove(lease)
            await self.bridge.async_remove(lease)
            await callbacks[0]({'trigger': {}}, context)
        self.assertEqual(len(payloads), 1)
        self.assertEqual(payloads[0]['trigger']['to_state']['entity_id'], 'sensor.test')
        self.assertEqual(payloads[0]['trigger']['event']['data'], {'x': 1})
        self.assertEqual(payloads[0]['trigger']['now'], '2026-01-01T00:00:00+00:00')
        self.assertEqual(payloads[0]['trigger']['for'], 2.5)
        self.assertEqual(payloads[0]['trigger']['at'], '12:30:00')
        self.assertEqual(payloads[0]['trigger']['date'], '2026-01-01')
        self.assertEqual(payloads[0]['extra'], [1, True])
        self.assertEqual(payloads[0]['context']['id'], context.id)
        self.assertEqual(removed, [1])

    async def test_cancelled_registration_drains_attachment_and_removes_late_lease(self):
        entered, release = asyncio.Event(), asyncio.Event()
        removed, loop_errors = [], []
        self.hass.loop.set_exception_handler(lambda loop, context: loop_errors.append(context))
        async def attach(hass, configs, action, domain, name, log_cb, **kwargs):
            entered.set()
            await release.wait()
            return lambda: removed.append('late')
        with patch.object(native.trigger, 'async_validate_trigger_config', new=accept_config), patch.object(native.trigger, 'async_initialize_triggers', attach):
            pending = asyncio.create_task(self.bridge.async_register('cancelled', self.configs('late'), lambda _: None))
            await asyncio.wait_for(entered.wait(), 2)
            pending.cancel()
            await asyncio.sleep(0)
            self.assertFalse(pending.done())
            release.set()
            with self.assertRaises(asyncio.CancelledError): await pending
        self.assertEqual(removed, ['late'])
        self.assertEqual(loop_errors, [])

    async def test_cleanup_attempts_every_unsubscribe_even_when_one_throws(self):
        removed = []
        async def attach(hass, configs, action, domain, name, log_cb, **kwargs):
            event = configs[0]['event_type']
            def remove():
                removed.append(event)
                if event == 'second': raise RuntimeError('cleanup fails')
            return remove
        with patch.object(native.trigger, 'async_validate_trigger_config', new=accept_config), patch.object(native.trigger, 'async_initialize_triggers', attach):
            lease = await self.bridge.async_register('cleanup', self.configs('first', 'second', 'third'), lambda _: None)
            with self.assertLogs(native._LOGGER, level='ERROR'):
                with self.assertRaises(RuntimeError): await self.bridge.async_remove(lease)
            await self.bridge.async_remove(lease)
        self.assertEqual(removed, ['third', 'second', 'first'])

    async def test_individual_helper_calls_preserve_original_batch_index(self):
        callbacks, payloads = [], []
        async def attach(hass, configs, action, domain, name, log_cb, **kwargs):
            callbacks.append(action)
            return lambda: None
        async def dispatch(payload): payloads.append(json.loads(payload))
        with patch.object(native.trigger, 'async_validate_trigger_config', new=accept_config), patch.object(native.trigger, 'async_initialize_triggers', attach):
            await self.bridge.async_register('idx', self.configs('first', 'second'), dispatch)
            await callbacks[1]({'trigger': {'id': 'second', 'idx': '0'}, 'context': {'authored': True}}, None)
        self.assertEqual(payloads[0]['trigger']['idx'], '1')
        self.assertEqual(payloads[0]['context'], {'authored': True})

    @staticmethod
    def configs(*events):
        return json.dumps([{'Id': event, 'ConfigurationJson': json.dumps({'trigger': 'event', 'event_type': event})} for event in events])

class RealHomeAssistantTriggers(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        from homeassistant.core import HomeAssistant
        from homeassistant import loader
        self.hass = HomeAssistant('/tmp/hass-sharp-native-real-test')
        loader.async_setup(self.hass)
        # Core normally initializes the registry. This isolated instance has no core startup.
        if hasattr(native.trigger, 'async_setup'):
            await native.trigger.async_setup(self.hass)
        self.bridge = native.NativeTriggerBridge(self.hass)
        self.service_patch = patch.object(type(self.hass.services), "async_call", self.forbidden_service)
        self.service_patch.start()

    async def forbidden_service(self, *args, **kwargs):
        self.fail('Native trigger test must never call a device service')

    async def asyncTearDown(self):
        await self.bridge.async_shutdown()
        await self.hass.async_stop(force=True)
        self.service_patch.stop()

    async def test_real_event_validation_attachment_payload_and_unsubscribe(self):
        received = []
        async def dispatch(payload): received.append(json.loads(payload))
        lease = await self.bridge.async_register('real-event', NativeTriggers.configs('hass_sharp_native_test'), dispatch)
        self.hass.bus.async_fire('hass_sharp_native_test', {'number': 42})
        await self.hass.async_block_till_done()
        self.assertEqual(received[0]['trigger']['event']['data'], {'number': 42})
        self.assertEqual(received[0]['trigger']['id'], 'hass_sharp_native_test')
        await self.bridge.async_remove(lease)
        self.hass.bus.async_fire('hass_sharp_native_test', {'number': 99})
        await self.hass.async_block_till_done()
        self.assertEqual(len(received), 1)

    async def test_real_unknown_platform_is_node_diagnostic_without_attachment(self):
        diagnostics = await native.async_validate_graph_triggers(self.hass, {'nodes': [{'id': 'bad', 'kind': 'trigger', 'config': {'type': 'ha', 'trigger': {'trigger': 'hass_sharp_nonexistent'}}}]})
        self.assertEqual(diagnostics[0]['nodeId'], 'bad')

    async def test_real_template_enabled_flags(self):
        received = []
        async def dispatch(payload): received.append(json.loads(payload))
        configs = json.dumps([{'Id': str(i), 'ConfigurationJson': json.dumps({'trigger': 'event', 'event_type': 'hass_sharp_enabled_test', 'enabled': enabled})} for i, enabled in enumerate([False, '{{ false }}', '{{ true }}'])])
        await self.bridge.async_register('real-enabled', configs, dispatch)
        self.hass.bus.async_fire('hass_sharp_enabled_test')
        await self.hass.async_block_till_done()
        self.assertEqual([r['trigger']['id'] for r in received], ['2'])

    @unittest.skipUnless(hasattr(native.trigger, 'StatelessEntityTriggerBase'), 'Class triggers require HA 2026.11')
    async def test_real_class_based_event_entity_trigger(self):
        from homeassistant.helpers import area_registry, device_registry, entity_registry, floor_registry, label_registry
        from homeassistant.config_entries import ConfigEntries
        self.hass.config_entries = ConfigEntries(self.hass, {})
        await self.hass.config_entries.async_initialize()
        device_registry.async_setup(self.hass)
        for registry in (area_registry, device_registry, entity_registry, floor_registry, label_registry):
            await registry.async_load(self.hass, load_empty=True)
        received = []
        async def dispatch(payload): received.append(json.loads(payload))
        config = {'trigger': 'event.received', 'target': {'entity_id': 'event.hass_sharp_test'}, 'options': {'event_type': ['pressed']}}
        declarations = json.dumps([{'Id': 'class-event', 'ConfigurationJson': json.dumps(config)}])
        self.hass.states.async_set('event.hass_sharp_test', 'unknown')
        await self.hass.async_block_till_done()
        await self.bridge.async_register('class-event', declarations, dispatch)
        self.hass.states.async_set('event.hass_sharp_test', '2026-01-01T00:00:00+00:00', {'event_type': 'pressed'})
        await self.hass.async_block_till_done()
        self.assertEqual(len(received), 1)
        self.assertEqual(received[0]['trigger']['id'], 'class-event')
