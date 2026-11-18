"""The entity platforms and the entity bridge against a real Home Assistant, with a fake .NET manager.

Each test builds its own HomeAssistant in a temporary config directory, adds a HassSharp config entry the way a
test harness does, and forwards the three platforms to it: Home Assistant's own entity platforms, state machine and
registries do the rest. Nothing here touches a running instance. Run it inside the Home Assistant container
(AGENTS.md, "Tests and verification").
"""
import asyncio
import importlib
import importlib.util
import json
import logging
import os
import pathlib
import sys
import tempfile
import threading
import types
import unittest
import warnings
from unittest.mock import patch

from test_entity_bridge import DotNetError, definition, description, device, snapshot

ROOT = pathlib.Path(os.environ.get('HASS_SHARP_TEST_ROOT', pathlib.Path(__file__).resolve().parents[2]))
PACKAGE = 'custom_components.hass_sharp'
DOMAIN = 'hass_sharp'


def load_package():
    """The integration's package without its __init__ (which loads .NET); Home Assistant imports its platforms."""
    parent = types.ModuleType('custom_components')
    parent.__path__ = [str(ROOT / 'custom_components')]
    sys.modules.setdefault('custom_components', parent)
    if not hasattr(sys.modules.get(PACKAGE), '__path__'):
        package = types.ModuleType(PACKAGE)
        package.__path__ = [str(ROOT / 'custom_components/hass_sharp')]
        sys.modules[PACKAGE] = package
    return (importlib.import_module(PACKAGE + '.entity_bridge'), importlib.import_module(PACKAGE + '.const'),
            importlib.import_module(PACKAGE + '.switch'))


class DotNetTask:
    """A finished .NET Task as Python.NET hands it over: GetResult returns or raises the task's exception, and the
    continuation runs on another thread, as on a .NET pool thread."""
    def __init__(self, error=None):
        self.error = error

    def GetAwaiter(self):
        return self

    def GetResult(self):
        if self.error is not None:
            raise self.error

    def OnCompleted(self, continuation):
        threading.Thread(target=continuation).start()


class Manager:
    """The part of HassSharpManager the bridge calls: EntityCommand(unique id, command, data JSON) -> Task."""
    def __init__(self):
        self.commands = []
        self.error = None

    def EntityCommand(self, unique_id, command, data_json):
        self.commands.append((unique_id, command, json.loads(data_json), threading.current_thread()))
        return DotNetTask(self.error)


class Records(logging.Handler):
    def __init__(self):
        super().__init__(logging.WARNING)
        self.records = []

    def emit(self, record):
        self.records.append(record)


def plug(*entities, **fields):
    """The plug definition: a relay switch, a power sensor and a motion binary sensor on the device "Plug one"."""
    return definition(*(entities or (description('relay'),
                                     description('power', 'sensor', 'Power', deviceClass='power',
                                                 options={'unit': 'W', 'stateClass': 'measurement', 'options': None}),
                                     description('motion', 'binary_sensor', 'Motion', deviceClass='motion'))),
                      **fields)


def controls():
    """One entity of each platform that takes commands besides the switch, with the options .NET describes, on the
    device "Plug one": a dimmable lamp and a bulb that is not, a blind with a position and a door without one."""
    return definition(
        description('restart', 'button', 'Restart', deviceClass='restart'),
        description('target', 'number', 'Target',
                    options={'min': 5, 'max': 30, 'step': 0.5, 'mode': 'box', 'unit': '°C'}),
        description('mode', 'select', 'Mode', options={'options': ['eco', 'comfort']}),
        description('message', 'text', 'Message',
                    options={'min': 1, 'max': 20, 'pattern': '^[a-z ]*$', 'mode': 'text'}),
        description('lamp', 'light', 'Lamp', options={'dimmable': True}),
        description('bulb', 'light', 'Bulb', options={'dimmable': False}),
        description('blind', 'cover', 'Blind', deviceClass='blind', options={'positionable': True}),
        description('door', 'cover', 'Door', deviceClass='garage', options={'positionable': False}))


# Each control's entity id, and a service call that reaches .NET for it.
CONTROLS = {'restart': 'button.plug_one_restart', 'target': 'number.plug_one_target', 'mode': 'select.plug_one_mode',
            'message': 'text.plug_one_message', 'lamp': 'light.plug_one_lamp', 'bulb': 'light.plug_one_bulb',
            'blind': 'cover.plug_one_blind', 'door': 'cover.plug_one_door'}
CALLS = {'restart': ('button', 'press', {}), 'target': ('number', 'set_value', {'value': 21.5}),
         'mode': ('select', 'select_option', {'option': 'eco'}), 'message': ('text', 'set_value', {'value': 'hi'}),
         'lamp': ('light', 'turn_on', {'brightness': 128}), 'bulb': ('light', 'turn_off', {}),
         'blind': ('cover', 'set_cover_position', {'position': 40}), 'door': ('cover', 'open_cover', {})}


# The platforms are Home Assistant's own entity classes; run these inside the HA container or a venv with it. A skip
# on the class, not the module, so that naming this module to unittest skips rather than fails.
@unittest.skipIf(importlib.util.find_spec('homeassistant') is None,
                 'Requires Home Assistant (the homeassistant package is not installed)')
