"""Service-description push and its debounced listener without a running HA instance."""
import ast
import asyncio
import json
from pathlib import Path
from types import SimpleNamespace as NS
import unittest
from unittest.mock import AsyncMock, Mock

EVENTS = ('service_registered', 'service_removed', 'homeassistant_started', 'core_config_updated')


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
    env = {'CoreState': NS(running='running', starting='starting'), 'EVENT_SERVICE_REGISTERED': EVENTS[0], 'EVENT_SERVICE_REMOVED': EVENTS[1],
           'EVENT_HOMEASSISTANT_STARTED': EVENTS[2], 'EVENT_CORE_CONFIG_UPDATE': EVENTS[3], 'callback': lambda f: f, 'JSONEncoder': Encoder,
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
    hass = NS(bus=NS(async_listen=listen), async_add_executor_job=executor, config=NS(language='en'), state='starting')
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
        # The translated copy and its JSON (about 1 KB per service) are made off the loop, and so is the .NET call.
        self.assertEqual(executed, [env['payload'], manager.SetServiceDescriptions])
        env['async_get_translations'].assert_awaited_once_with(hass, 'en', 'services', ['light'])

    async def test_a_cancelled_first_push_leaves_no_listener(self):
        env, hass, listeners, _, _ = load_services({'light': {}})
        async def cancelled(_hass):
            raise asyncio.CancelledError
        env['async_get_all_descriptions'].side_effect = cancelled
        with self.assertRaises(asyncio.CancelledError):
            await env['async_start'](hass, NS(SetServiceDescriptions=Mock()))
        self.assertEqual(listeners, {})

    async def test_a_push_in_flight_does_not_reach_dotnet_after_unsubscribe(self):
        env, hass, listeners, timers, _ = load_services({'light': {}})
        manager = NS(SetServiceDescriptions=Mock())
        unsubscribe = env['async_track'](hass, manager)
        gathering, release = asyncio.Event(), asyncio.Event()
        async def descriptions(_hass):
            gathering.set()
            await release.wait()
            return {'light': {}}
        env['async_get_all_descriptions'].side_effect = descriptions
        listeners[EVENTS[0]](NS(data={}))
        push = asyncio.create_task(timers[0].action(None))
        await gathering.wait()
        unsubscribe()
        release.set()
        await push
        manager.SetServiceDescriptions.assert_not_called()

    async def test_push_says_whether_home_assistant_has_started(self):
        # Only a push once Home Assistant runs may drop a service from the typed calls of node code.
        env, hass, _, _, _ = load_services({'light': {}})
        manager = NS(SetServiceDescriptions=Mock())
        await env['async_push'](hass, manager)
        hass.state = 'running'
        await env['async_push'](hass, manager)
        self.assertEqual([c.args for c in manager.SetServiceDescriptions.call_args_list],
                         [('{"light": {}}', False), ('{"light": {}}', True)])

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

    async def test_translation_failure_still_pushes_untranslated_descriptions(self):
        # Names are cosmetic: a broken translation file must not drop the whole catalog.
        descriptions = {'light': {'turn_on': {'name': 'Turn on', 'fields': {'brightness_pct': {'selector': {}}}}}}
        env, hass, _, _, _ = load_services(descriptions)
        env['async_get_translations'].side_effect = RuntimeError('malformed en.json')
        manager = NS(SetServiceDescriptions=Mock())
        await env['async_push'](hass, manager)
        manager.SetServiceDescriptions.assert_called_once()
        self.assertEqual(json.loads(manager.SetServiceDescriptions.call_args.args[0]), descriptions)
        env['logger'].warning.assert_called_once()
        env['logger'].exception.assert_not_called()

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
        manager.SetServiceDescriptions.assert_called_once_with('{"light": {}}', False)
        listeners[EVENTS[2]](NS(data={}))
        unsubscribe()
        self.assertEqual(listeners, {})
        timers[3].cancel.assert_called_once()
        timers[2].cancel.assert_not_called()

    async def test_a_language_change_pushes_again_and_other_config_changes_do_not(self):
        # Names and descriptions are translated into the language of Home Assistant: a new language re-pushes them,
        # debounced like the service events; a change of, say, the time zone leaves them as they are.
        env, hass, listeners, timers, _ = load_services({'light': {}})
        manager = NS(SetServiceDescriptions=Mock())
        unsubscribe = env['async_track'](hass, manager)
        listeners[EVENTS[3]](NS(data={'time_zone': 'Europe/Zagreb'}))
        listeners[EVENTS[3]](NS(data={}))
        listeners[EVENTS[3]](NS(data=None))
        self.assertEqual(timers, [])
        hass.config.language = 'hr'
        listeners[EVENTS[3]](NS(data={'language': 'hr'}))
        listeners[EVENTS[0]](NS(data={}))
        self.assertEqual([t.delay for t in timers], [2, 2])
        timers[0].cancel.assert_called_once()
        await timers[1].action(None)
        env['async_get_translations'].assert_awaited_once_with(hass, 'hr', 'services', ['light'])
        manager.SetServiceDescriptions.assert_called_once()
        unsubscribe()
        self.assertEqual(listeners, {})

    async def test_started_during_the_first_push_is_not_missed(self):
        # The listeners are in place before the first push gathers descriptions: a "started" event that fires
        # meanwhile schedules a push of its own, which says started (so stale services can be dropped).
        env, hass, listeners, timers, _ = load_services({'light': {}})
        async def descriptions(_hass):
            hass.state = 'running'
            listeners[EVENTS[2]](NS(data={}))
            return {'light': {}}
        env['async_get_all_descriptions'].side_effect = descriptions
        manager = NS(SetServiceDescriptions=Mock())
        unsubscribe = await env['async_start'](hass, manager)
        self.assertEqual(set(listeners), set(EVENTS))
        self.assertEqual(len(timers), 1)
        await timers[0].action(None)
        self.assertEqual([c.args for c in manager.SetServiceDescriptions.call_args_list],
                         [('{"light": {}}', False), ('{"light": {}}', True)])
        unsubscribe()
        self.assertEqual(listeners, {})

    def test_setup_listens_before_the_first_push(self):
        source = Path('custom_components/hass_sharp/__init__.py').read_text()
        self.assertIn('await services.async_start(hass, hass_sharp)', source)
        self.assertNotIn('services.async_push(hass, hass_sharp)', source)
