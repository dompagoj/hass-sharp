"""Entity-domain push and its debounced listener without a running HA instance."""
import ast
import asyncio
from enum import Enum
import json
from pathlib import Path
from types import SimpleNamespace as NS
import unittest
from unittest.mock import AsyncMock, Mock

EVENTS = ('component_loaded', 'homeassistant_started', 'core_config_updated', 'entity_registry_updated')
LOADED, STARTED, CONFIG, REGISTRY = EVENTS
ON = 'component.binary_sensor.entity_component._.state.on'


class Platform(str, Enum):
    BINARY_SENSOR = 'binary_sensor'
    LAWN_MOWER = 'lawn_mower'
    LIGHT = 'light'


def load_entities(states=(), registered=(), components=(), unknown=()):
    """The module's own code, with Home Assistant replaced by fakes (as test_services does)."""
    tree = ast.parse(Path('custom_components/hass_sharp/entities.py').read_text())
    tree.body = [n for n in tree.body if not isinstance(n, ast.ImportFrom)]
    timers = []
    def call_later(hass, delay, action):
        timer = NS(delay=delay, action=action, cancel=Mock())
        timers.append(timer)
        return timer.cancel
    async def integrations(hass, domains):
        return {d: (ImportError(d) if d in unknown else NS(domain=d)) for d in domains}
    async def translations(hass, language, category, domains):
        return {f'component.{d}.entity_component._.state.on': 'On' for d in domains}
    env = {'Platform': Platform, 'CoreState': NS(running='running', starting='starting'),
           'EVENT_COMPONENT_LOADED': LOADED, 'EVENT_HOMEASSISTANT_STARTED': STARTED, 'EVENT_CORE_CONFIG_UPDATE': CONFIG,
           'EVENT_ENTITY_REGISTRY_UPDATED': REGISTRY, 'callback': lambda f: f, 'async_call_later': call_later, 'logger': Mock(),
           'er': NS(async_get=lambda hass: NS(entities={f'{d}.x': NS(domain=d) for d in registered})),
           'loader': NS(async_get_integrations=AsyncMock(side_effect=integrations)),
           'async_get_translations': AsyncMock(side_effect=translations)}
    exec(compile(tree, '<entities>', 'exec'), env)
    listeners = {}
    def listen(event_type, handler):
        listeners[event_type] = handler
        return lambda: listeners.pop(event_type)
    async def executor(fn, *args):
        return fn(*args)
    hass = NS(bus=NS(async_listen=listen), async_add_executor_job=executor, config=NS(language='en', components=set(components)),
              states=NS(async_all=lambda: [NS(domain=d) for d in states]), state='starting')
    return env, hass, listeners, timers


def pushed(manager):
    return [(json.loads(c.args[0]), c.args[1]) for c in manager.SetEntityDescriptions.call_args_list]


