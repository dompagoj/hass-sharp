"""The entity bridge and entity base against fake registries, a fake entity platform and a real event loop.

The fakes behave as the spike findings (docs/superpowers/plans/2026-11-17-custom-entities-phase-1-spike.md) say
Home Assistant 2026.11 does: writing a state before the entity is added raises, a disabled registry entry is not
added, removing a device removes the entities on it, a via device that does not exist is refused.
"""
import ast
import asyncio
import enum
import itertools
import json
import sys
import threading
import unittest
from pathlib import Path
from types import SimpleNamespace as NS
from unittest.mock import Mock, patch

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

    @property
    def unit_of_measurement(self):
        return getattr(self, '_attr_unit_of_measurement', None)

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
        # device id -> the entity ids that were still on it when it was removed, and so went with it.
        self.removed_with = {}

    def add(self, definition_id, config_entry_id=ENTRY, **fields):
        device = NS(id=f'device-{definition_id}', identifiers={(DOMAIN, definition_id)}, config_entry_id=config_entry_id,
                    **{field: None for field in self.FIELDS})
        for key, value in fields.items():
            setattr(device, key, value)
        self.devices[device.id] = device
        return device

    def async_get(self, device_id):
        return self.devices.get(device_id)

    # Home Assistant 2026.11 deprecates async_get_device (identifiers are unique per config entry only) and
    # `via_device`, and refuses a `via_device_id` that is not a registered device; this fake has neither.
    def async_get_device_by_identifier(self, identifier, config_entry_id):
        return next((d for d in self.devices.values()
                     if identifier in d.identifiers and d.config_entry_id == config_entry_id), None)

    def async_get_or_create(self, *, config_entry_id, identifiers, **fields):
        self.calls.append(('get_or_create', dict(fields), threading.get_ident()))
        if 'via_device' in fields:
            raise AssertionError('via_device is deprecated: pass via_device_id')
        if fields.get('via_device_id') is not None and fields['via_device_id'] not in self.devices:
            raise ValueError(f"via_device_id {fields['via_device_id']} is not a registered device id")
        device = self.async_get_device_by_identifier(next(iter(identifiers)), config_entry_id)
        if device is None:
            device = self.add(next(iter(identifiers))[1], config_entry_id)
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
        # The entities on it go too, live ones included (spike rule 9), and a device connected through it has no parent.
        on_it = [e for e in self._entities.entities.values() if e.device_id == device_id]
        self.removed_with[device_id] = [e.entity_id for e in on_it]
        for entry in on_it:
            del self._entities.entities[entry.entity_id]
        for other in self.devices.values():
            if other.via_device_id == device_id:
                other.via_device_id = None


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


# What the platform modules import from Home Assistant, as far as they use it: the base classes by name only, the
# enums with the members they take.
class NumberMode(enum.StrEnum):
    AUTO = 'auto'
    BOX = 'box'
    SLIDER = 'slider'


class TextMode(enum.StrEnum):
    TEXT = 'text'
    PASSWORD = 'password'


class ColorMode(enum.StrEnum):
    ONOFF = 'onoff'
    BRIGHTNESS = 'brightness'


class CoverEntityFeature(enum.IntFlag):
    OPEN = 1
    CLOSE = 2
    SET_POSITION = 4
    STOP = 8


class Camera(Entity):
    """As Home Assistant's Camera: its initialiser must run (it sets the content type and the access token of the
    picture's URL), and its frames come every 0.5 s unless the entity says otherwise."""
    _attr_frame_interval = 0.5

    def __init__(self):
        super().__init__()
        self.content_type = 'image/jpeg'
        self.access_tokens = ['token']


PLATFORM_BASES = {**{name: type(name, (Entity,), {}) for name in (
    'BinarySensorEntity', 'SensorEntity', 'SwitchEntity', 'ButtonEntity', 'NumberEntity', 'SelectEntity', 'TextEntity',
    'LightEntity', 'CoverEntity')}, 'Camera': Camera}

JPEG = b'\xff\xd8\xff\xe0 a frame \xff\xd9'


class DotNetBytes(bytearray):
    """A System.Byte[] as Python.NET hands it over: a buffer of a fixed length that Python fills through a memoryview."""


# The part of .NET's System the bridge uses to build a picture's bytes (spike rule 16).
SYSTEM = NS(Byte='System.Byte', Array=NS(CreateInstance=lambda type_, length: DotNetBytes(length)
                                         if type_ == 'System.Byte' else TypeError(type_)))


def platform_class(domain):
    """The entity class of a platform module, loaded over the entity base and the fakes above."""
    base = load('entity_base', {'Entity': Entity, 'EntityCategory': EntityCategory})['HassSharpEntity']
    env = load(domain, {'HassSharpEntity': base, 'entity_platform': NS(), **PLATFORM_BASES, 'NumberMode': NumberMode,
                        'TextMode': TextMode, 'ColorMode': ColorMode, 'CoverEntityFeature': CoverEntityFeature,
                        'ATTR_BRIGHTNESS': 'brightness', 'ATTR_POSITION': 'position'})
    classes = [v for v in env.values() if isinstance(v, type) and issubclass(v, base) and v is not base]
    assert len(classes) == 1, classes
    return classes[0]


def device(name='Plug one', key='main', **fields):
    """A device of a sync request, keyed `main` as a class's (or a graph's definition-level) device is."""
    return {'key': key, 'name': name, 'manufacturer': 'Shelly', 'model': 'Plug S', 'swVersion': None, 'hwVersion': None,
            'configurationUrl': None, 'suggestedArea': None, 'via': None, **fields}


# The commands .NET describes for each domain (the catalog's entityDomains).
COMMANDS = {'switch': ['turn_on', 'turn_off'], 'button': ['press'], 'number': ['set_value'],
            'select': ['select_option'], 'text': ['set_value'], 'light': ['turn_on', 'turn_off'],
            'cover': ['open', 'close', 'stop', 'set_position']}


def description(key, domain='switch', name='Relay', definition='plug1', device='main', **fields):
    """An entity of a sync request, on the device of the key `device` (None: standalone)."""
    return {'uniqueId': f'{definition}:{key}', 'key': key, 'domain': domain, 'name': name, 'icon': None,
            'deviceClass': None, 'entityCategory': None, 'enabledByDefault': True,
            'commands': COMMANDS.get(domain, []), 'options': {}, 'device': device, **fields}


def definition(*entities, definition_id='plug1', name='Plug one', devices=None):
    """A sync request; its devices are the one device `main` unless given."""
    return json.dumps({'definitionId': definition_id, 'name': name, 'devices': [device()] if devices is None else devices,
                       'entities': list(entities) or [description('relay', definition=definition_id)]})


