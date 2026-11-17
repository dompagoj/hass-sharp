"""The entity bridge and entity base against fake registries, a fake entity platform and a real event loop.

The fakes behave as the spike findings (docs/superpowers/plans/2026-11-17-custom-entities-phase-1-spike.md) say
Home Assistant 2026.11 does: writing a state before the entity is added raises, a disabled registry entry is not
added, removing a device removes the entities on it, a via device that does not exist is ignored.
"""
import ast
import asyncio
import enum
import json
import threading
import unittest
from pathlib import Path
from types import SimpleNamespace as NS
from unittest.mock import Mock

DOMAIN = 'hass_sharp'
ENTRY = 'entry1'


class EntityCategory(enum.StrEnum):
    CONFIG = 'config'
    DIAGNOSTIC = 'diagnostic'


class HomeAssistantError(Exception):
    pass


class DotNetError(Exception):
    """As Python.NET raises a .NET exception: its text names the type and the stack, `Message` is the message."""
    def __init__(self, message):
        super().__init__(f'System.InvalidOperationException: {message}\n   at HassSharp.Manager.EntityCommand()')
        self.Message = message


class Entity:
    """The part of Home Assistant's Entity the base and the bridge use."""
    entity_id = None
    hass = None

    def __init__(self):
        self.written = []

    @property
    def unique_id(self):
        return self._attr_unique_id

    @property
    def icon(self):
        return getattr(self, '_attr_icon', None)

    @property
    def entity_category(self):
        return getattr(self, '_attr_entity_category', None)

    @property
    def device_info(self):
        return getattr(self, '_attr_device_info', None)

    def async_write_ha_state(self):
        if self.hass is None:  # spike rule 11
            raise RuntimeError(f'Attribute hass is None for {self.unique_id}')
        self.written.append({'available': self._attr_available, 'name': self._attr_name,
                             'attributes': getattr(self, '_attr_extra_state_attributes', None),
                             'is_on': getattr(self, '_attr_is_on', None), 'thread': threading.get_ident()})


class EntityRegistry:
    def __init__(self):
        self.entities = {}
        self.calls = []

    def add(self, domain, unique_id, entity_id=None, config_entry_id=ENTRY, device_id=None, disabled_by=None):
        entity_id = entity_id or f"{domain}.{unique_id.replace(':', '_')}"
        self.entities[entity_id] = NS(entity_id=entity_id, domain=domain, platform=DOMAIN, unique_id=unique_id,
                                      config_entry_id=config_entry_id, device_id=device_id, disabled_by=disabled_by,
                                      original_icon=None, entity_category=None, unit_of_measurement=None)
        return self.entities[entity_id]

    def async_get(self, entity_id):
        return self.entities.get(entity_id)

    def async_get_entity_id(self, domain, platform, unique_id):
        return next((e.entity_id for e in self.entities.values()
                     if (e.domain, e.platform, e.unique_id) == (domain, platform, unique_id)), None)

    def async_update_entity(self, entity_id, **changes):
        self.calls.append(('update', entity_id, changes, threading.get_ident()))
        entry = self.entities[entity_id]
        for key, value in changes.items():
            setattr(entry, key, value)
        return entry

    def async_remove(self, entity_id):
        self.calls.append(('remove', entity_id, threading.get_ident()))
        del self.entities[entity_id]


class DeviceRegistry:
    FIELDS = ('name', 'manufacturer', 'model', 'sw_version', 'hw_version', 'configuration_url', 'via_device_id')

    def __init__(self, entities):
        self.devices = {}
        self.calls = []
        self._entities = entities

    def add(self, definition_id, config_entry_id=ENTRY, **fields):
        device = NS(id=f'device-{definition_id}', identifiers={(DOMAIN, definition_id)}, config_entry_id=config_entry_id,
                    **{field: None for field in self.FIELDS})
        for key, value in fields.items():
            setattr(device, key, value)
        self.devices[device.id] = device
        return device

    def async_get(self, device_id):
        return self.devices.get(device_id)

    def async_get_device(self, identifiers=None, connections=None):
        return next((d for d in self.devices.values() if d.identifiers & set(identifiers or ())), None)

    def async_get_or_create(self, *, config_entry_id, identifiers, **fields):
        self.calls.append(('get_or_create', dict(fields), threading.get_ident()))
        device = self.async_get_device(identifiers=identifiers)
        if device is None:
            device = self.add(next(iter(identifiers))[1], config_entry_id)
        if 'via_device' in fields:
            parent = self.async_get_device(identifiers={fields.pop('via_device')})
            if parent is not None:  # A parent that does not exist is ignored (spike rule 8).
                device.via_device_id = parent.id
        fields.pop('suggested_area', None)  # Accepted, but not a field of DeviceEntry.
        for key, value in fields.items():
            setattr(device, key, value)
        return device

    def async_update_device(self, device_id, **changes):
        self.calls.append(('update', device_id, changes))
        for key, value in changes.items():
            setattr(self.devices[device_id], key, value)
        return self.devices[device_id]

    def async_remove_device(self, device_id):
        self.calls.append(('remove', device_id))
        del self.devices[device_id]
        # The entities on it go too, live ones included (spike rule 9).
        for entry in [e for e in self._entities.entities.values() if e.device_id == device_id]:
            del self._entities.entities[entry.entity_id]