class EntityPlatformsTest(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        from homeassistant import config_entries, loader
        from homeassistant.helpers import (area_registry, category_registry, device_registry, entity,
                                           entity_registry, floor_registry, frame, issue_registry, label_registry,
                                           translation)
        from homeassistant.core import CoreState, HomeAssistant
        # Cleanups run after asyncTearDown, and also when this fails part-way.
        self.records = Records()
        logging.getLogger().addHandler(self.records)
        self.addCleanup(logging.getLogger().removeHandler, self.records)
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.hass = hass = HomeAssistant(self.temp.name)
        # The platforms' integration depends on http, which would open Home Assistant's port: count it as set up.
        hass.config.components.add('http')
        hass.config.skip_pip = True
        with warnings.catch_warnings():
            # Home Assistant's http module, which its entity components import, warns about aiohttp on import.
            warnings.simplefilter('ignore', DeprecationWarning)
            importlib.import_module('homeassistant.components.http')
        self.bridge_module, self.const, self.switch = load_package()
        # .NET's Action wraps the continuation; there is no .NET runtime here.
        self.system = patch.dict(sys.modules, {'System': types.SimpleNamespace(Action=lambda f: f)})
        self.system.start()
        self.addCleanup(self.system.stop)
        hass.config_entries = config_entries.ConfigEntries(hass, {})
        loader.async_setup(hass)
        # What bootstrap's async_load_base_functionality does for entities, without importing every core component.
        for helper in (entity, frame, translation, device_registry):
            helper.async_setup(hass)
        await asyncio.gather(*(registry.async_load(hass) for registry in (
            area_registry, category_registry, device_registry, entity_registry, floor_registry, issue_registry,
            label_registry)), hass.config_entries.async_initialize())
        hass.set_state(CoreState.running)
        self.entry = config_entries.ConfigEntry(
            domain=DOMAIN, title='HassSharp', data={}, options={}, source='user', version=1, minor_version=1,
            unique_id=None, discovery_keys={}, subentries_data=None, state=config_entries.ConfigEntryState.LOADED)
        hass.config_entries._entries[self.entry.entry_id] = self.entry  # As MockConfigEntry.add_to_hass does.
        self.manager = Manager()
        await self.set_up_platforms()

    async def asyncTearDown(self):
        self.bridge.close()
        await self.hass.async_stop(force=True)
        # Home Assistant always warns that a custom integration is loaded, and asyncio's debug mode (on in these
        # tests) warns of slow steps; anything else is a defect here.
        unexpected = [r for r in self.records.records
                      if not (r.name == 'homeassistant.loader' and 'custom integration' in r.getMessage())
                      and not (r.name == 'asyncio' and r.getMessage().startswith('Executing '))]
        self.assertEqual([], [f'{r.name}: {r.getMessage()}\n{r.exc_text or ""}' for r in unexpected])

    async def set_up_platforms(self):
        """What async_setup_entry does for the entities: a bridge in hass.data, then the platforms forwarded."""
        self.bridge = self.bridge_module.EntityBridge(self.hass, self.entry, self.manager)
        self.hass.data.setdefault('hass_sharp_entity_bridge', {})[self.entry.entry_id] = self.bridge
        await self.hass.config_entries.async_forward_entry_setups(self.entry, self.const.PLATFORMS)

    async def unload_platforms(self):
        """What async_unload_entry does for the entities."""
        self.bridge.close()
        self.assertTrue(await self.hass.config_entries.async_unload_platforms(self.entry, self.const.PLATFORMS))
        self.hass.data['hass_sharp_entity_bridge'].pop(self.entry.entry_id)

    # .NET's side of the bridge

    async def answer(self, begin, argument):
        """The answer of one bridge operation, called as .NET calls it: from its own thread."""
        loop, answers = asyncio.get_running_loop(), []
        answered = loop.create_future()

        def answer(kind):
            def call(*args):
                answers.append((kind, *args))
                loop.call_soon_threadsafe(lambda: answered.done() or answered.set_result(None))
            return call
        worker = threading.Thread(target=begin, args=(argument, answer('success'), answer('error')))
        worker.start()
        worker.join()
        await asyncio.wait_for(answered, 5)
        await self.hass.async_block_till_done()
        self.assertEqual(1, len(answers), answers)
        return answers[0]

    async def sync(self, request):
        answer = await self.answer(self.bridge.begin_sync, request)
        self.assertEqual('success', answer[0], answer)
        return {key: value['entityId'] for key, value in json.loads(answer[1])['entities'].items()}

    async def push(self, *snapshots):
        worker = threading.Thread(target=self.bridge.push, args=(json.dumps(list(snapshots)),))
        worker.start()
        worker.join()
        await self.hass.async_block_till_done()

    # Home Assistant's side

    def state(self, entity_id):
        return self.hass.states.get(entity_id)

    @property
    def entities(self):
        from homeassistant.helpers import entity_registry as er
        return er.async_get(self.hass)

    @property
    def devices(self):
        from homeassistant.helpers import device_registry as dr
        return dr.async_get(self.hass)

    def registered(self, domain, unique_id):
        entity_id = self.entities.async_get_entity_id(domain, DOMAIN, unique_id)
        return self.entities.async_get(entity_id) if entity_id is not None else None

    def plug_device(self):
        return self.devices.async_get_device_by_identifier((DOMAIN, 'plug1'), self.entry.entry_id)

    # The tests

    async def test_synced_entities_reach_the_state_machine(self):
        ids = await self.sync(plug())
        self.assertEqual({'plug1:relay': 'switch.plug_one_relay', 'plug1:power': 'sensor.plug_one_power',
                          'plug1:motion': 'binary_sensor.plug_one_motion'}, ids)
        for entity_id in ids.values():
            # Unavailable until .NET's first snapshot.
            self.assertEqual('unavailable', self.state(entity_id).state)
        self.assertEqual('Plug one Relay', self.state('switch.plug_one_relay').attributes['friendly_name'])
        self.assertEqual('power', self.state('sensor.plug_one_power').attributes['device_class'])
        self.assertEqual('W', self.state('sensor.plug_one_power').attributes['unit_of_measurement'])
        self.assertEqual('measurement', self.state('sensor.plug_one_power').attributes['state_class'])
        self.assertEqual('motion', self.state('binary_sensor.plug_one_motion').attributes['device_class'])

    async def test_device_and_entities_are_registered_with_the_unique_id(self):
        await self.sync(plug())
        plug_device = self.plug_device()
        self.assertEqual((('Plug one', 'Shelly', 'Plug S'), {(DOMAIN, 'plug1')}, self.entry.entry_id),
                         ((plug_device.name, plug_device.manufacturer, plug_device.model), plug_device.identifiers,
                          plug_device.config_entry_id))
        for domain, key in (('switch', 'relay'), ('sensor', 'power'), ('binary_sensor', 'motion')):
            entry = self.registered(domain, f'plug1:{key}')
            self.assertEqual((DOMAIN, self.entry.entry_id, plug_device.id, None),
                             (entry.platform, entry.config_entry_id, entry.device_id, entry.disabled_by))

    async def test_push_changes_state(self):
        await self.sync(plug())
        await self.push(snapshot('plug1:relay', values={'is_on': True}, attributes={'voltage': 230}),
                        snapshot('plug1:power', values={'value': 42.5}),
                        snapshot('plug1:motion', values={'is_on': False}))
        relay = self.state('switch.plug_one_relay')
        self.assertEqual(('on', 230), (relay.state, relay.attributes['voltage']))
        self.assertEqual('42.5', self.state('sensor.plug_one_power').state)
        self.assertEqual('off', self.state('binary_sensor.plug_one_motion').state)
        await self.push(snapshot('plug1:relay', values={'is_on': False}))
        relay = self.state('switch.plug_one_relay')
        # The attributes are the snapshot's: one it leaves out is gone.
        self.assertEqual('off', relay.state)
        self.assertNotIn('voltage', relay.attributes)

    async def test_unavailable_push_gives_unavailable(self):
        await self.sync(plug())
        await self.push(snapshot('plug1:relay'))
        self.assertEqual('on', self.state('switch.plug_one_relay').state)
        await self.push(snapshot('plug1:relay', available=False))
        self.assertEqual('unavailable', self.state('switch.plug_one_relay').state)

    async def test_switch_turn_on_reaches_the_manager(self):
        await self.sync(plug())
        await self.push(snapshot('plug1:relay', values={'is_on': False}))
        await self.hass.services.async_call('switch', 'turn_on', {'entity_id': 'switch.plug_one_relay'}, blocking=True)
        await self.hass.services.async_call('switch', 'turn_off', {'entity_id': 'switch.plug_one_relay'}, blocking=True)
        self.assertEqual([('plug1:relay', 'turn_on', {}), ('plug1:relay', 'turn_off', {})],
                         [command[:3] for command in self.manager.commands])
        # .NET is called in the executor, never on the loop.
        self.assertNotIn(threading.main_thread(), [command[3] for command in self.manager.commands])
        # The state is .NET's to push: a command does not change it.
        self.assertEqual('off', self.state('switch.plug_one_relay').state)

    async def test_failed_command_fails_the_service_call(self):
        from homeassistant.exceptions import HomeAssistantError
        await self.sync(plug())
        await self.push(snapshot('plug1:relay'))
        self.manager.error = DotNetError('The plug did not answer')
        with self.assertRaises(HomeAssistantError) as raised:
            await self.hass.services.async_call('switch', 'turn_off', {'entity_id': 'switch.plug_one_relay'},
                                                blocking=True)
        # The .NET message, without its type and stack.
        self.assertEqual('The plug did not answer', str(raised.exception))

    # The controls: button, number, select, text, light and cover

    async def test_controls_reach_the_state_machine_with_their_features(self):
        ids = await self.sync(controls())
        self.assertEqual({f'plug1:{key}': entity_id for key, entity_id in CONTROLS.items()}, ids)
        for entity_id in ids.values():
            self.assertEqual('unavailable', self.state(entity_id).state)
        await self.push(*(snapshot(f'plug1:{key}', values={}) for key in CONTROLS))
        attributes = {key: self.state(entity_id).attributes for key, entity_id in CONTROLS.items()}
        self.assertEqual(('Plug one Restart', 'restart'),
                         (attributes['restart']['friendly_name'], attributes['restart']['device_class']))
        target = attributes['target']
        self.assertEqual((5, 30, 0.5, 'box', '°C'),
                         (target['min'], target['max'], target['step'], target['mode'], target['unit_of_measurement']))
        self.assertEqual(['eco', 'comfort'], attributes['mode']['options'])
        message = attributes['message']
        self.assertEqual((1, 20, '^[a-z ]*$', 'text'),
                         (message['min'], message['max'], message['pattern'], message['mode']))
        self.assertEqual((['brightness'], ['onoff']),
                         (attributes['lamp']['supported_color_modes'], attributes['bulb']['supported_color_modes']))
        from homeassistant.components.cover import CoverEntityFeature
        moves = CoverEntityFeature.OPEN | CoverEntityFeature.CLOSE | CoverEntityFeature.STOP
        self.assertEqual((moves | CoverEntityFeature.SET_POSITION, moves),
                         (attributes['blind']['supported_features'], attributes['door']['supported_features']))
        self.assertEqual(('blind', 'garage'), (attributes['blind']['device_class'], attributes['door']['device_class']))
        # Known to nothing yet: unknown, not unavailable.
        self.assertEqual({'unknown'}, {self.state(entity_id).state for entity_id in CONTROLS.values()})

    async def test_control_pushes_show(self):
        await self.sync(controls())
        await self.push(snapshot('plug1:restart', values={}),
                        snapshot('plug1:target', values={'value': 21.5}),
                        snapshot('plug1:mode', values={'option': 'comfort'}),
                        snapshot('plug1:message', values={'value': 'hello'}),
                        snapshot('plug1:lamp', values={'is_on': True, 'brightness': 128}),
                        snapshot('plug1:bulb', values={'is_on': True, 'brightness': None}),
                        snapshot('plug1:blind', values={'is_closed': None, 'position': 40}),
                        snapshot('plug1:door', values={'is_closed': True, 'position': None}))
        shown = {key: self.state(entity_id).state for key, entity_id in CONTROLS.items()}
        self.assertEqual({'restart': 'unknown', 'target': '21.5', 'mode': 'comfort', 'message': 'hello', 'lamp': 'on',
                          'bulb': 'on', 'blind': 'open', 'door': 'closed'}, shown)
        lamp, bulb = self.state(CONTROLS['lamp']).attributes, self.state(CONTROLS['bulb']).attributes
        self.assertEqual((128, 'brightness'), (lamp['brightness'], lamp['color_mode']))
        self.assertEqual('onoff', bulb['color_mode'])
        self.assertNotIn('brightness', bulb)
        self.assertEqual(40, self.state(CONTROLS['blind']).attributes['current_position'])
        self.assertNotIn('current_position', self.state(CONTROLS['door']).attributes)
        # A cover that says only where it is: closed at position 0. One that says nothing is unknown.
        await self.push(snapshot('plug1:blind', values={'is_closed': None, 'position': 0}),
                        snapshot('plug1:door', values={'is_closed': None, 'position': None}),
                        snapshot('plug1:lamp', values={'is_on': False, 'brightness': 128}))
        self.assertEqual(('closed', 'unknown', 'off'),
                         tuple(self.state(CONTROLS[key]).state for key in ('blind', 'door', 'lamp')))

    async def test_control_service_calls_reach_their_commands(self):
        from homeassistant.exceptions import HomeAssistantError
        await self.sync(controls())
        await self.push(*(snapshot(f'plug1:{key}', values={}) for key in CONTROLS))
        for domain, service, data in (
                ('button', 'press', {'entity_id': CONTROLS['restart']}),
                ('number', 'set_value', {'entity_id': CONTROLS['target'], 'value': 21.5}),
                ('select', 'select_option', {'entity_id': CONTROLS['mode'], 'option': 'eco'}),
                ('text', 'set_value', {'entity_id': CONTROLS['message'], 'value': 'hi there'}),
                ('light', 'turn_on', {'entity_id': CONTROLS['lamp'], 'brightness': 128}),
                ('light', 'turn_on', {'entity_id': CONTROLS['lamp']}),
                ('light', 'turn_on', {'entity_id': CONTROLS['bulb'], 'brightness': 128}),
                ('light', 'turn_off', {'entity_id': CONTROLS['lamp']}),
                ('cover', 'open_cover', {'entity_id': CONTROLS['blind']}),
                ('cover', 'close_cover', {'entity_id': CONTROLS['blind']}),
                ('cover', 'stop_cover', {'entity_id': CONTROLS['blind']}),
                ('cover', 'set_cover_position', {'entity_id': CONTROLS['blind'], 'position': 40})):
            await self.hass.services.async_call(domain, service, data, blocking=True)
        self.assertEqual([('plug1:restart', 'press', {}), ('plug1:target', 'set_value', {'value': 21.5}),
                          ('plug1:mode', 'select_option', {'option': 'eco'}),
                          ('plug1:message', 'set_value', {'value': 'hi there'}),
                          ('plug1:lamp', 'turn_on', {'brightness': 128}), ('plug1:lamp', 'turn_on', {}),
                          # Home Assistant drops the brightness of a light that cannot be dimmed.
                          ('plug1:bulb', 'turn_on', {}), ('plug1:lamp', 'turn_off', {}),
                          ('plug1:blind', 'open', {}), ('plug1:blind', 'close', {}), ('plug1:blind', 'stop', {}),
                          ('plug1:blind', 'set_position', {'position': 40})],
                         [command[:3] for command in self.manager.commands])
        self.assertNotIn(threading.main_thread(), [command[3] for command in self.manager.commands])
        # Home Assistant refuses what the entity does not offer before it is asked: a position for a cover without
        # one, a number beyond the maximum, an option not in the list, a text that does not match.
        for domain, service, data in (
                ('cover', 'set_cover_position', {'entity_id': CONTROLS['door'], 'position': 40}),
                ('number', 'set_value', {'entity_id': CONTROLS['target'], 'value': 45}),
                ('select', 'select_option', {'entity_id': CONTROLS['mode'], 'option': 'boost'}),
                ('text', 'set_value', {'entity_id': CONTROLS['message'], 'value': 'HI'})):
            with self.subTest(f'{domain}.{service}'), self.assertRaises((HomeAssistantError, ValueError)):
                await self.hass.services.async_call(domain, service, data, blocking=True)
        self.assertEqual(12, len(self.manager.commands))

    async def test_refused_control_command_fails_the_service_call(self):
        from homeassistant.exceptions import HomeAssistantError
        await self.sync(controls())
        await self.push(*(snapshot(f'plug1:{key}', values={}) for key in CONTROLS))
        for key, (domain, service, data) in CALLS.items():
            with self.subTest(key):
                self.manager.error = DotNetError(f'{key.title()} ({key}) takes nothing now')
                with self.assertRaises(HomeAssistantError) as raised:
                    await self.hass.services.async_call(domain, service, {'entity_id': CONTROLS[key], **data},
                                                        blocking=True)
                self.assertEqual(f'{key.title()} ({key}) takes nothing now', str(raised.exception))
        self.assertEqual(len(CALLS), len(self.manager.commands))

    async def test_resync_renames_a_live_entity(self):
        await self.sync(plug())
        await self.push(snapshot('plug1:relay'))
        ids = await self.sync(plug(description('relay', name='Main relay', icon='mdi:power-socket-eu',
                                               entityCategory='config', deviceClass='outlet')))
        # The entity id is fixed at creation (spike rule 5); the name and the state follow.
        self.assertEqual({'plug1:relay': 'switch.plug_one_relay'}, ids)
        relay = self.state('switch.plug_one_relay')
        self.assertEqual(('on', 'Plug one Main relay', 'mdi:power-socket-eu'),
                         (relay.state, relay.attributes['friendly_name'], relay.attributes['icon']))
        self.assertEqual('outlet', relay.attributes['device_class'])
        entry = self.registered('switch', 'plug1:relay')
        self.assertEqual(('Main relay', 'mdi:power-socket-eu', 'config', 'outlet'),
                         (entry.original_name, entry.original_icon, entry.entity_category, entry.original_device_class))

    async def test_removed_entity_leaves_the_registry(self):
        await self.sync(plug())
        await self.sync(plug(description('relay')))
        self.assertIsNone(self.registered('sensor', 'plug1:power'))
        self.assertIsNone(self.state('sensor.plug_one_power'))
        self.assertIsNone(self.registered('binary_sensor', 'plug1:motion'))
        self.assertIsNotNone(self.registered('switch', 'plug1:relay'))
        # Removing the definition removes its device and the rest.
        self.assertEqual(('success',), await self.answer(self.bridge.begin_remove, 'plug1'))
        self.assertIsNone(self.registered('switch', 'plug1:relay'))
        self.assertIsNone(self.state('switch.plug_one_relay'))
        self.assertIsNone(self.plug_device())

    async def test_failed_sync_keeps_the_entities_it_dropped(self):
        await self.sync(plug())
        await self.push(snapshot('plug1:power', values={'value': 5}))
        # The new revision drops power and motion, and its device has a field Home Assistant refuses.
        answer = await self.answer(self.bridge.begin_sync,
                                   plug(description('relay'), device=device(configurationUrl='not a url')))
        self.assertEqual('error', answer[0], answer)
        self.assertIn('configuration_url', answer[1])
        # The bridge logged the failure; that is expected here.
        self.records.records = [r for r in self.records.records
                                if not (r.name == self.const.logger.name and 'Cannot sync' in r.getMessage())]
        self.assertIsNotNone(self.registered('sensor', 'plug1:power'))
        self.assertIsNotNone(self.registered('binary_sensor', 'plug1:motion'))
        await self.push(snapshot('plug1:power', values={'value': 6}))
        self.assertEqual('6', self.state('sensor.plug_one_power').state)  # Still live.
        # A sync that succeeds removes them.
        await self.sync(plug(description('relay')))
        self.assertIsNone(self.registered('sensor', 'plug1:power'))
        self.assertIsNone(self.state('sensor.plug_one_power'))
        self.assertIsNone(self.registered('binary_sensor', 'plug1:motion'))

    def narrowed(self, **options):
        """The controls with some entities' options replaced, by key."""
        request = json.loads(controls())
        for entity in request['entities']:
            if entity['key'] in options:
                entity['options'] = {**entity['options'], **options[entity['key']]}
        return json.dumps(request)

    def write_failures(self):
        """The log records of state writes Home Assistant refused, which a sync must not cause."""
        return [r.getMessage() for r in self.records.records if r.levelno >= logging.WARNING
                and r.name != 'homeassistant.loader' and not r.getMessage().startswith('Executing ')]

    async def test_a_text_too_long_for_its_new_maximum_is_dropped(self):
        await self.sync(controls())
        await self.push(snapshot('plug1:message', values={'value': 'hello there'}))
        self.assertEqual('hello there', self.state(CONTROLS['message']).state)
        # The sync succeeds, and the value the text can no longer show is gone: its state is unknown.
        await self.sync(self.narrowed(message={'max': 5}))
        self.assertEqual([], self.write_failures())
        message = self.state(CONTROLS['message'])
        self.assertEqual(('unknown', 5), (message.state, message.attributes['max']))
        await self.push(snapshot('plug1:message', values={'value': 'hi'}))
        self.assertEqual('hi', self.state(CONTROLS['message']).state)
        # The same for a pattern it no longer matches, and a minimum it is now shorter than.
        await self.sync(self.narrowed(message={'max': 5, 'pattern': '^[0-9]*$'}))
        self.assertEqual('unknown', self.state(CONTROLS['message']).state)
        await self.push(snapshot('plug1:message', values={'value': '42'}))
        await self.sync(self.narrowed(message={'min': 3, 'max': 5, 'pattern': '^[0-9]*$'}))
        self.assertEqual(('unknown', []), (self.state(CONTROLS['message']).state, self.write_failures()))

    async def test_a_password_text_dropped_is_not_quoted(self):
        await self.sync(self.narrowed(message={'mode': 'password'}))
        await self.push(snapshot('plug1:message', values={'value': 'hunter two'}))
        await self.sync(self.narrowed(message={'mode': 'password', 'max': 5}))
        self.assertEqual('unknown', self.state(CONTROLS['message']).state)
        self.assertFalse([r for r in self.records.records if 'hunter' in r.getMessage() or 'hunter' in (r.exc_text or '')])

    async def test_a_select_option_removed_is_dropped(self):
        await self.sync(controls())
        await self.push(snapshot('plug1:mode', values={'option': 'comfort'}))
        self.assertEqual('comfort', self.state(CONTROLS['mode']).state)
        await self.sync(self.narrowed(mode={'options': ['eco', 'boost']}))
        self.assertEqual([], self.write_failures())
        mode = self.state(CONTROLS['mode'])
        self.assertEqual(('unknown', ['eco', 'boost']), (mode.state, mode.attributes['options']))
        self.assertIsNone(self.bridge._live['plug1:mode'].current_option)
        await self.push(snapshot('plug1:mode', values={'option': 'boost'}))
        self.assertEqual('boost', self.state(CONTROLS['mode']).state)

    async def test_a_sensor_value_its_new_options_lack_is_dropped(self):
        def sensor(**options):
            return definition(description('level', 'sensor', 'Level', deviceClass='enum',
                                          options={'unit': None, 'stateClass': None, 'options': ['low', 'high'], **options}))
        await self.sync(sensor())
        await self.push(snapshot('plug1:level', values={'value': 'high'}))
        self.assertEqual('high', self.state('sensor.plug_one_level').state)
        await self.sync(sensor(options=['low', 'medium']))
        self.assertEqual([], self.write_failures())
        self.assertEqual('unknown', self.state('sensor.plug_one_level').state)
        await self.push(snapshot('plug1:level', values={'value': 'medium'}))
        self.assertEqual('medium', self.state('sensor.plug_one_level').state)

    async def test_a_sync_whose_write_fails_leaves_the_entity_as_it_was(self):
        await self.sync(controls())
        await self.push(snapshot('plug1:message', values={'value': 'hello'}))
        live = self.bridge._live['plug1:message']
        before = (live.name, live.native_max, live.native_value, live.pattern)

        def refuse():
            raise ValueError('Home Assistant refused the state')
        with patch.object(live, 'async_write_ha_state', refuse):
            answer = await self.answer(self.bridge.begin_sync,
                                       self.narrowed(message={'max': 8, 'pattern': '^[a-z]*$'}).replace('"Message"', '"Note"'))
        self.assertEqual('error', answer[0], answer)
        self.records.records = [r for r in self.records.records
                                if not (r.name == self.const.logger.name and 'Cannot sync' in r.getMessage())]
        # Nothing of the refused description is left on it: not half its options, not its name.
        self.assertEqual(before, (live.name, live.native_max, live.native_value, live.pattern))
        await self.push(snapshot('plug1:message', values={'value': 'hello again'}))
        self.assertEqual('hello again', self.state(CONTROLS['message']).state)

    async def test_key_whose_domain_changes_is_added_anew(self):
        await self.sync(plug(description('main', 'switch', 'Main')))
        ids = await self.sync(plug(description('main', 'sensor', 'Main')))
        self.assertEqual({'plug1:main': 'sensor.plug_one_main'}, ids)
        self.assertIsNone(self.registered('switch', 'plug1:main'))
        self.assertIsNone(self.state('switch.plug_one_main'))
        await self.push(snapshot('plug1:main', values={'value': 3}))
        self.assertEqual('3', self.state('sensor.plug_one_main').state)

    async def test_prune_removes_only_deleted_definitions(self):
        await self.sync(plug())
        await self.sync(definition(description('fan', definition='fan1'), definition_id='fan1', name='Fan',
                                   device=device('Fan')))
        self.assertEqual(('success',), await self.answer(self.bridge.begin_prune, '["fan1"]'))
        self.assertIsNone(self.plug_device())
        self.assertIsNone(self.registered('switch', 'plug1:relay'))
        self.assertEqual('unavailable', self.state('switch.fan_relay').state)

    async def test_unload_keeps_registry_entries(self):
        await self.sync(plug())
        await self.push(snapshot('plug1:relay'))
        await self.unload_platforms()
        # Unavailable, restored from the registry, under the same entity id (spike rule 10); the device stays.
        relay = self.state('switch.plug_one_relay')
        self.assertEqual(('unavailable', True), (relay.state, relay.attributes['restored']))
        self.assertEqual('unavailable', self.state('sensor.plug_one_power').state)
        self.assertIsNotNone(self.registered('switch', 'plug1:relay'))
        self.assertIsNotNone(self.plug_device())
        # A reload: the next sync takes them back.
        await self.set_up_platforms()
        self.assertEqual('unavailable', self.state('switch.plug_one_relay').state)
        ids = await self.sync(plug())
        self.assertEqual('switch.plug_one_relay', ids['plug1:relay'])
        await self.push(snapshot('plug1:relay'))
        relay = self.state('switch.plug_one_relay')
        self.assertEqual('on', relay.state)
        self.assertNotIn('restored', relay.attributes)

    # Points the fakes of test_entity_bridge could not settle

    async def test_disabled_entity_syncs_and_takes_pushes_quietly(self):
        from homeassistant.helpers import entity_registry as er
        request = plug(description('relay', enabledByDefault=False),
                       description('power', 'sensor', 'Power', options={'unit': 'W'}))
        ids = await self.sync(request)
        entry = self.registered('switch', 'plug1:relay')
        self.assertEqual(entry.entity_id, ids['plug1:relay'])
        self.assertIs(er.RegistryEntryDisabler.INTEGRATION, entry.disabled_by)
        self.assertIsNone(self.state(entry.entity_id))
        await self.push(snapshot('plug1:relay'), snapshot('plug1:power', values={'value': 3}))
        self.assertIsNone(self.state(entry.entity_id))
        self.assertEqual('3', self.state('sensor.plug_one_power').state)
        ids = await self.sync(plug(description('relay', name='Main relay', enabledByDefault=False),
                                   description('power', 'sensor', 'Main power', options={'unit': 'W'})))
        self.assertEqual(entry.entity_id, ids['plug1:relay'])
        self.assertEqual('Plug one Main power', self.state('sensor.plug_one_power').attributes['friendly_name'])
        # tearDown checks that nothing was logged.

    async def test_unit_change_of_a_live_sensor_shows_the_new_unit(self):
        await self.sync(self.power('W'))
        await self.push(snapshot('plug1:power', values={'value': 1500}))
        self.assertEqual(('1500', 'W'), self.reading('sensor.plug_one_power'))
        await self.sync(self.power('kW'))
        await self.push(snapshot('plug1:power', values={'value': 1.5}))
        # Live, Home Assistant stores no unit of its own: the state takes the new one, and so does the registry, from
        # which the sensor takes its display precision.
        self.assertEqual(('1.5', 'kW'), self.reading('sensor.plug_one_power'))
        entry = self.registered('sensor', 'plug1:power')
        self.assertEqual('kW', entry.unit_of_measurement)
        self.assertEqual(2, entry.options['sensor']['suggested_display_precision'])
        self.assertNotIn('sensor.private', entry.options)

    async def test_unit_change_across_a_reload_keeps_the_first_unit(self):
        await self.sync(self.power('W'))
        await self.unload_platforms()
        await self.set_up_platforms()
        await self.sync(self.power('kW'))
        await self.push(snapshot('plug1:power', values={'value': 1.5}))
        # Re-added with another unit of the same device class, it keeps the unit of its registry entry and converts
        # to it (spike rule 7).
        self.assertEqual(('1500.0', 'W'), self.reading('sensor.plug_one_power'))
        entry = self.registered('sensor', 'plug1:power')
        self.assertEqual({'suggested_unit_of_measurement': 'W'}, entry.options['sensor.private'])
        # A sync of the live entity writes the registry the unit the state shows, as Home Assistant does on an add.
        await self.sync(self.power('kW'))
        await self.push(snapshot('plug1:power', values={'value': 2.5}))
        self.assertEqual(('2500.0', 'W'), self.reading('sensor.plug_one_power'))
        self.assertEqual('W', self.registered('sensor', 'plug1:power').unit_of_measurement)

    async def test_unit_change_without_conversion_shows_the_new_unit(self):
        count = lambda unit: plug(description('count', 'sensor', 'Count', options={'unit': unit}))
        await self.sync(count('items'))
        await self.push(snapshot('plug1:count', values={'value': 3}))
        self.assertEqual(('3', 'items'), self.reading('sensor.plug_one_count'))
        await self.sync(count('pieces'))
        self.assertEqual(('3', 'pieces'), self.reading('sensor.plug_one_count'))
        self.assertEqual('pieces', self.registered('sensor', 'plug1:count').unit_of_measurement)
        # Nothing to convert, so a re-add takes the new unit too.
        await self.unload_platforms()
        await self.set_up_platforms()
        await self.sync(count('units'))
        await self.push(snapshot('plug1:count', values={'value': 4}))
        self.assertEqual(('4', 'units'), self.reading('sensor.plug_one_count'))
        self.assertEqual('units', self.registered('sensor', 'plug1:count').unit_of_measurement)

    @staticmethod
    def power(unit):
        return plug(description('power', 'sensor', 'Power', deviceClass='power',
                                options={'unit': unit, 'stateClass': 'measurement'}))

    def reading(self, entity_id):
        state = self.state(entity_id)
        return state.state, state.attributes.get('unit_of_measurement')

    async def test_device_field_set_to_null_is_cleared(self):
        await self.sync(plug(description('relay')))
        await self.sync(plug(description('relay'), device=device(model=None, swVersion='1.2')))
        plug_device = self.plug_device()
        self.assertEqual(('Plug one', None, '1.2'), (plug_device.name, plug_device.model, plug_device.sw_version))
        # A device whose name is cleared: Home Assistant names the entity by itself alone.
        await self.sync(plug(description('relay'), device=device(name=None)))
        self.assertIsNone(self.plug_device().name)
        self.assertEqual('Relay', self.state('switch.plug_one_relay').attributes['friendly_name'])

    async def test_via_device_links_and_unlinks(self):
        await self.sync(definition(description('fan', definition='hub1'), definition_id='hub1', name='Hub',
                                   device=device('Hub')))
        hub = self.devices.async_get_device_by_identifier((DOMAIN, 'hub1'), self.entry.entry_id)
        await self.sync(plug(description('relay'), device=device(via={'definition': 'hub1'})))
        self.assertEqual(hub.id, self.plug_device().via_device_id)
        await self.sync(plug(description('relay'), device=device(via=None)))
        self.assertIsNone(self.plug_device().via_device_id)
        await self.sync(plug(description('relay'), device=device(via={'deviceId': hub.id})))
        self.assertEqual(hub.id, self.plug_device().via_device_id)
        # A parent that does not exist is left out (Home Assistant refuses an unknown via device id).
        await self.sync(plug(description('relay'), device=device(via={'definition': 'gone'})))
        self.assertIsNone(self.plug_device().via_device_id)

    async def test_close_during_an_add_adds_nothing_to_the_unloaded_platform(self):
        # Home Assistant finishes an add it started even after the platform unloaded, leaving an entity that keeps
        # its unique id taken until a restart: unload waits for that add before it unloads the platforms.
        entered, release = asyncio.Event(), asyncio.Event()
        added = self.switch.HassSharpSwitch.async_added_to_hass

        async def slow_add(entity):
            entered.set()
            await release.wait()
            await added(entity)
        answers = []
        with patch.object(self.switch.HassSharpSwitch, 'async_added_to_hass', slow_add):
            self.bridge.begin_sync(plug(), lambda *a: answers.append(('success', *a)),
                                   lambda *a: answers.append(('error', *a)))
            await asyncio.wait_for(entered.wait(), 5)
            self.bridge.close()
            self.assertEqual([('error', 'closed')], answers)
            waiting = asyncio.ensure_future(self.bridge.async_wait_closed())
            await asyncio.sleep(0.01)
            self.assertFalse(waiting.done())
            release.set()
            await asyncio.wait_for(waiting, 5)
            self.assertTrue(await self.hass.config_entries.async_unload_platforms(self.entry, self.const.PLATFORMS))
            await self.hass.async_block_till_done()
        self.hass.data['hass_sharp_entity_bridge'].pop(self.entry.entry_id)
        # The relay went with the platform; the sync stopped there, so the sensors were never added or registered.
        relay = self.state('switch.plug_one_relay')
        self.assertEqual(('unavailable', True), (relay.state, relay.attributes.get('restored')))
        self.assertIsNone(self.registered('sensor', 'plug1:power'))
        self.assertIsNone(self.registered('binary_sensor', 'plug1:motion'))
        self.assertEqual([('error', 'closed')], answers)
        # Calls after the close are refused, not run against the unloaded platforms.
        self.assertEqual(('error', 'closed'), await self.answer(self.bridge.begin_sync, plug()))
        await self.push(snapshot('plug1:relay'))
        self.assertEqual('unavailable', self.state('switch.plug_one_relay').state)
        # A reload takes the relay back under its unique id.
        await self.set_up_platforms()
        await self.sync(plug())
        await self.push(snapshot('plug1:relay'))
        self.assertEqual('on', self.state('switch.plug_one_relay').state)

if __name__ == '__main__':
    unittest.main()