# The request .NET sends for a plug (tests/Runtime/EntityHostTests.cs pins this text).
PLUG = """{"definitionId":"plug1","name":"Plug one","devices":[{"key":"main","name":"Plug one","manufacturer":"Shelly","model":"Plug S","swVersion":null,"hwVersion":null,"configurationUrl":null,"suggestedArea":null,"via":null}],"entities":[{"uniqueId":"plug1:relay","key":"relay","domain":"switch","name":"Relay","icon":null,"deviceClass":null,"entityCategory":null,"enabledByDefault":true,"commands":["turn_on","turn_off"],"options":{},"device":"main"},{"uniqueId":"plug1:power","key":"power","domain":"sensor","name":"Power","icon":null,"deviceClass":"power","entityCategory":null,"enabledByDefault":true,"commands":[],"options":{"unit":"W","stateClass":"measurement","options":null},"device":"main"}]}"""


def identified(devices, identifier):
    """The device of this config entry with the identifier (DOMAIN, identifier), or None."""
    return devices.async_get_device_by_identifier((DOMAIN, identifier), ENTRY)


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
        self.background = []  # the tasks begun with async_create_background_task

        def background(coro, name, eager_start=True):
            self.background.append(asyncio.Task(coro, loop=loop, name=name, eager_start=eager_start))
            return self.background[-1]
        self.hass = NS(loop=loop, data={}, async_create_task=lambda coro, name=None, eager_start=True:
                       asyncio.Task(coro, loop=loop, name=name, eager_start=eager_start),
                       async_create_background_task=background)
        base = load('entity_base', {'Entity': Entity, 'EntityCategory': EntityCategory})['HassSharpEntity']

        class Switch(base):
            def apply_values(self, values):
                self._attr_is_on = values.get('is_on')

        class Sensor(base):
            # As SensorEntity: it shows the unit Home Assistant stored for it when there is one, else its own.
            stored_unit = None

            def apply(self, description, device, definition_name):
                super().apply(description, device, definition_name)
                self._attr_native_unit_of_measurement = (description.get('options') or {}).get('unit')

            @property
            def unit_of_measurement(self):
                return self.stored_unit or self._attr_native_unit_of_measurement

            def apply_values(self, values):
                self._attr_native_value = values.get('value')

        self.manager = NS(EntityCommand=Mock(return_value='task'), CameraImage=Mock(return_value=DotNetBytes(JPEG)))
        self.dotnet_calls = []
        # Home Assistant's cameras, by entity id: the picture each gives, or the error it raises.
        self.cameras, self.camera_calls = {}, []

        async def get_image(hass, entity_id, **kwargs):
            self.camera_calls.append((entity_id, kwargs, threading.get_ident()))
            assert hass is self.hass
            found = self.cameras.get(entity_id, HomeAssistantError('Camera not found'))
            if isinstance(found, BaseException):
                raise found
            if isinstance(found, asyncio.Event):  # A camera that does not answer until the event is set.
                await found.wait()
                found = NS(content_type='image/jpeg', content=JPEG)
            return found

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
        # The bridge imports Home Assistant's camera component when it first needs it.
        env['_camera'] = lambda: NS(async_get_image=get_image)
        system = patch.dict(sys.modules, {'System': SYSTEM})
        system.start()
        self.addCleanup(system.stop)
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
        self.assertEqual(relay.device_info['identifiers'], {(DOMAIN, 'plug1:main')})
        plug = identified(self.devices, 'plug1:main')
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
        # The unit is the one the entity shows, as Home Assistant writes it on an add: a sensor re-added with another
        # unit of its device class keeps showing the unit stored for it (spike rule 7).
        self.live('plug1:power').stored_unit = 'W'
        await self.sync(definition(description('power', 'sensor', 'Power', options={'unit': 'MW'})))
        self.assertEqual('W', self.registry_updates('sensor.plug1_power')[-1]['unit_of_measurement'])

    async def test_sync_clears_a_device_field_set_to_null(self):
        await self.sync(definition(devices=[device(swVersion='1.2', configurationUrl='http://plug')]))
        plug = identified(self.devices, 'plug1:main')
        self.assertEqual((plug.sw_version, plug.configuration_url), ('1.2', 'http://plug'))
        await self.sync(definition(devices=[device(model=None)]))
        self.assertEqual((plug.model, plug.sw_version, plug.configuration_url), (None, None, None))
        self.assertEqual(plug.manufacturer, 'Shelly')
        cleared = [c[2] for c in self.devices.calls if c[0] == 'update']
        self.assertEqual(cleared, [{'model': None, 'sw_version': None, 'configuration_url': None}])
        # Nothing to clear on the next sync: no write.
        await self.sync(definition(devices=[device(model=None)]))
        self.assertEqual(len([c for c in self.devices.calls if c[0] == 'update']), 1)

    async def test_sync_names_main_and_standalone_entities(self):
        await self.sync(definition(description('main', name=None), description('relay')))
        await self.sync(definition(description('any', 'switch', None, definition='motion', device=None),
                                   description('named', 'switch', 'Any motion', definition='motion', device=None),
                                   definition_id='motion', name='Motion', devices=[]))
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
        await self.sync(definition(description('relay', name=None, device=None), devices=[]))
        relay = self.live('plug1:relay')
        self.assertIsNone(self.entities.async_get('switch.plug1_relay').device_id)
        self.assertEqual(relay._attr_name, 'Plug one')
        await self.sync(definition(description('relay', name=None)))
        plug = identified(self.devices, 'plug1:main')
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

    async def test_sync_that_fails_keeps_the_entities_it_dropped(self):
        await self.sync(definition(description('relay'), description('power', 'sensor', 'Power')))
        power = self.live('plug1:power')
        self.platforms['switch'].refuse.add('plug1:bad')
        # The new revision drops power and adds an entity Home Assistant refuses: the old revision keeps running.
        answers = await self.answers(self.bridge.begin_sync, definition(description('relay'), description('bad')))
        self.assertEqual([a[0] for a in answers], ['error'], answers)
        self.assertIsNotNone(self.entities.async_get('sensor.plug1_power'))
        self.assertNotIn('remove', [c[0] for c in self.entities.calls])
        written = len(power.written)
        await self.push(snapshot('plug1:power', values={'value': 3}))
        self.assertEqual(len(power.written), written + 1)  # Still live.
        # A device step that fails leaves them too.
        self.devices.async_get_or_create = Mock(side_effect=ValueError("invalid configuration_url 'not a url'"))
        answers = await self.answers(self.bridge.begin_sync, definition(description('relay')))
        self.assertEqual([a[0] for a in answers], ['error'], answers)
        self.assertIsNotNone(self.entities.async_get('sensor.plug1_power'))
        del self.devices.async_get_or_create
        # Once a sync succeeds, they go.
        await self.sync(definition(description('relay')))
        self.assertIsNone(self.entities.async_get('sensor.plug1_power'))
        written = len(power.written)
        await self.push(snapshot('plug1:power', values={'value': 4}))
        self.assertEqual(len(power.written), written)

    async def test_sync_of_a_key_whose_domain_changes_adds_it_anew(self):
        await self.sync(definition(description('main', 'switch', 'Main')))
        switch = self.live('plug1:main')
        result = await self.sync(definition(description('main', 'sensor', 'Main')))
        self.assertEqual(result, {'entities': {'plug1:main': {'entityId': 'sensor.plug1_main'}}})
        self.assertIsNone(self.entities.async_get('switch.plug1_main'))
        sensor = next(e for e in self.platforms['sensor'].added if e.unique_id == 'plug1:main')
        self.assertIsNot(sensor, switch)
        await self.push(snapshot('plug1:main', values={'value': 7}))
        self.assertEqual(len(sensor.written), 2)  # The add, then the push.

    async def test_sync_without_its_device_leaves_the_entities_standalone(self):
        await self.sync(definition(description('relay', name=None), description('power', 'sensor', 'Power'),
                                   description('spare', name='Spare', enabledByDefault=False)))
        relay = self.live('plug1:relay')
        plug = identified(self.devices, 'plug1:main')
        entries = {e.entity_id: e for e in self.entities.entities.values()}
        self.assertEqual({e.device_id for e in entries.values()}, {plug.id})
        self.assertEqual(entries['switch.plug1_spare'].disabled_by, 'integration')
        await self.sync(definition(description('relay', name=None, device=None),
                                   description('power', 'sensor', 'Power', device=None),
                                   description('spare', name='Spare', enabledByDefault=False, device=None), devices=[]))
        self.assertIsNone(identified(self.devices, 'plug1:main'))
        self.assertEqual(self.devices.devices, {})
        # Moved off before the device went, so none was removed with it: the same entries, now on no device.
        self.assertEqual({e.entity_id: e for e in self.entities.entities.values()}, entries)
        for entry in entries.values():
            self.assertIsNone(entry.device_id, entry.entity_id)
        self.assertEqual(entries['switch.plug1_spare'].disabled_by, 'integration')
        self.assertIs(self.live('plug1:relay'), relay)
        self.assertEqual(relay.written[-1]['name'], 'Plug one')
        # The device went only after its entries were off it.
        self.assertEqual(self.devices.removed_with, {plug.id: []})
        self.assertEqual([c for c in self.entities.calls if c[0] == 'remove'], [])

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

    async def test_sync_refuses_a_text_pattern_python_cannot_read(self):
        # Valid in .NET, but Home Assistant matches the text with Python's re, which has no \p{...}: every state write
        # would fail with a traceback, so the definition is refused instead.
        self.platforms['text'] = Platform('text', self.hass, self.entities, self.devices)
        self.bridge.attach_platform('text', self.platforms['text'], platform_class('text'))
        options = {'min': 0, 'max': 10, 'pattern': r'^\p{Lu}+$', 'mode': 'text'}
        code = description('code', 'text', 'Code', options=options)
        answers = await self.answers(self.bridge.begin_sync, definition(description('relay'), code))
        self.assertEqual(answers, [('error', r'Code (code) has the pattern ^\p{Lu}+$, which Home Assistant cannot use: '
                                             r'bad escape \p at position 1')])
        # Refused before anything changed, and logged without a traceback.
        self.assertEqual((self.entities.entities, self.devices.devices), ({}, {}))
        self.assertEqual((self.platforms['switch'].added, self.platforms['text'].added), ([], []))
        self.logger.exception.assert_not_called()
        # A pattern Python reads is taken, and so is a text without one.
        await self.sync(definition(description('code', 'text', 'Code', options={**options, 'pattern': '^[A-Z]+$'}),
                                   description('note', 'text', None, options={**options, 'pattern': None})))
        self.assertEqual(self.live('plug1:code')._attr_pattern, '^[A-Z]+$')
        self.assertIsNone(self.live('plug1:note')._attr_pattern)

    async def test_sync_resolves_the_via_device(self):
        # Another definition's device is its device `main`; the one an earlier build made for it is not that.
        self.devices.add('hub:main', name='Hub')
        self.devices.add('hub', name='Old hub')
        other = self.devices.add('zigbee', config_entry_id='entry2')
        other.identifiers = {('zha', 'coordinator'), ('zha', 'b')}
        await self.sync(definition(devices=[device(via={'definition': 'hub'})]))
        plug = identified(self.devices, 'plug1:main')
        self.assertEqual(plug.via_device_id, 'device-hub:main')
        await self.sync(definition(devices=[device(via={'deviceId': other.id})]))
        self.assertEqual(plug.via_device_id, other.id)
        # A parent that does not exist is left out: the device no longer has one.
        await self.sync(definition(devices=[device(via={'definition': 'gone'})]))
        self.assertNotIn('via_device_id', [c[1] for c in self.devices.calls if c[0] == 'get_or_create'][-1])
        self.assertIsNone(plug.via_device_id)

    # Several devices

    @staticmethod
    def two_devices(relay='plug', hub=None):
        """The plug definition with the devices plug (Hall plug) and hub (Hub): relay on `relay`, power on plug, online on
        hub and bare on none; plug is part of `hub` when given."""
        return definition(description('relay', device=relay), description('power', 'sensor', 'Power', device='plug'),
                          description('online', name=None, device='hub'), description('bare', name='Bare', device=None),
                          devices=[device('Hub', 'hub', model='H1'), device('Hall plug', 'plug', via=hub)])

    async def test_sync_makes_one_device_per_described_device(self):
        result = await self.sync(self.two_devices())
        self.assertEqual(sorted(result['entities']), ['plug1:bare', 'plug1:online', 'plug1:power', 'plug1:relay'])
        plug, hub = identified(self.devices, 'plug1:plug'), identified(self.devices, 'plug1:hub')
        self.assertEqual((plug.identifiers, hub.identifiers), ({(DOMAIN, 'plug1:plug')}, {(DOMAIN, 'plug1:hub')}))
        self.assertEqual((plug.name, plug.model, hub.name, hub.model), ('Hall plug', 'Plug S', 'Hub', 'H1'))
        self.assertEqual(len(self.devices.devices), 2)  # No device for the definition itself.
        on = {e.unique_id: e.device_id for e in self.entities.entities.values()}
        self.assertEqual(on, {'plug1:relay': plug.id, 'plug1:power': plug.id, 'plug1:online': hub.id, 'plug1:bare': None})
        # Each entity has its own device's info; the standalone one none, and its own name.
        self.assertEqual(self.live('plug1:relay').device_info['identifiers'], {(DOMAIN, 'plug1:plug')})
        self.assertEqual(self.live('plug1:online').device_info['identifiers'], {(DOMAIN, 'plug1:hub')})
        self.assertIsNone(self.live('plug1:online')._attr_name)  # The device's main entity.
        self.assertIsNone(self.live('plug1:bare').device_info)
        self.assertEqual(self.live('plug1:bare')._attr_name, 'Bare')
        # The same sync again changes no entry's device.
        await self.sync(self.two_devices())
        self.assertEqual({e.unique_id: e.device_id for e in self.entities.entities.values()}, on)
        self.assertFalse([c for c in self.entities.calls if c[0] == 'update' and 'device_id' in c[2]])

    async def test_sync_moves_an_entity_to_another_device(self):
        self.entities.add('switch', 'plug1:relay', entity_id='switch.hall_relay')  # Named by the user, say.
        await self.sync(self.two_devices())
        relay, entry = self.live('plug1:relay'), self.entities.async_get('switch.hall_relay')
        plug, hub = identified(self.devices, 'plug1:plug'), identified(self.devices, 'plug1:hub')
        self.assertEqual(entry.device_id, plug.id)
        result = await self.sync(self.two_devices(relay='hub'))
        self.assertEqual(result['entities']['plug1:relay'], {'entityId': 'switch.hall_relay'})
        # The same registry entry, under the same entity id and unique id, on the other device; the entity was not added
        # again, and the device it left stays, as it is still described.
        self.assertIs(self.entities.async_get('switch.hall_relay'), entry)
        self.assertEqual((entry.unique_id, entry.device_id), ('plug1:relay', hub.id))
        self.assertIs(self.live('plug1:relay'), relay)
        self.assertEqual(len(self.platforms['switch'].added), 3)  # relay, online and bare, once each.
        self.assertIs(identified(self.devices, 'plug1:plug'), plug)
        self.assertEqual(self.entities.async_get('sensor.plug1_power').device_id, plug.id)
        # Then on no device: still the same entry.
        await self.sync(self.two_devices(relay=None))
        self.assertIs(self.entities.async_get('switch.hall_relay'), entry)
        self.assertIsNone(entry.device_id)
        self.assertNotIn('remove', [c[0] for c in self.entities.calls])

    async def test_sync_moves_a_disabled_entity_too(self):
        # The user disabled it before it was ever added, so it is never live: Home Assistant registers it with its device
        # and skips it, and no sync takes the live entity's path for it.
        entry = self.entities.add('switch', 'plug1:relay', disabled_by='user')
        await self.sync(self.two_devices())
        plug, hub = identified(self.devices, 'plug1:plug'), identified(self.devices, 'plug1:hub')
        self.assertIs(self.entities.async_get('switch.plug1_relay'), entry)
        self.assertEqual((entry.device_id, entry.disabled_by), (plug.id, 'user'))
        self.assertNotIn('plug1:relay', self.bridge._live)
        await self.sync(self.two_devices(relay='hub'))
        self.assertNotIn('plug1:relay', self.bridge._live)
        self.assertIs(self.entities.async_get('switch.plug1_relay'), entry)
        self.assertEqual((entry.device_id, entry.disabled_by), (hub.id, 'user'))
        # Moved from a device that goes, it is not removed with it.
        await self.sync(definition(description('relay', device='hub'), description('online', name=None, device='hub'),
                                   devices=[device('Hub', 'hub')]))
        self.assertIsNone(identified(self.devices, 'plug1:plug'))
        self.assertEqual(self.devices.removed_with, {plug.id: []})
        self.assertIs(self.entities.async_get('switch.plug1_relay'), entry)
        self.assertEqual((entry.device_id, entry.disabled_by), (hub.id, 'user'))
        # And to none.
        await self.sync(definition(description('relay', device=None), description('online', name=None, device='hub'),
                                   devices=[device('Hub', 'hub')]))
        self.assertIs(self.entities.async_get('switch.plug1_relay'), entry)
        self.assertEqual((entry.device_id, entry.disabled_by), (None, 'user'))

    async def test_sync_moves_an_entry_the_add_does_not_reach(self):
        # Home Assistant moves an entry it adds, disabled or not, but not one it skips before its registry step (an entity
        # id already taken, the limit of enabled entities): the bridge moves every described entry to its device.
        entry = self.entities.add('switch', 'plug1:relay', disabled_by='user')  # Never live.
        await self.sync(self.two_devices())
        plug, hub = identified(self.devices, 'plug1:plug'), identified(self.devices, 'plug1:hub')
        self.assertEqual(entry.device_id, plug.id)
        platform = self.platforms['switch']
        add = platform.async_add_entities

        async def skip_relay(new):
            await add([e for e in new if e.unique_id != 'plug1:relay'])
        platform.async_add_entities = skip_relay
        await self.sync(self.two_devices(relay='hub'))
        self.assertEqual((entry.device_id, entry.disabled_by), (hub.id, 'user'))

    async def test_sync_removes_the_device_an_earlier_build_made(self):
        old = self.devices.add('plug1', name='Plug one')
        self.entities.add('switch', 'plug1:relay', entity_id='switch.my_relay', device_id=old.id)
        self.entities.add('sensor', 'plug1:power', device_id=old.id, disabled_by='user')
        entries = {e.entity_id: e for e in self.entities.entities.values()}
        # An entry of no definition on it is moved off too, rather than removed with it.
        legacy = self.entities.add('sensor', 'legacy_sensor', device_id=old.id)
        # Another definition's devices, of an id that starts with the same text, are not this one's.
        other = self.devices.add('plug10:plug')
        older = self.devices.add('plug10')
        self.entities.add('switch', 'plug10:relay', device_id=other.id)
        await self.sync(definition(description('relay', device='plug'), description('power', 'sensor', 'Power', device='plug'),
                                   devices=[device('Hall plug', 'plug')]))
        plug = identified(self.devices, 'plug1:plug')
        self.assertIsNone(identified(self.devices, 'plug1'))
        self.assertEqual(self.devices.removed_with, {old.id: []})  # Its entries were moved off first.
        for entity_id, entry in entries.items():
            self.assertIs(self.entities.async_get(entity_id), entry)
            self.assertEqual(entry.device_id, plug.id, entity_id)
        self.assertEqual(entries['sensor.plug1_power'].disabled_by, 'user')
        self.assertIs(self.entities.async_get('sensor.legacy_sensor'), legacy)
        self.assertIsNone(legacy.device_id)
        self.assertEqual(sorted(self.devices.devices), sorted(['device-plug1:plug', 'device-plug10', 'device-plug10:plug']))
        self.assertIs(identified(self.devices, 'plug10'), older)
        self.assertEqual(self.entities.async_get('switch.plug10_relay').device_id, other.id)

    async def test_sync_removes_a_device_no_longer_described(self):
        await self.sync(self.two_devices())
        plug, hub = identified(self.devices, 'plug1:plug'), identified(self.devices, 'plug1:hub')
        # Another config entry's device of the same identifier is not this one's.
        self.devices.add('foreign', config_entry_id='entry2').identifiers = {(DOMAIN, 'plug1:plug')}
        await self.sync(definition(description('relay', device='hub'), description('power', 'sensor', 'Power', device='hub'),
                                   description('online', name=None, device='hub'), devices=[device('Hub', 'hub')]))
        self.assertIsNone(identified(self.devices, 'plug1:plug'))
        self.assertEqual(self.devices.removed_with, {plug.id: []})
        self.assertEqual(sorted(e.unique_id for e in self.entities.entities.values()),
                         ['plug1:online', 'plug1:power', 'plug1:relay'])
        self.assertEqual({e.device_id for e in self.entities.entities.values()}, {hub.id})
        self.assertEqual(sorted(self.devices.devices), sorted([hub.id, 'device-foreign']))

    async def test_sync_that_fails_removes_no_device(self):
        await self.sync(self.two_devices())
        plug = identified(self.devices, 'plug1:plug')
        old = self.devices.add('plug1', name='Plug one')  # As an earlier build left it.
        self.entities.add('switch', 'plug1:old', device_id=old.id)
        calls = len(self.devices.calls)
        self.platforms['switch'].refuse.add('plug1:bad')
        # A revision without the device plug, and with an entity Home Assistant refuses: nothing is removed.
        answers = await self.answers(self.bridge.begin_sync, definition(
            description('relay', device='hub'), description('bad', device='hub'), devices=[device('Hub', 'hub')]))
        self.assertEqual([a[0] for a in answers], ['error'], answers)
        self.assertNotIn('remove', [c[0] for c in self.devices.calls[calls:]])
        self.assertNotIn('remove', [c[0] for c in self.entities.calls])
        self.assertIs(identified(self.devices, 'plug1:plug'), plug)
        self.assertIs(identified(self.devices, 'plug1'), old)
        self.assertEqual(self.entities.async_get('switch.plug1_old').device_id, old.id)
        self.assertEqual(self.entities.async_get('sensor.plug1_power').device_id, plug.id)

    async def test_sync_links_a_device_to_another_of_the_definition(self):
        # The host lists a device after the one it is part of (tests/Runtime/EntityGraphTests.cs), and the bridge makes
        # them in that order, so the parent exists when its child names it.
        await self.sync(self.two_devices(hub={'device': 'hub'}))
        plug, hub = identified(self.devices, 'plug1:plug'), identified(self.devices, 'plug1:hub')
        self.assertEqual(plug.via_device_id, hub.id)
        self.assertIsNone(hub.via_device_id)
        made = [c[1]['name'] for c in self.devices.calls if c[0] == 'get_or_create']
        self.assertEqual(made[:2], ['Hub', 'Hall plug'])
        # Listed before its parent, a device that is new has no parent on this sync (Home Assistant refuses an unknown
        # via device id), and the sync still succeeds.
        await self.sync(definition(description('relay', device='lamp'), description('online', name=None, device='desk'),
                                   devices=[device('Lamp', 'lamp', via={'device': 'desk'}), device('Desk', 'desk')]))
        self.assertIsNone(identified(self.devices, 'plug1:lamp').via_device_id)
        self.assertIsNotNone(identified(self.devices, 'plug1:desk'))
        # A parent of a key no device has is no parent.
        await self.sync(self.two_devices(hub={'device': 'gone'}))
        self.assertIsNone(plug.via_device_id)
        # A parent of the same key in another definition is not this one's.
        self.devices.add('other:hub')
        await self.sync(definition(description('relay', device='plug'), devices=[device('Hall plug', 'plug', via={'device': 'hub'})]))
        self.assertIsNone(identified(self.devices, 'plug1:plug').via_device_id)

    async def test_the_entities_a_csharp_device_adds_stay_while_it_is_described(self):
        # Two C# devices of one class each add a relay: the host gives each the unique id <definition id>:<device key>:
        # <entity key> (tests/Runtime/EntityGraphTests.cs). Each stays while its device is described, goes with its
        # device, is kept by a prune that keeps its definition and goes with a prune or a removal of it.
        def plugs(*keys):
            return definition(*[description('relay', device=key, uniqueId=f'plug1:{key}:relay') for key in keys],
                              devices=[device(f'Plug {key}', key) for key in keys])
        result = await self.sync(plugs('plug_a', 'plug_b'))
        self.assertEqual(sorted(result['entities']), ['plug1:plug_a:relay', 'plug1:plug_b:relay'])
        plug_a, plug_b = identified(self.devices, 'plug1:plug_a'), identified(self.devices, 'plug1:plug_b')
        on = {e.unique_id: e.device_id for e in self.entities.entities.values()}
        self.assertEqual(on, {'plug1:plug_a:relay': plug_a.id, 'plug1:plug_b:relay': plug_b.id})
        relay_a = self.live('plug1:plug_a:relay')
        await self.sync(plugs('plug_a'))
        self.assertIsNone(identified(self.devices, 'plug1:plug_b'))
        self.assertEqual({e.unique_id: e.device_id for e in self.entities.entities.values()}, {'plug1:plug_a:relay': plug_a.id})
        self.assertIs(self.live('plug1:plug_a:relay'), relay_a)
        self.assertNotIn('plug1:plug_b:relay', self.bridge._live)
        # Another definition's entity of three parts, whose definition is gone, is pruned by its definition id alone.
        self.entities.add('switch', 'gone:plug:relay')
        answers = await self.answers(self.bridge.begin_prune, json.dumps(['plug1']))
        self.assertEqual(answers, [('success',)])
        self.assertEqual([e.unique_id for e in self.entities.entities.values()], ['plug1:plug_a:relay'])
        self.assertIs(identified(self.devices, 'plug1:plug_a'), plug_a)
        answers = await self.answers(self.bridge.begin_remove, 'plug1')
        self.assertEqual(answers, [('success',)])
        self.assertEqual((self.entities.entities, self.devices.devices, self.bridge._live), ({}, {}, {}))

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

    async def test_failed_state_writes_are_logged_once(self):
        await self.sync(definition(description('relay'), description('power', 'sensor', 'Power')))
        relay = self.live('plug1:relay')
        relay.apply_state = Mock(side_effect=ValueError('not a number'))
        await self.push(snapshot())
        await self.push(snapshot())
        self.logger.exception.assert_called_once()  # The first, with its traceback.
        self.logger.error.assert_not_called()
        relay.apply_state.side_effect = ValueError('too long')
        await self.push(snapshot(), snapshot())
        self.logger.error.assert_called_once()  # Another message: once, without the traceback.
        self.assertIn('too long', self.logger.error.call_args.args[0] % self.logger.error.call_args.args[1:])
        # Another entity's failure is its own.
        self.live('plug1:power').apply_state = Mock(side_effect=ValueError('not a number'))
        await self.push(snapshot('plug1:power'))
        self.assertEqual(self.logger.exception.call_count, 2)
        # Synced again, the definition's failures are logged afresh.
        await self.sync(definition(description('relay'), description('power', 'sensor', 'Power')))
        relay.apply_state.side_effect = ValueError('not a number')
        await self.push(snapshot())
        self.assertEqual(self.logger.exception.call_count, 3)

    async def test_write_failures_with_changing_messages_are_bounded(self):
        # Home Assistant puts the value into the message, so a sensor fed changing text fails differently on each push.
        await self.sync(definition(description('power', 'sensor', 'Power')))
        power = self.live('plug1:power')
        values = itertools.count()

        def fail(_):
            raise ValueError(f"has the non-numeric value: '{next(values)} °C'")
        power.apply_state = fail

        def lines():
            calls = self.logger.exception.call_args_list + self.logger.error.call_args_list \
                + self.logger.warning.call_args_list
            return [c.args[0] % c.args[1:] for c in calls]

        for _ in range(20):
            await self.push(snapshot('plug1:power'))
        self.assertEqual(self.logger.exception.call_count + self.logger.error.call_count, 5, lines())
        self.assertEqual(sum('not logged' in line for line in lines()), 1, lines())
        self.assertEqual(len(lines()), 6, lines())
        self.assertLessEqual(len(self.bridge._write_failures['plug1:power']), 6)
        # Synced again, the record starts afresh.
        await self.sync(definition(description('power', 'sensor', 'Power')))
        self.assertNotIn('plug1:power', self.bridge._write_failures)
        await self.push(snapshot('plug1:power'))
        self.assertEqual(self.logger.exception.call_count, 2, lines())

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
        self.assertEqual([d.id for d in self.devices.devices.values()], ['device-plug2:main'])
        self.assertIn(('remove', 'device-plug1:main'), self.devices.calls)
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
        self.assertEqual(sorted(self.devices.devices), ['device-plug1:main', 'device-plug3:main'])
        self.assertEqual(list(self.bridge._pending), ['plug1:later'])

    async def test_prune_keeps_the_devices_of_known_definitions(self):
        await self.sync(definition(description('relay', device='plug'), description('power', 'sensor', 'Power', device='hub'),
                                   devices=[device('Hub', 'hub'), device('Hall plug', 'plug')]))
        plug, hub = identified(self.devices, 'plug1:plug'), identified(self.devices, 'plug1:hub')
        older = self.devices.add('plug1')  # What an earlier build made: the next sync removes it, not the clean-up.
        gone_main, gone = self.devices.add('gone:main'), self.devices.add('gone')
        lone = self.devices.add('lone:main')  # Of a definition that is gone and had no entities.
        self.entities.add('switch', 'gone:relay', device_id=gone_main.id)
        self.entities.add('switch', 'gone:old', device_id=gone.id)
        # A definition whose id starts with a known one's is not that one.
        prefixed = self.devices.add('plug10:plug')
        self.entities.add('switch', 'plug10:relay', device_id=prefixed.id)
        answers = await self.answers(self.bridge.begin_prune, json.dumps(['plug1']))
        self.assertEqual(answers, [('success',)])
        self.assertEqual(sorted(self.devices.devices), sorted([plug.id, hub.id, older.id]))
        self.assertEqual({e.unique_id: e.device_id for e in self.entities.entities.values()},
                         {'plug1:relay': plug.id, 'plug1:power': hub.id})
        self.assertEqual({c[1] for c in self.devices.calls if c[0] == 'remove'}, {gone_main.id, gone.id, lone.id, prefixed.id})

    async def test_remove_purges_every_device_of_the_definition(self):
        await self.sync(definition(description('relay', device='plug'), description('power', 'sensor', 'Power', device='hub'),
                                   description('bare', name='Bare', device=None),
                                   devices=[device('Hub', 'hub'), device('Hall plug', 'plug')]))
        older = self.devices.add('plug1')
        self.entities.add('switch', 'plug1:old', device_id=older.id)
        self.entities.add('switch', 'plug1:disabled', device_id=identified(self.devices, 'plug1:plug').id, disabled_by='user')
        others = [self.devices.add('plug10:plug'), self.devices.add('plug10'), self.devices.add('plug1:x', config_entry_id='entry2')]
        self.entities.add('switch', 'plug10:relay', device_id=others[0].id)
        answers = await self.answers(self.bridge.begin_remove, 'plug1')
        self.assertEqual(answers, [('success',)])
        self.assertEqual(sorted(self.devices.devices), sorted(d.id for d in others))
        self.assertEqual([e.unique_id for e in self.entities.entities.values()], ['plug10:relay'])
        self.assertEqual(self.bridge._live, {})

    async def test_prune_refuses_a_malformed_list(self):
        await self.sync(definition())
        await self.sync(definition(definition_id='plug2'))
        # A string would otherwise be a set of characters and an object a set of keys: either purges everything.
        for request in ('"plug1"', '{"plug1": true}', '["plug1", 2]', 'null'):
            answers = await self.answers(self.bridge.begin_prune, request)
            self.assertEqual([a[0] for a in answers], ['error'], request)
        self.assertEqual(sorted(e.unique_id for e in self.entities.entities.values()), ['plug1:relay', 'plug2:relay'])
        self.assertEqual(sorted(self.devices.devices), ['device-plug1:main', 'device-plug2:main'])
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

    async def test_wait_closed_waits_for_an_add_in_flight(self):
        # Home Assistant finishes an add it started even after the platform unloaded (test_entity_platforms).
        self.platforms['switch'].gate = asyncio.Event()
        answers = []
        self.bridge.begin_sync(definition(description('relay'), description('power', 'sensor', 'Power')),
                               lambda *a: answers.append(('success', *a)), lambda *a: answers.append(('error', *a)))
        await asyncio.wait_for(self.platforms['switch'].entered.wait(), 2)
        self.bridge.close()
        self.assertEqual(answers, [('error', 'closed')])
        waiting = asyncio.ensure_future(self.bridge.async_wait_closed())
        await self.settle()
        self.assertFalse(waiting.done())
        self.platforms['switch'].gate.set()
        await asyncio.wait_for(waiting, 2)
        self.assertEqual(self.platforms['sensor'].added, [])  # The rest of the sync was skipped.
        self.assertEqual(answers, [('error', 'closed')])
        self.logger.warning.assert_not_called()
        # Nothing in flight: it returns at once.
        await asyncio.wait_for(self.bridge.async_wait_closed(), 0.1)

    async def test_wait_closed_gives_up_after_its_bound(self):
        self.platforms['switch'].gate = asyncio.Event()
        self.bridge.begin_sync(definition(), Mock(), Mock())
        await asyncio.wait_for(self.platforms['switch'].entered.wait(), 2)
        self.bridge.close()
        await asyncio.wait_for(self.bridge.async_wait_closed(0.01), 1)
        message, *args = self.logger.warning.call_args.args
        self.assertIn('restart Home Assistant', message % tuple(args))
        self.platforms['switch'].gate.set()
        await self.settle()

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

    # Cameras

    def attach_cameras(self):
        self.platforms['camera'] = Platform('camera', self.hass, self.entities, self.devices)
        self.bridge.attach_platform('camera', self.platforms['camera'], platform_class('camera'))

    async def test_camera_image_is_the_frame_as_bytes(self):
        frame = await self.bridge.async_camera_image('plug1:view')
        self.assertEqual((type(frame), frame), (bytes, JPEG))
        self.assertEqual(self.dotnet_calls, [(self.manager.CameraImage, ('plug1:view',))])

    async def test_camera_image_is_none_when_dotnet_gives_none(self):
        # .NET logged what the user needs; Python adds a debug line and nothing more.
        self.manager.CameraImage.side_effect = DotNetError('View has shown no frame')
        self.assertIsNone(await self.bridge.async_camera_image('plug1:view'))
        self.logger.debug.assert_called_once()
        message, *args = self.logger.debug.call_args.args
        self.assertIn('View has shown no frame', message % tuple(args))
        for level in (self.logger.info, self.logger.warning, self.logger.error, self.logger.exception):
            level.assert_not_called()

    async def test_camera_takes_a_new_frame_interval_live(self):
        self.attach_cameras()
        await self.sync(definition(description('view', 'camera', 'View', options={'frame_interval': 2})))
        view = self.live('plug1:view')
        self.assertEqual(view._attr_frame_interval, 2)
        await self.sync(definition(description('view', 'camera', 'View', options={'frame_interval': 5})))
        self.assertIs(self.live('plug1:view'), view)
        self.assertEqual(view._attr_frame_interval, 5)
        await self.sync(definition(description('view', 'camera', 'View', options={})))
        self.assertEqual(view._attr_frame_interval, 1)
        self.assertEqual(await view.async_camera_image(), JPEG)
        self.assertEqual(self.dotnet_calls[-1], (self.manager.CameraImage, ('plug1:view',)))

    async def snapshot(self, definition_id, entity_id, success=None):
        """Every answer a picture request gives, asked as .NET asks: from its own thread."""
        answers, first = [], self.loop.create_future()

        def answer(kind, callback=None):
            def call(*args):
                answers.append((kind, *args, threading.get_ident()))
                self.loop.call_soon_threadsafe(lambda: first.done() or first.set_result(None))
                if callback is not None:
                    callback(*args)
            return call
        thread = threading.Thread(target=self.bridge.begin_camera_snapshot,
                                  args=(definition_id, entity_id, answer('success', success), answer('error')))
        thread.start()
        thread.join()
        await asyncio.wait_for(asyncio.shield(first), 2)
        await self.settle()
        loop_thread = threading.get_ident()
        self.assertEqual({a[-1] for a in answers}, {loop_thread}, 'answered off the loop')
        return [a[:-1] for a in answers]

    async def test_snapshot_answers_with_the_pictures_bytes_once(self):
        self.cameras['camera.porch'] = NS(content_type='image/jpeg', content=JPEG)
        answers = await self.snapshot('front', 'camera.porch')
        self.assertEqual(answers, [('success', DotNetBytes(JPEG))])
        self.assertIs(type(answers[0][1]), DotNetBytes)  # Built as a System.Byte[], not converted implicitly.
        self.assertEqual([c[:2] for c in self.camera_calls], [('camera.porch', {'timeout': 10})])
        self.assertEqual({c[2] for c in self.camera_calls}, {threading.get_ident()})
        # An automation's step asks without a definition.
        self.assertEqual(await self.snapshot('', 'camera.porch'), [('success', DotNetBytes(JPEG))])

    async def test_snapshot_answers_home_assistants_error_once(self):
        for message in ('Camera not found', 'Camera is off', 'Unable to get image'):
            with self.subTest(message):
                self.cameras['camera.porch'] = HomeAssistantError(message)
                self.assertEqual(await self.snapshot('front', 'camera.porch'), [('error', message)])
        self.cameras['camera.porch'] = RuntimeError('the camera broke')
        self.assertEqual(await self.snapshot('front', 'camera.porch'), [('error', 'the camera broke')])
        self.logger.error.assert_not_called()

    async def test_snapshot_refuses_a_picture_that_is_not_a_jpeg(self):
        self.cameras['camera.porch'] = NS(content_type='image/png', content=b'\x89PNG')
        self.assertEqual(await self.snapshot('front', 'camera.porch'),
                         [('error', 'camera.porch gives image/png pictures; only JPEG pictures can be used')])
        self.cameras['camera.porch'] = NS(content_type='image/jpg', content=JPEG)
        self.assertEqual(await self.snapshot('front', 'camera.porch'), [('success', DotNetBytes(JPEG))])

    async def test_snapshot_refuses_a_camera_of_the_asking_definition(self):
        self.attach_cameras()
        await self.sync(definition(description('view', 'camera', 'View'), description('relay')))
        self.cameras['camera.plug1_view'] = NS(content_type='image/jpeg', content=JPEG)
        self.assertEqual(await self.snapshot('plug1', 'camera.plug1_view'),
                         [('error', 'camera.plug1_view is a camera of this definition; a definition cannot take a '
                                    'picture of its own camera')])
        self.assertEqual(self.camera_calls, [])  # Refused at once: it would wait for itself.
        # Another definition, or none, takes it; so does this one of a camera that is not its own.
        self.assertEqual(await self.snapshot('other', 'camera.plug1_view'), [('success', DotNetBytes(JPEG))])
        self.assertEqual(await self.snapshot('', 'camera.plug1_view'), [('success', DotNetBytes(JPEG))])
        self.cameras['camera.porch'] = NS(content_type='image/jpeg', content=JPEG)
        self.assertEqual(await self.snapshot('plug1', 'camera.porch'), [('success', DotNetBytes(JPEG))])
        # Its entities that are not cameras are Home Assistant's to refuse.
        self.assertEqual(await self.snapshot('plug1', 'switch.plug1_relay'), [('error', 'Camera not found')])

    async def test_snapshot_whose_callback_raises_stays_off_the_loop(self):
        self.cameras['camera.porch'] = NS(content_type='image/jpeg', content=JPEG)

        def fail(_):
            raise RuntimeError('.NET delegate failed')
        self.assertEqual(await self.snapshot('front', 'camera.porch', fail), [('success', DotNetBytes(JPEG))])
        self.logger.exception.assert_called_once()  # And nothing reached the loop (tearDown).

    async def test_snapshot_cancelled_or_closed_answers_once(self):
        # Home Assistant cancels the background tasks when it stops.
        self.cameras['camera.porch'] = asyncio.Event()
        answers = []
        self.bridge.begin_camera_snapshot('front', 'camera.porch', lambda b: answers.append(('success', b)),
                                          lambda m: answers.append(('error', m)))
        await self.settle()
        self.assertEqual(answers, [])
        self.background[-1].cancel()
        await self.settle()
        self.assertEqual(answers, [('error', 'Home Assistant is stopping, so no picture was taken')])
        # After close, while HassSharp unloads.
        self.bridge.close()
        self.assertEqual(await self.snapshot('front', 'camera.porch'),
                         [('error', 'HassSharp is stopping, so no picture was taken')])
        # After the loop closed.
        loop = asyncio.new_event_loop()
        loop.close()
        self.bridge.hass = NS(loop=loop)
        answers.clear()
        self.bridge.begin_camera_snapshot('front', 'camera.porch', answers.append, answers.append)
        self.assertEqual(answers, ['Home Assistant is stopping, so no picture was taken'])


