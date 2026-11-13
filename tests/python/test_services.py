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


def load_services(descriptions=None, translations=None):
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
           'async_get_all_descriptions': AsyncMock(return_value=descriptions or {}),
           'async_get_translations': AsyncMock(return_value=translations or {})}
    exec(compile(tree, '<services>', 'exec'), env)
    listeners = {}
    def listen(event_type, handler):
        listeners[event_type] = handler
        return lambda: listeners.pop(event_type)
    executed = []
    async def executor(fn, *args):
        executed.append(fn)
        return fn(*args)
    hass = NS(bus=NS(async_listen=listen), async_add_executor_job=executor, config=NS(language='en'))
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
        env['async_get_translations'].assert_awaited_once_with(hass, 'en', 'services', ['light'])

    async def test_push_adds_translated_names_without_changing_cached_descriptions(self):
        # Built-in services keep their names in translations only; HA's own UI shows those.
        descriptions = {
            'climate': {'set_hvac_mode': {'fields': {'hvac_mode': {'selector': {'state': {}}}}}},
            'light': {'turn_on': {'name': 'Yaml name', 'fields': {
                'brightness_pct': {'selector': {'number': {}}},
                'additional_fields': {'collapsed': True, 'fields': {'brightness': {'name': 'Yaml field'}}}}}},
            'script': {'greet': {'name': 'Greet', 'fields': {'who': {'name': 'Who to greet'}}}},
        }
        cached = json.loads(json.dumps(descriptions))
        translations = {
            'component.climate.services.set_hvac_mode.name': 'Set thermostat HVAC mode',
            'component.climate.services.set_hvac_mode.description': 'Sets the HVAC mode of a thermostat.',
            'component.climate.services.set_hvac_mode.fields.hvac_mode.name': 'HVAC mode',
            'component.light.services.turn_on.name': 'Turn on light',
            'component.light.services.turn_on.fields.brightness_pct.name': 'Brightness',
            'component.light.services.turn_on.fields.brightness.name': 'Brightness value',
            'component.light.services.turn_on.sections.additional_fields.name': 'Additional options',
        }
        env, hass, _, _, _ = load_services(descriptions, translations)
        manager = NS(SetServiceDescriptions=Mock())
        await env['async_push'](hass, manager)
        payload = json.loads(manager.SetServiceDescriptions.call_args.args[0])
        self.assertEqual(payload['climate']['set_hvac_mode'], {
            'name': 'Set thermostat HVAC mode', 'description': 'Sets the HVAC mode of a thermostat.',
            'fields': {'hvac_mode': {'name': 'HVAC mode', 'selector': {'state': {}}}}})
        light = payload['light']['turn_on']
        self.assertEqual(light['name'], 'Turn on light')
        self.assertEqual(light['fields']['brightness_pct']['name'], 'Brightness')
        self.assertEqual(light['fields']['additional_fields']['name'], 'Additional options')
        self.assertEqual(light['fields']['additional_fields']['fields']['brightness'], {'name': 'Brightness value'})
        self.assertEqual(payload['script'], cached['script'])
        self.assertEqual(descriptions, cached)

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