class EntityDomains(unittest.IsolatedAsyncioTestCase):
    async def test_push_sends_domains_and_translations(self):
        env, hass, _, _ = load_entities(states=['sun'], registered=['light'], components=['sensor.mqtt', 'hue'])
        manager = NS(SetEntityDescriptions=Mock())
        await env['async_push'](hass, manager)
        (content, _), = pushed(manager)
        # A Platform with nothing loaded, a state, a registry entry and the top-level part of a component all count.
        for domain in ('lawn_mower', 'sun', 'light', 'sensor', 'hue'):
            self.assertIn(domain, content['domains'])
        self.assertEqual(content['domains'], sorted(content['domains']))
        self.assertEqual(content['translations'][ON], 'On')
        env['async_get_translations'].assert_awaited_once()
        self.assertEqual(env['async_get_translations'].await_args.args[1:3], ('en', 'entity_component'))

    async def test_unknown_domain_is_listed_but_not_translated(self):
        env, hass, _, _ = load_entities(states=['made_up'], unknown=['made_up'])
        manager = NS(SetEntityDescriptions=Mock())
        await env['async_push'](hass, manager)
        (content, _), = pushed(manager)
        self.assertIn('made_up', content['domains'])
        self.assertNotIn('made_up', env['async_get_translations'].await_args.args[3])
        self.assertNotIn('component.made_up.entity_component._.state.on', content['translations'])
        env['logger'].warning.assert_not_called()

    async def test_started_flag(self):
        env, hass, _, _ = load_entities()
        manager = NS(SetEntityDescriptions=Mock())
        await env['async_push'](hass, manager)
        hass.state = 'running'
        await env['async_push'](hass, manager)
        self.assertEqual([started for _, started in pushed(manager)], [False, True])

    async def test_debounce(self):
        env, hass, listeners, timers = load_entities()
        manager = NS(SetEntityDescriptions=Mock())
        unsubscribe = env['async_track'](hass, manager)
        self.assertEqual(set(listeners), set(EVENTS))
        listeners[LOADED](NS(data={}))
        listeners[STARTED](NS(data={}))
        listeners[LOADED](NS(data={}))
        self.assertEqual([t.delay for t in timers], [2, 2, 2])
        timers[0].cancel.assert_called_once()
        timers[1].cancel.assert_called_once()
        timers[2].cancel.assert_not_called()
        await timers[2].action(None)
        self.assertEqual(len(pushed(manager)), 1)
        unsubscribe()
        self.assertEqual(listeners, {})

    async def test_only_a_language_change_pushes_again(self):
        env, hass, listeners, timers = load_entities()
        env['async_track'](hass, NS(SetEntityDescriptions=Mock()))
        for data in ({'time_zone': 'Europe/Zagreb'}, {}, None):
            listeners[CONFIG](NS(data=data))
        self.assertEqual(timers, [])
        hass.config.language = 'hr'
        listeners[CONFIG](NS(data={'language': 'hr'}))
        self.assertEqual(len(timers), 1)
        await timers[0].action(None)
        self.assertEqual(env['async_get_translations'].await_args.args[1], 'hr')

    async def test_nothing_after_stop(self):
        env, hass, listeners, timers = load_entities()
        manager = NS(SetEntityDescriptions=Mock())
        unsubscribe = env['async_track'](hass, manager)
        gathering, release = asyncio.Event(), asyncio.Event()
        async def slow(*_args):
            gathering.set()
            await release.wait()
            return {}
        env['async_get_translations'].side_effect = slow
        listeners[LOADED](NS(data={}))
        push = asyncio.create_task(timers[0].action(None))
        await gathering.wait()
        unsubscribe()
        release.set()
        await push
        manager.SetEntityDescriptions.assert_not_called()

    async def test_stop_cancels_a_pending_push(self):
        env, hass, listeners, timers = load_entities()
        unsubscribe = env['async_track'](hass, NS(SetEntityDescriptions=Mock()))
        listeners[LOADED](NS(data={}))
        unsubscribe()
        timers[0].cancel.assert_called_once()

    async def test_failure_is_logged_not_raised(self):
        env, hass, _, _ = load_entities()
        manager = NS(SetEntityDescriptions=Mock(side_effect=RuntimeError('managed failure')))
        await env['async_push'](hass, manager)
        env['logger'].exception.assert_called_once()

    async def test_a_cancelled_first_push_leaves_no_listener(self):
        env, hass, listeners, _ = load_entities()
        env['loader'].async_get_integrations.side_effect = asyncio.CancelledError
        with self.assertRaises(asyncio.CancelledError):
            await env['async_start'](hass, NS(SetEntityDescriptions=Mock()))
        self.assertEqual(listeners, {})

    async def test_start_listens_then_pushes_once(self):
        env, hass, listeners, timers = load_entities()
        manager = NS(SetEntityDescriptions=Mock())
        unsubscribe = await env['async_start'](hass, manager)
        self.assertEqual(set(listeners), set(EVENTS))
        self.assertEqual(len(pushed(manager)), 1)
        self.assertEqual(timers, [])
        unsubscribe()
        self.assertEqual(listeners, {})

    async def test_new_entity_domain_pushes_again(self):
        env, hass, listeners, timers = load_entities(states=['sun'])
        manager = NS(SetEntityDescriptions=Mock())
        await env['async_start'](hass, manager)
        # Updates, removals and a domain the last push had do not push.
        listeners[REGISTRY](NS(data={'action': 'update', 'entity_id': 'made_up.a'}))
        listeners[REGISTRY](NS(data={'action': 'remove', 'entity_id': 'made_up.a'}))
        listeners[REGISTRY](NS(data={'action': 'create', 'entity_id': 'sun.sun'}))
        listeners[REGISTRY](NS(data={'action': 'create', 'entity_id': 'light.new'}))
        self.assertEqual(timers, [])
        # A second entity of the same new domain within the debounce makes one push, not two.
        hass.states.async_all = lambda: [NS(domain='sun'), NS(domain='made_up')]
        listeners[REGISTRY](NS(data={'action': 'create', 'entity_id': 'made_up.a'}))
        listeners[REGISTRY](NS(data={'action': 'create', 'entity_id': 'made_up.b'}))
        self.assertEqual(len(timers), 2)
        timers[0].cancel.assert_called_once()
        await timers[1].action(None)
        self.assertEqual(len(pushed(manager)), 2)
        self.assertIn('made_up', pushed(manager)[1][0]['domains'])
        # Now made_up is known: a further entity of it does not push.
        listeners[REGISTRY](NS(data={'action': 'create', 'entity_id': 'made_up.c'}))
        self.assertEqual(len(timers), 2)

    async def test_translation_failure_sends_domains_only(self):
        env, hass, _, _ = load_entities(states=['sun'])
        env['async_get_translations'].side_effect = RuntimeError('malformed en.json')
        manager = NS(SetEntityDescriptions=Mock())
        await env['async_push'](hass, manager)
        (content, _), = pushed(manager)
        self.assertIn('sun', content['domains'])
        self.assertEqual(content['translations'], {})
        env['logger'].warning.assert_called_once()
        env['logger'].exception.assert_not_called()

    async def test_integration_lookup_failure_sends_domains_only(self):
        env, hass, _, _ = load_entities(states=['sun'])
        env['loader'].async_get_integrations.side_effect = RuntimeError('lookup failed')
        manager = NS(SetEntityDescriptions=Mock())
        await env['async_push'](hass, manager)
        (content, _), = pushed(manager)
        self.assertIn('sun', content['domains'])
        self.assertEqual(content['translations'], {})
        env['async_get_translations'].assert_not_called()

    def test_setup_starts_after_services_and_before_init(self):
        source = Path('custom_components/hass_sharp/__init__.py').read_text()
        self.assertIn('await entities.async_start(hass, hass_sharp)', source)
        self.assertLess(source.index('services.async_start'), source.index('entities.async_start'))
        self.assertLess(source.index('entities.async_start'), source.index('hass_sharp.Init'))
        # Registered once, stopped on both cleanup paths.
        self.assertEqual(source.count('"hass_sharp_entities"'), 3)