class PlatformModulesTest(unittest.IsolatedAsyncioTestCase):
    """Each platform module over fake base classes: what it takes from a description's options and a snapshot's
    values, and the command and data each of its methods sends."""

    def entity(self, domain, **options):
        self.commands, self.frames, self.frame = [], [], None

        async def command(unique_id, name, data):
            self.commands.append((name, data))

        async def camera_image(unique_id):
            self.frames.append(unique_id)
            return self.frame
        bridge = NS(async_command=command, async_camera_image=camera_image)
        return platform_class(domain)(bridge, description('main', domain, 'Main', options=options), None, 'Def')

    def values(self, entity, **values):
        entity.apply_state({'available': True, 'values': values, 'attributes': {}})
        return entity

    async def test_button_presses(self):
        button = self.values(self.entity('button'))
        await button.async_press()
        self.assertEqual(self.commands, [('press', {})])

    async def test_number_takes_its_range_and_sets_a_value(self):
        number = self.entity('number', min=5, max=30, step=0.5, mode='box', unit='°C')
        self.assertEqual((number._attr_native_min_value, number._attr_native_max_value, number._attr_native_step,
                          number._attr_native_unit_of_measurement), (5, 30, 0.5, '°C'))
        self.assertIs(number._attr_mode, NumberMode.BOX)
        self.assertEqual(self.values(number, value=21.5)._attr_native_value, 21.5)
        self.assertIsNone(self.values(number, value=None)._attr_native_value)
        await number.async_set_native_value(22.0)
        self.assertEqual(self.commands, [('set_value', {'value': 22.0})])
        # Synced again with other options, a live number takes them.
        number.apply(description('main', 'number', 'Main', options={'min': 0, 'max': 1, 'step': 0.1, 'mode': 'auto',
                                                                    'unit': None}), None, 'Def')
        self.assertEqual((number._attr_native_max_value, number._attr_mode, number._attr_native_unit_of_measurement),
                         (1, NumberMode.AUTO, None))

    async def test_select_takes_its_options_and_selects_one(self):
        select = self.entity('select', options=['eco', 'comfort'])
        self.assertEqual(select._attr_options, ['eco', 'comfort'])
        self.assertEqual(self.values(select, option='comfort')._attr_current_option, 'comfort')
        self.assertIsNone(self.values(select, option=None)._attr_current_option)
        await select.async_select_option('eco')
        self.assertEqual(self.commands, [('select_option', {'option': 'eco'})])

    async def test_text_takes_its_lengths_pattern_and_mode_and_sets_a_text(self):
        text = self.entity('text', min=1, max=20, pattern='^[a-z ]*$', mode='password')
        self.assertEqual((text._attr_native_min, text._attr_native_max, text._attr_pattern), (1, 20, '^[a-z ]*$'))
        self.assertIs(text._attr_mode, TextMode.PASSWORD)
        self.assertEqual(self.values(text, value='hello')._attr_native_value, 'hello')
        await text.async_set_value('hi there')
        self.assertEqual(self.commands, [('set_value', {'value': 'hi there'})])

    async def test_text_refuses_only_a_pattern_python_cannot_read(self):
        cls = platform_class('text')
        options = {'min': 0, 'max': 10, 'mode': 'text'}
        self.assertEqual(cls.refusal(description('code', 'text', None, options={**options, 'pattern': '(?<n>a)'})),
                         'code has the pattern (?<n>a), which Home Assistant cannot use: unknown extension ?<n at '
                         'position 1')
        self.assertIsNone(cls.refusal(description('code', 'text', 'Code', options={**options, 'pattern': '^a+$'})))
        self.assertIsNone(cls.refusal(description('code', 'text', 'Code', options={**options, 'pattern': None})))
        # The other platforms refuse nothing.
        self.assertIsNone(platform_class('number').refusal(description('main', 'number', options={'pattern': '('})))

    async def test_light_takes_its_colour_mode_and_sends_brightness_only_when_given(self):
        lamp = self.entity('light', dimmable=True)
        self.assertEqual((lamp._attr_supported_color_modes, lamp._attr_color_mode),
                         ({ColorMode.BRIGHTNESS}, ColorMode.BRIGHTNESS))
        bulb = self.entity('light', dimmable=False)
        self.assertEqual((bulb._attr_supported_color_modes, bulb._attr_color_mode),
                         ({ColorMode.ONOFF}, ColorMode.ONOFF))
        self.values(lamp, is_on=True, brightness=128)
        self.assertEqual((lamp._attr_is_on, lamp._attr_brightness), (True, 128))
        self.values(lamp, is_on=None, brightness=None)
        self.assertEqual((lamp._attr_is_on, lamp._attr_brightness), (None, None))
        await lamp.async_turn_on(brightness=128, transition=2)
        await lamp.async_turn_on()
        await lamp.async_turn_off(transition=2)
        self.assertEqual(self.commands, [('turn_on', {'brightness': 128}), ('turn_on', {}), ('turn_off', {})])

    async def test_cover_takes_its_features_and_works_out_closed(self):
        blind = self.entity('cover', positionable=True)
        features = CoverEntityFeature.OPEN | CoverEntityFeature.CLOSE | CoverEntityFeature.STOP
        self.assertEqual(blind._attr_supported_features, features | CoverEntityFeature.SET_POSITION)
        self.assertEqual(self.entity('cover', positionable=False)._attr_supported_features, features)
        for values, closed, position in (({'is_closed': False, 'position': 0}, False, 0),
                                         ({'is_closed': None, 'position': 0}, True, 0),
                                         ({'position': 40}, False, 40),
                                         ({'is_closed': True, 'position': None}, True, None),
                                         ({'is_closed': None, 'position': None}, None, None)):
            self.values(blind, **values)
            self.assertEqual((blind._attr_is_closed, blind._attr_current_cover_position), (closed, position), values)
        await blind.async_open_cover()
        await blind.async_close_cover(speed='fast')
        await blind.async_stop_cover()
        await blind.async_set_cover_position(position=40)
        self.assertEqual(self.commands, [('open', {}), ('close', {}), ('stop', {}), ('set_position', {'position': 40})])

    async def test_camera_asks_the_bridge_for_its_frame(self):
        camera = self.values(self.entity('camera', frame_interval=2))
        # Both initialisers ran: the entity's and the camera's.
        self.assertEqual((camera.unique_id, camera.written, camera.content_type), ('plug1:main', [], 'image/jpeg'))
        self.assertEqual(camera._attr_frame_interval, 2)
        self.assertFalse(hasattr(camera, '_attr_supported_features'))
        self.frame = JPEG
        self.assertEqual(await camera.async_camera_image(), JPEG)
        # A size is Home Assistant's to scale to.
        self.assertEqual(await camera.async_camera_image(width=320, height=240), JPEG)
        self.frame = None
        self.assertIsNone(await camera.async_camera_image())
        self.assertEqual((self.frames, self.commands), (['plug1:main'] * 3, []))
        # Synced again, a live camera takes the new interval; 1 s when the option is left empty.
        for options, interval in (({'frame_interval': 0.5}, 0.5), ({'frame_interval': None}, 1), ({}, 1)):
            camera.apply(description('main', 'camera', 'Main', options=options), None, 'Def')
            self.assertEqual(camera._attr_frame_interval, interval, options)


if __name__ == '__main__':
    unittest.main()