class Platform:
    """Adds entities as EntityPlatform.async_add_entities does in Home Assistant 2026.11.1 (`_async_add_entity_impl`):
    it registers each with its device and sets `entity_id` from the registry entry *before* it refuses one (an entity
    id that already exists, say) or skips a disabled entry. Either way `add_to_platform_abort()` resets `hass` and
    `platform` but leaves `entity_id`, and only an entity really added is in `entities`. Failures are logged (here:
    ignored), not raised (spike rules 2, 4)."""
    def __init__(self, domain, hass, registry, devices):
        self.domain, self.hass, self.registry, self.devices = domain, hass, registry, devices
        self.entities = {}  # entity id -> entity, as EntityPlatform.entities
        self.added = []
        self.refuse = set()
        self.gate = None
        self.entered = asyncio.Event()

    async def async_add_entities(self, new):
        self.entered.set()
        if self.gate is not None:
            await self.gate.wait()
        for entity in new:
            self.added.append(entity)
            entity.hass, entity.platform = self.hass, self  # add_to_platform_start
            info = entity.device_info
            device = self.devices.async_get_or_create(config_entry_id=ENTRY, **info) if info else None
            entity_id = self.registry.async_get_entity_id(self.domain, DOMAIN, entity.unique_id)
            if entity_id is None:
                enabled = getattr(entity, '_attr_entity_registry_enabled_default', True)
                entry = self.registry.add(self.domain, entity.unique_id, device_id=device.id if device else None,
                                          disabled_by=None if enabled else 'integration')
            else:
                entry = self.registry.async_get(entity_id)
                entry.device_id = device.id if device else None
            entity.entity_id = entry.entity_id
            if entry.disabled_by is not None or entity.unique_id in self.refuse:
                entity.hass, entity.platform = None, None  # add_to_platform_abort
                continue
            self.entities[entity.entity_id] = entity
            entity.async_write_ha_state()


def load(name, env):
    tree = ast.parse(Path(f'custom_components/hass_sharp/{name}.py').read_text())
    tree.body = [n for n in tree.body if not isinstance(n, ast.ImportFrom)]
    exec(compile(tree, f'<{name}>', 'exec'), env)
    return env


def device(name='Plug one', **fields):
    return {'name': name, 'manufacturer': 'Shelly', 'model': 'Plug S', 'swVersion': None, 'hwVersion': None,
            'configurationUrl': None, 'suggestedArea': None, 'via': None, **fields}


def description(key, domain='switch', name='Relay', definition='plug1', **fields):
    return {'uniqueId': f'{definition}:{key}', 'key': key, 'domain': domain, 'name': name, 'icon': None,
            'deviceClass': None, 'entityCategory': None, 'enabledByDefault': True,
            'commands': ['turn_on', 'turn_off'] if domain == 'switch' else [], 'options': {}, **fields}


def definition(*entities, definition_id='plug1', name='Plug one', device=device()):
    return json.dumps({'definitionId': definition_id, 'name': name, 'device': device,
                       'entities': list(entities) or [description('relay', definition=definition_id)]})


# The request .NET sends for a plug (tests/Runtime/EntityHostTests.cs pins this text).
PLUG = """{"definitionId":"plug1","name":"Plug one","device":{"name":"Plug one","manufacturer":"Shelly","model":"Plug S","swVersion":null,"hwVersion":null,"configurationUrl":null,"suggestedArea":null,"via":null},"entities":[{"uniqueId":"plug1:relay","key":"relay","domain":"switch","name":"Relay","icon":null,"deviceClass":null,"entityCategory":null,"enabledByDefault":true,"commands":["turn_on","turn_off"],"options":{}},{"uniqueId":"plug1:power","key":"power","domain":"sensor","name":"Power","icon":null,"deviceClass":"power","entityCategory":null,"enabledByDefault":true,"commands":[],"options":{"unit":"W","stateClass":"measurement","options":null}}]}"""


