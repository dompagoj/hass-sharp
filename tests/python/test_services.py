"""Service-description push and its debounced listener without a running HA instance."""
import ast
import json
from pathlib import Path
from types import SimpleNamespace as NS
import unittest
from unittest.mock import AsyncMock, Mock

EVENTS = ('service_registered', 'service_removed', 'homeassistant_started')


class Selector:
    """Stands in for a value only HA's JSONEncoder can serialise."""
    def __init__(self, value): self.value = value


class Encoder(json.JSONEncoder):
    def default(self, value):
        if isinstance(value, Selector):
            return value.value
        return super().default(value)


def load_services(descriptions=None):
    tree = ast.parse(Path('custom_components/hass_sharp/services.py').read_text())
    tree.body = [n for n in tree.body if not isinstance(n, ast.ImportFrom)]
    timers = []
    def call_later(hass, delay, action):
        timer = NS(delay=delay, action=action, cancel=Mock())
        timers.append(timer)
        return timer.cancel
    env = {'EVENT_SERVICE_REGISTERED': EVENTS[0], 'EVENT_SERVICE_REMOVED': EVENTS[1],
           'EVENT_HOMEASSISTANT_STARTED': EVENTS[2], 'callback': lambda f: f, 'JSONEncoder': Encoder,
           'async_call_later': call_later, 'logger': Mock(),
           'async_get_all_descriptions': AsyncMock(return_value=descriptions or {})}
    exec(compile(tree, '<services>', 'exec'), env)
    listeners = {}
    def listen(event_type, handler):
        listeners[event_type] = handler
        return lambda: listeners.pop(event_type)
    executed = []
    async def executor(fn, *args):
        executed.append(fn)
        return fn(*args)
    hass = NS(bus=NS(async_listen=listen), async_add_executor_job=executor)
    return env, hass, listeners, timers, executed


class ServiceDescriptions(unittest.IsolatedAsyncioTestCase):
    async def test_push_serialises_descriptions_with_ha_encoder_off_the_loop(self):
        descriptions = {'light': {'turn_on': {'name': 'Turn on', 'fields': {
            'brightness_pct': {'selector': Selector({'number': {'min': 0, 'max': 100}})}}}}}
        env, hass, _, _, executed = load_services(descriptions)
        manager = NS(SetServiceDescriptions=Mock())
        await env['async_push'](hass, manager)
        env['async_get_all_descriptions'].assert_awaited_once_with(hass)
        manager.SetServiceDescriptions.assert_called_once()
        payload = manager.SetServiceDescriptions.call_args.args[0]
        self.assertIsInstance(payload, str)
        self.assertEqual(json.loads(payload), {'light': {'turn_on': {'name': 'Turn on', 'fields': {
            'brightness_pct': {'selector': {'number': {'min': 0, 'max': 100}}}}}}})
        self.assertEqual(executed, [manager.SetServiceDescriptions])

    async def test_missing_manager_method_is_skipped_quietly(self):
        env, hass, _, _, executed = load_services()
        await env['async_push'](hass, NS(Init=object()))
        env['async_get_all_descriptions'].assert_not_awaited()
        self.assertEqual(executed, [])
        env['logger'].exception.assert_not_called()
        env['logger'].error.assert_not_called()
        env['logger'].warning.assert_not_called()

    async def test_push_failure_is_logged_without_leaking(self):
        env, hass, _, _, _ = load_services()
        manager = NS(SetServiceDescriptions=Mock(side_effect=RuntimeError('managed failure')))
        await env['async_push'](hass, manager)
        env['logger'].exception.assert_called_once()

    async def test_listener_debounces_bursts_and_unsubscribes(self):
        env, hass, listeners, timers, _ = load_services({'light': {}})
        manager = NS(SetServiceDescriptions=Mock())
        unsubscribe = env['async_track'](hass, manager)
        self.assertEqual(set(listeners), set(EVENTS))
        listeners[EVENTS[0]](NS(data={}))
        listeners[EVENTS[1]](NS(data={}))
        listeners[EVENTS[0]](NS(data={}))
        self.assertEqual([t.delay for t in timers], [2, 2, 2])
        timers[0].cancel.assert_called_once()
        timers[1].cancel.assert_called_once()
        timers[2].cancel.assert_not_called()
        await timers[2].action(None)
        manager.SetServiceDescriptions.assert_called_once_with('{"light": {}}')
        listeners[EVENTS[2]](NS(data={}))
        unsubscribe()
        self.assertEqual(listeners, {})
        timers[3].cancel.assert_called_once()
        timers[2].cancel.assert_not_called()