def snapshot(unique_id='plug1:relay', available=True, values=None, attributes=None):
    return {'uniqueId': unique_id, 'available': available, 'values': {'is_on': True} if values is None else values,
            'attributes': attributes or {}}


class EntityBridgeTest(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.loop = asyncio.get_running_loop()
        self.loop_errors = []
        self.loop.set_exception_handler(lambda _loop, context: self.loop_errors.append(context))
        self.entities = EntityRegistry()
        self.devices = DeviceRegistry(self.entities)
        loop = self.loop
        self.hass = NS(loop=loop, data={}, async_create_task=lambda coro, name=None, eager_start=True:
                       asyncio.Task(coro, loop=loop, name=name, eager_start=eager_start))
        base = load('entity_base', {'Entity': Entity, 'EntityCategory': EntityCategory})['HassSharpEntity']

        class Switch(base):
            def apply_values(self, values):
                self._attr_is_on = values.get('is_on')

        class Sensor(base):
            def apply_values(self, values):
                self._attr_native_value = values.get('value')

        self.manager = NS(EntityCommand=Mock(return_value='task'))
        self.dotnet_calls = []

        async def run_dotnet_task(hass, target, *args):
            self.dotnet_calls.append((target, args))
            return target(*args)

        self.logger = Mock()
        env = load('entity_bridge', {
            'er': NS(async_get=lambda hass: self.entities,
                     async_entries_for_config_entry=lambda registry, entry_id:
                         [e for e in registry.entities.values() if e.config_entry_id == entry_id]),
            'dr': NS(async_get=lambda hass: self.devices,
                     async_entries_for_config_entry=lambda registry, entry_id:
                         [d for d in registry.devices.values() if d.config_entry_id == entry_id]),
            'dotnet_tasks': NS(async_run_dotnet_task=run_dotnet_task),
            'HomeAssistantError': HomeAssistantError, 'DOMAIN': DOMAIN, 'logger': self.logger})
        self.bridge = env['EntityBridge'](self.hass, NS(entry_id=ENTRY), self.manager)
        self.platforms = {}
        for domain, factory in (('switch', Switch), ('sensor', Sensor)):
            self.platforms[domain] = Platform(domain, self.hass, self.entities, self.devices)
            self.bridge.attach_platform(domain, self.platforms[domain], factory)

    def tearDown(self):
        self.assertEqual(self.loop_errors, [], 'an exception reached the event loop')

    async def answers(self, begin, argument, settle=True):
        """Every answer `begin` gives, once the first one arrived."""
        answers, first = [], self.loop.create_future()

        def answer(kind):
            def call(*args):
                answers.append((kind, *args))
                if not first.done():
                    first.set_result(None)
            return call
        begin(argument, answer('success'), answer('error'))
        await asyncio.wait_for(asyncio.shield(first), 2)
        if settle:
            await self.settle()
        return answers

    async def settle(self):
        for _ in range(5):
            await asyncio.sleep(0)

    async def sync(self, request):
        answers = await self.answers(self.bridge.begin_sync, request)
        self.assertEqual([a[0] for a in answers], ['success'], answers)
        return json.loads(answers[0][1])

    async def push(self, *snapshots):
        self.bridge.push(json.dumps(list(snapshots)))
        await self.settle()

    def live(self, unique_id):
        added = [e for p in self.platforms.values() for e in p.added if e.unique_id == unique_id and e.hass]
        self.assertEqual(len(added), 1, f'{unique_id} added {len(added)} times')
        return added[0]

    def registry_updates(self, entity_id):
        return [call[2] for call in self.entities.calls if call[0] == 'update' and call[1] == entity_id]

    # Sync

    async def test_sync_adds_entities_through_the_platform(self):
        await self.sync(PLUG)
        relay, power = self.live('plug1:relay'), self.live('plug1:power')
        self.assertEqual((relay._attr_name, power._attr_name), ('Relay', 'Power'))
        self.assertEqual(power._attr_device_class, 'power')
        self.assertEqual(relay.device_info['identifiers'], {(DOMAIN, 'plug1')})
        plug = self.devices.async_get_device(identifiers={(DOMAIN, 'plug1')})
        self.assertEqual((plug.name, plug.manufacturer, plug.model), ('Plug one', 'Shelly', 'Plug S'))
        self.assertEqual({e.device_id for e in self.entities.entities.values()}, {plug.id})
        # Unavailable until .NET says otherwise.
        self.assertFalse(relay.written[-1]['available'])

    async def test_sync_updates_a_live_entity_in_place(self):
        await self.sync(definition())
        relay = self.live('plug1:relay')
        await self.sync(definition(description('relay', name='Main relay')))
        self.assertIs(self.live('plug1:relay'), relay)
        self.assertEqual(len(self.platforms['switch'].added), 1)
        self.assertEqual(relay.written[-1]['name'], 'Main relay')

    async def test_sync_writes_icon_category_and_unit_to_the_registry(self):
        await self.sync(definition(description('power', 'sensor', 'Power', options={'unit': 'W'})))
        await self.sync(definition(description('power', 'sensor', 'Power', icon='mdi:flash', entityCategory='diagnostic',
                                               options={'unit': 'kW'})))
        changes = self.registry_updates('sensor.plug1_power')[-1]
        self.assertEqual({k: changes[k] for k in ('original_icon', 'entity_category', 'unit_of_measurement')},
                         {'original_icon': 'mdi:flash', 'entity_category': 'diagnostic', 'unit_of_measurement': 'kW'})
        # Home Assistant's registry takes an EntityCategory, not its text.
        self.assertIs(type(changes['entity_category']), EntityCategory)
        self.assertNotIn('device_id', changes)  # The device did not change.
        self.assertIs(self.live('plug1:power')._attr_entity_category, EntityCategory.DIAGNOSTIC)

    async def test_sync_clears_a_device_field_set_to_null(self):
        await self.sync(definition(device=device(swVersion='1.2', configurationUrl='http://plug')))
        plug = self.devices.async_get_device(identifiers={(DOMAIN, 'plug1')})
        self.assertEqual((plug.sw_version, plug.configuration_url), ('1.2', 'http://plug'))
        await self.sync(definition(device=device(model=None)))
        self.assertEqual((plug.model, plug.sw_version, plug.configuration_url), (None, None, None))
        self.assertEqual(plug.manufacturer, 'Shelly')
        cleared = [c[2] for c in self.devices.calls if c[0] == 'update']
        self.assertEqual(cleared, [{'model': None, 'sw_version': None, 'configuration_url': None}])
        # Nothing to clear on the next sync: no write.
        await self.sync(definition(device=device(model=None)))
        self.assertEqual(len([c for c in self.devices.calls if c[0] == 'update']), 1)

    async def test_sync_names_main_and_standalone_entities(self):
        await self.sync(definition(description('main', name=None), description('relay')))
        await self.sync(definition(description('any', 'switch', None, definition='motion'),
                                   description('named', 'switch', 'Any motion', definition='motion'),
                                   definition_id='motion', name='Motion', device=None))
        self.assertIsNone(self.live('plug1:main')._attr_name)  # The device's main entity.
        self.assertEqual(self.live('plug1:relay')._attr_name, 'Relay')
        self.assertEqual(self.live('motion:any')._attr_name, 'Motion')  # Standalone: the definition's name.
        self.assertEqual(self.live('motion:named')._attr_name, 'Any motion')
        self.assertIsNone(self.live('motion:any').device_info)
        # A name that goes back to null follows the same rule on a live entity.
        await self.sync(definition(description('relay', name=None)))
        self.assertIsNone(self.live('plug1:relay')._attr_name)

    async def test_sync_treats_a_disabled_registry_entry_as_not_live(self):
        self.entities.add('switch', 'plug1:relay', entity_id='switch.my_relay', disabled_by='user')
        result = await self.sync(definition())
        self.assertEqual(result, {'entities': {'plug1:relay': {'entityId': 'switch.my_relay'}}})
        attempt = self.platforms['switch'].added[-1]
        await self.push(snapshot())
        self.assertEqual(attempt.written, [])  # Not live: the push is kept, not written.
        # Still not live, so the next sync tries to add it again rather than updating it.
        await self.sync(definition())
        self.assertEqual(len(self.platforms['switch'].added), 2)

    async def test_disabled_entity_takes_pushes_and_resyncs_quietly(self):
        request = definition(description('relay', enabledByDefault=False), description('power', 'sensor', 'Power'))
        result = await self.sync(request)
        self.assertEqual(self.entities.async_get('switch.plug1_relay').disabled_by, 'integration')
        attempt = self.platforms['switch'].added[-1]
        self.assertIsNotNone(attempt.entity_id)  # Home Assistant sets it before it skips a disabled entry.
        await self.push(snapshot(), snapshot('plug1:power', values={'value': 5}))
        self.assertEqual(attempt.written, [])
        self.logger.exception.assert_not_called()
        power = self.live('plug1:power')
        self.assertEqual(len(power.written), 2)  # The add, then the push.
        # The next sync passes the disabled entity and still updates the one after it.
        self.assertEqual(await self.sync(request), result)
        self.assertEqual(result['entities']['plug1:relay'], {'entityId': 'switch.plug1_relay'})
        self.assertEqual(len(power.written), 3)
        self.assertEqual(attempt.written, [])
        self.logger.exception.assert_not_called()

    async def test_sync_answers_error_when_an_add_fails(self):
        self.platforms['switch'].refuse.add('plug1:bad')
        answers = await self.answers(self.bridge.begin_sync, definition(description('bad'), description('good')))
        self.assertEqual(len(answers), 1)
        self.assertEqual(answers[0][0], 'error')
        self.assertIn('plug1:bad', answers[0][1])
        self.assertNotIn('plug1:good', answers[0][1])
        self.live('plug1:good')  # The others are still added.

    async def test_sync_moves_an_entity_onto_a_new_device(self):
        await self.sync(definition(description('relay', name=None), device=None))
        relay = self.live('plug1:relay')
        self.assertIsNone(self.entities.async_get('switch.plug1_relay').device_id)
        self.assertEqual(relay._attr_name, 'Plug one')
        await self.sync(definition(description('relay', name=None)))
        plug = self.devices.async_get_device(identifiers={(DOMAIN, 'plug1')})
        self.assertEqual(self.registry_updates('switch.plug1_relay')[-1]['device_id'], plug.id)
        self.assertEqual(self.entities.async_get('switch.plug1_relay').device_id, plug.id)
        self.assertIs(self.live('plug1:relay'), relay)  # Moved, not added again (spike rule 8).
        self.assertIsNone(relay._attr_name)

    async def test_sync_removes_entities_no_longer_described(self):
        await self.sync(definition(description('relay'), description('power', 'sensor', 'Power')))
        self.entities.add('switch', 'plug1:old')  # Left from an earlier version, not live.
        self.entities.add('switch', 'plug10:relay')  # Another definition whose id starts the same.
        self.entities.add('switch', 'plug1:other', config_entry_id='entry2')  # Another config entry's.
        power = self.live('plug1:power')
        await self.sync(definition(description('relay')))
        self.assertEqual(sorted(e.unique_id for e in self.entities.entities.values()),
                         ['plug10:relay', 'plug1:other', 'plug1:relay'])
        written = len(power.written)
        await self.push(snapshot('plug1:power', values={'value': 3}))
        self.assertEqual(len(power.written), written)  # No longer live.

    async def test_sync_without_device_removes_the_old_device(self):
        await self.sync(definition(description('relay', name=None)))
        relay = self.live('plug1:relay')
        await self.sync(definition(description('relay', name=None), device=None))
        self.assertIsNone(self.devices.async_get_device(identifiers={(DOMAIN, 'plug1')}))
        entry = self.entities.async_get('switch.plug1_relay')
        self.assertIsNotNone(entry)  # Moved off before the device went, so it was not removed with it.
        self.assertIsNone(entry.device_id)
        self.assertIs(self.live('plug1:relay'), relay)
        self.assertEqual(relay.written[-1]['name'], 'Plug one')

    async def test_sync_result_maps_unique_ids_to_entity_ids(self):
        self.entities.add('switch', 'plug1:relay', entity_id='switch.kitchen_plug')
        result = await self.sync(PLUG)
        self.assertEqual(result, {'entities': {'plug1:relay': {'entityId': 'switch.kitchen_plug'},
                                               'plug1:power': {'entityId': 'sensor.plug1_power'}}})
        # A live entity's entity id, on the next sync.
        self.assertEqual(await self.sync(PLUG), result)

    async def test_sync_for_a_platform_not_attached_answers_error(self):
        answers = await self.answers(self.bridge.begin_sync,
                                     definition(description('relay'), description('motion', 'binary_sensor', 'Motion')))
        self.assertEqual(len(answers), 1)
        self.assertEqual(answers[0][0], 'error')
        self.assertIn('binary_sensor', answers[0][1])
        # Nothing was changed.
        self.assertEqual((self.entities.entities, self.devices.devices, self.platforms['switch'].added), ({}, {}, []))

    async def test_sync_resolves_the_via_device(self):
        self.devices.add('hub', name='Hub')
        other = self.devices.add('zigbee', config_entry_id='entry2')
        other.identifiers = {('zha', 'coordinator'), ('zha', 'b')}
        await self.sync(definition(device=device(via={'definition': 'hub'})))
        plug = self.devices.async_get_device(identifiers={(DOMAIN, 'plug1')})
        self.assertEqual(plug.via_device_id, 'device-hub')
        await self.sync(definition(device=device(via={'deviceId': other.id})))
        self.assertEqual(plug.via_device_id, other.id)
        # A parent that does not exist is left out: the device no longer has one.
        await self.sync(definition(device=device(via={'definition': 'gone'})))
        self.assertNotIn('via_device', [c[1] for c in self.devices.calls if c[0] == 'get_or_create'][-1])
        self.assertIsNone(plug.via_device_id)

    # Push

    async def test_push_applies_state_and_availability(self):
        await self.sync(definition())
        relay = self.live('plug1:relay')
        await self.push(snapshot(available=True, values={'is_on': True}, attributes={'power_on_count': 3}))
        self.assertEqual({k: relay.written[-1][k] for k in ('available', 'is_on', 'attributes')},
                         {'available': True, 'is_on': True, 'attributes': {'power_on_count': 3}})
        await self.push(snapshot(available=False, values={'is_on': False}))
        self.assertEqual({k: relay.written[-1][k] for k in ('available', 'is_on', 'attributes')},
                         {'available': False, 'is_on': False, 'attributes': {}})

    async def test_push_before_add_is_applied_on_add(self):
        await self.push(snapshot(values={'is_on': False}), snapshot(values={'is_on': True}))
        await self.sync(definition())
        relay = self.live('plug1:relay')
        # The first state written, by the add itself, is the latest pushed.
        self.assertEqual(len(relay.written), 1)
        self.assertEqual({k: relay.written[0][k] for k in ('available', 'is_on')}, {'available': True, 'is_on': True})

    async def test_push_during_add_is_applied_after_it(self):
        self.platforms['switch'].gate = asyncio.Event()
        answers = asyncio.ensure_future(self.answers(self.bridge.begin_sync, definition()))
        await asyncio.wait_for(self.platforms['switch'].entered.wait(), 2)
        await self.push(snapshot(values={'is_on': True}))
        self.platforms['switch'].gate.set()
        await answers
        self.assertTrue(self.live('plug1:relay').written[-1]['is_on'])

    async def test_push_for_unknown_entity_after_close_is_dropped(self):
        await self.sync(definition())
        relay = self.live('plug1:relay')
        await self.push(snapshot('plug1:later'))  # Kept for an entity not live yet.
        self.bridge.close()
        written = len(relay.written)
        await self.push(snapshot('plug1:unknown'), snapshot('plug1:relay'))
        self.assertEqual(len(relay.written), written)
        self.assertEqual(self.bridge._pending, {})

    # Remove and prune

    async def test_remove_purges_registry_and_device(self):
        await self.sync(definition(description('relay'), description('power', 'sensor', 'Power')))
        await self.sync(definition(definition_id='plug2'))
        self.entities.add('switch', 'plug1:disabled', disabled_by='user')
        relay = self.live('plug1:relay')
        await self.push(snapshot('plug1:disabled'))
        answers = await self.answers(self.bridge.begin_remove, 'plug1')
        self.assertEqual(answers, [('success',)])
        self.assertEqual([e.unique_id for e in self.entities.entities.values()], ['plug2:relay'])
        self.assertEqual([d.id for d in self.devices.devices.values()], ['device-plug2'])
        self.assertIn(('remove', 'device-plug1'), self.devices.calls)
        self.assertEqual(self.bridge._pending, {})
        written = len(relay.written)
        await self.push(snapshot('plug1:relay'))
        self.assertEqual(len(relay.written), written)

    async def test_prune_keeps_known_definitions_and_purges_the_rest(self):
        for definition_id in ('plug1', 'plug2', 'plug3'):
            await self.sync(definition(definition_id=definition_id))
        self.entities.add('switch', 'gone:relay')  # An entry whose definition has no device.
        self.devices.add('empty')  # A device whose definition has no entities.
        await self.push(snapshot('gone:relay'), snapshot('plug1:later'))
        answers = await self.answers(self.bridge.begin_prune, json.dumps(['plug1', 'plug3']))
        self.assertEqual(answers, [('success',)])
        self.assertEqual(sorted(e.unique_id for e in self.entities.entities.values()), ['plug1:relay', 'plug3:relay'])
        self.assertEqual(sorted(self.devices.devices), ['device-plug1', 'device-plug3'])
        self.assertEqual(list(self.bridge._pending), ['plug1:later'])

    async def test_prune_refuses_a_malformed_list(self):
        await self.sync(definition())
        await self.sync(definition(definition_id='plug2'))
        # A string would otherwise be a set of characters and an object a set of keys: either purges everything.
        for request in ('"plug1"', '{"plug1": true}', '["plug1", 2]', 'null'):
            answers = await self.answers(self.bridge.begin_prune, request)
            self.assertEqual([a[0] for a in answers], ['error'], request)
        self.assertEqual(sorted(e.unique_id for e in self.entities.entities.values()), ['plug1:relay', 'plug2:relay'])
        self.assertEqual(sorted(self.devices.devices), ['device-plug1', 'device-plug2'])
        self.assertFalse([c for c in self.entities.calls if c[0] == 'remove'])

    async def test_prune_ignores_entries_of_other_config_entries(self):
        self.entities.add('switch', 'other:relay', config_entry_id='entry2')
        self.devices.add('other', config_entry_id='entry2')
        self.entities.add('sensor', 'legacy_sensor')  # Not a definition's: no colon.
        answers = await self.answers(self.bridge.begin_prune, '[]')
        self.assertEqual(answers, [('success',)])
        self.assertEqual(sorted(e.unique_id for e in self.entities.entities.values()), ['legacy_sensor', 'other:relay'])
        self.assertEqual(list(self.devices.devices), ['device-other'])

    # Threads, order and answers

    async def test_calls_from_another_thread_run_on_the_loop(self):
        loop_thread = threading.get_ident()
        answered, done = [], asyncio.Event()

        def answer(*args):
            answered.append(threading.get_ident())
            self.loop.call_soon_threadsafe(done.set)

        def from_dotnet():
            self.bridge.begin_sync(definition(), answer, answer)
            self.bridge.push(json.dumps([snapshot()]))
        thread = threading.Thread(target=from_dotnet)
        thread.start()
        await asyncio.wait_for(done.wait(), 2)
        thread.join()
        await self.settle()
        relay = self.live('plug1:relay')
        self.assertEqual(answered, [loop_thread])
        self.assertTrue(self.devices.calls)
        self.assertEqual({c[2] for c in self.devices.calls if c[0] == 'get_or_create'}, {loop_thread})
        self.assertEqual({w['thread'] for w in relay.written}, {loop_thread})
        self.assertTrue(relay.written[-1]['available'])

    async def test_operations_run_in_the_order_requested(self):
        self.platforms['switch'].gate = asyncio.Event()
        order = []
        self.bridge.begin_sync(definition(), lambda result: order.append('sync'), order.append)
        self.bridge.begin_remove('plug1', lambda: order.append('remove'), order.append)
        await asyncio.wait_for(self.platforms['switch'].entered.wait(), 2)
        await self.settle()
        self.assertEqual(order, [])  # The remove waits for the sync's add.
        self.assertEqual(self.entities.calls, [])
        self.platforms['switch'].gate.set()
        await self.settle()
        self.assertEqual(order, ['sync', 'remove'])
        self.assertEqual(self.entities.entities, {})
        # A remove that finishes without waiting does not stop the next operation from starting.
        await self.sync(definition())

    async def test_callbacks_fire_exactly_once(self):
        # A body that raises answers error with its text, once, and logs it.
        answers = await self.answers(self.bridge.begin_sync, 'not json')
        self.assertEqual([a[0] for a in answers], ['error'])
        self.logger.exception.assert_called()
        # A callback that raises does not reach the loop or bring a second answer, and the operation queued behind
        # it still runs; so does one refused after close.
        calls = []

        def success(result):
            calls.append('success')
            raise RuntimeError('.NET delegate failed')
        self.bridge.begin_sync(definition(), success, calls.append)
        self.assertEqual(await self.answers(self.bridge.begin_remove, 'plug1'), [('success',)])
        self.assertEqual(calls, ['success'])
        # Closed while running and while queued: error("closed"), once, even when the running one finishes later.
        self.platforms['switch'].gate = asyncio.Event()
        self.platforms['switch'].entered.clear()
        running, queued = [], []
        self.bridge.begin_sync(definition(description('relay'), description('second')),
                               lambda r: running.append(('success', r)), lambda m: running.append(('error', m)))
        self.bridge.begin_prune('[]', lambda: queued.append(('success',)), lambda m: queued.append(('error', m)))
        await asyncio.wait_for(self.platforms['switch'].entered.wait(), 2)
        self.bridge.close()
        self.logger.reset_mock()
        self.platforms['switch'].gate.set()
        await self.settle()
        self.assertEqual((running, queued), ([('error', 'closed')], [('error', 'closed')]))
        # The rest of the sync is skipped quietly: nothing more is added to the platform being unloaded.
        self.assertNotIn('plug1:second', [e.unique_id for e in self.platforms['switch'].added])
        self.logger.exception.assert_not_called()
        self.logger.error.assert_not_called()

    async def test_cancelled_operations_answer_error_once(self):
        """Home Assistant cancels the bridge's task when it stops: the running and queued operations answer."""
        self.platforms['switch'].gate = asyncio.Event()
        running, queued = [], []
        self.bridge.begin_sync(definition(), running.append, running.append)
        self.bridge.begin_prune('[]', lambda: queued.append('done'), queued.append)
        await asyncio.wait_for(self.platforms['switch'].entered.wait(), 2)
        self.bridge._runner.cancel()
        await self.settle()
        self.assertEqual((running, queued), (['cancelled'], ['cancelled']))
        # Closed first, then cancelled: the close's answer is the only one.
        self.platforms['switch'].entered.clear()
        answers = []
        self.bridge.begin_sync(definition(), answers.append, answers.append)
        await asyncio.wait_for(self.platforms['switch'].entered.wait(), 2)
        self.bridge.close()
        self.bridge._runner.cancel()
        await self.settle()
        self.assertEqual(answers, ['closed'])

    async def test_close_answers_error_and_drops_pushes(self):
        await self.sync(definition())
        relay = self.live('plug1:relay')
        self.bridge.close()
        self.assertEqual(await self.answers(self.bridge.begin_sync, definition()), [('error', 'closed')])
        self.assertEqual(await self.answers(self.bridge.begin_remove, 'plug1'), [('error', 'closed')])
        self.assertEqual(await self.answers(self.bridge.begin_prune, '[]'), [('error', 'closed')])
        failing = Mock(side_effect=RuntimeError('.NET delegate failed'))
        self.bridge.begin_remove('plug1', Mock(), failing)
        await self.settle()
        failing.assert_called_once_with('closed')  # And nothing reached the loop (tearDown).
        written = len(relay.written)
        await self.push(snapshot(values={'is_on': False}))
        self.assertEqual(len(relay.written), written)
        # Unload never purges (spike rule 10).
        self.assertEqual(list(self.entities.entities), ['switch.plug1_relay'])
        self.assertEqual(len(self.devices.devices), 1)
        # A platform set up after close is not used.
        self.bridge.attach_platform('switch', self.platforms['switch'], Mock())
        self.assertEqual(await self.answers(self.bridge.begin_sync, definition()), [('error', 'closed')])

    async def test_calls_after_the_loop_closed_answer_without_raising(self):
        loop = asyncio.new_event_loop()
        loop.close()
        self.bridge.hass = NS(loop=loop)
        answers = []
        self.bridge.begin_sync(definition(), answers.append, answers.append)
        self.bridge.push(json.dumps([snapshot()]))
        self.assertEqual(answers, ['closed'])

    # Commands

    async def test_command_failure_raises_home_assistant_error(self):
        await self.sync(definition())
        await self.live('plug1:relay').command('turn_on')
        self.assertEqual(self.dotnet_calls, [(self.manager.EntityCommand, ('plug1:relay', 'turn_on', '{}'))])
        await self.bridge.async_command('plug1:relay', 'set', {'value': 2})
        self.assertEqual(self.dotnet_calls[-1][1], ('plug1:relay', 'set', '{"value": 2}'))
        self.manager.EntityCommand.side_effect = DotNetError('The entity is not running')
        with self.assertRaises(HomeAssistantError) as raised:
            await self.live('plug1:relay').command('turn_off')
        # The message only: not the .NET type or stack (spike rule 12).
        self.assertEqual(str(raised.exception), 'The entity is not running')


if __name__ == '__main__':
    unittest.main()
