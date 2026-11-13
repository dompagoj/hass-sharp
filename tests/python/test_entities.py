"""Entity discovery and generated-symbol refresh without a running HA instance."""
import ast
from pathlib import Path
from types import SimpleNamespace as NS
import unittest
from unittest.mock import Mock


def entry(name=None, original_name=None, area_id=None, device_id=None, device_class=None,
          original_device_class=None, supported_features=0, unit_of_measurement=None, icon=None,
          original_icon=None, disabled_by=None):
    return NS(name=name, original_name=original_name, area_id=area_id, device_id=device_id,
              device_class=device_class, original_device_class=original_device_class,
              supported_features=supported_features, unit_of_measurement=unit_of_measurement,
              icon=icon, original_icon=original_icon, disabled_by=disabled_by)


def load_utils():
    tree = ast.parse(Path('custom_components/hass_sharp/utils.py').read_text())
    functions = [n for n in tree.body if isinstance(n, ast.FunctionDef)
                 and (n.name.startswith('get_hass_entit') or n.name.startswith('_entity'))]
    registry = NS(entities={
        'sensor.offline': entry(name='Offline meter', original_name='Meter', device_id='dev1',
                                original_device_class='energy', unit_of_measurement='kWh', disabled_by='user'),
        'light.hall': entry(name='Registry hall', area_id='hall', device_id='dev1', supported_features=4,
                            original_icon='mdi:lamp'),
        'sensor.unnamed': entry(device_class='power', original_device_class='energy'),
        'climate.living': entry(device_id='dev2'),
        'select.mode': entry(),
        'input_select.scene': entry(),
    })
    # HA 2026.11 deprecates `devices` as a mapping: only iterate it, and look devices up with `async_get`.
    device_entries = {'dev1': NS(id='dev1', name='Hub', name_by_user='Hall hub', area_id='hall'),
                      'dev2': NS(id='dev2', name='Thermostat', name_by_user=None, area_id=None)}
    devices = NS(devices=tuple(device_entries.values()), async_get=device_entries.get)
    areas = NS(async_list_areas=lambda: [NS(id='hall', name='Hall', floor_id='ground')])
    floors = NS(async_list_floors=lambda: [NS(floor_id='ground', name='Ground floor')])
    labels = NS(async_list_labels=lambda: [NS(label_id='night', name='Night')])
    env = {'HomeAssistant': object, 'er': NS(async_get=lambda _: registry), 'dr': NS(async_get=lambda _: devices),
           'ar': NS(async_get=lambda _: areas), 'fr': NS(async_get=lambda _: floors),
           'lr': NS(async_get=lambda _: labels)}
    exec(compile(ast.Module(body=functions, type_ignores=[]), '<utils>', 'exec'), env)
    states = {
        'light.hall': NS(attributes={'friendly_name': 'Hall lamp', 'supported_features': 44, 'icon': 'mdi:ceiling-light'}),
        'sensor.live': NS(attributes={'device_class': 'temperature', 'unit_of_measurement': '°C'}),
        'sensor.unnamed': NS(attributes={'device_class': 'current'}),
        'climate.living': NS(attributes={'friendly_name': 'Living', 'hvac_modes': ['off', 'heat'], 'options': ['x']}),
        'select.mode': NS(attributes={'options': ['eco', 'boost'], 'hvac_modes': ['off']}),
        'input_select.scene': NS(attributes={'options': ('day', 'night')}),
    }
    hass = NS(states=NS(async_entity_ids=lambda: [i for i in states], get=states.get))
    return env, hass


def load_route(name, env):
    tree = ast.parse(Path('custom_components/hass_sharp/websocket.py').read_text())
    register = next(n for n in tree.body if isinstance(n, ast.FunctionDef))
    node = next(n for n in register.body if isinstance(n, ast.AsyncFunctionDef) and n.name == 'websocket_' + name)
    node.decorator_list = []
    for arg in node.args.args:
        arg.annotation = None
    exec(compile(ast.Module(body=[node], type_ignores=[]), '<route>', 'exec'), env)
    return env[node.name]


class Entities(unittest.IsolatedAsyncioTestCase):
    def registry(self):
        env, hass = load_utils()
        result = env['get_hass_entity_registry'](hass)
        return result, {e['entity_id']: e for e in result['entities']}

    def test_registry_shape_and_union_of_state_and_registry_entities(self):
        result, entities = self.registry()
        self.assertEqual(list(entities), sorted(entities))
        self.assertEqual(set(entities), {'climate.living', 'input_select.scene', 'light.hall', 'select.mode',
                                         'sensor.live', 'sensor.offline', 'sensor.unnamed'})
        self.assertEqual(entities['light.hall'], {
            'entity_id': 'light.hall', 'name': 'Hall lamp', 'domain': 'light', 'area_id': 'hall', 'device_id': 'dev1',
            'device_class': None, 'supported_features': 44, 'unit': None, 'icon': 'mdi:ceiling-light',
            'disabled': False, 'state_options': None})
        self.assertEqual(entities['sensor.live'], {
            'entity_id': 'sensor.live', 'name': None, 'domain': 'sensor', 'area_id': None, 'device_id': None,
            'device_class': 'temperature', 'supported_features': None, 'unit': '°C', 'icon': None,
            'disabled': False, 'state_options': None})
        offline = entities['sensor.offline']
        self.assertEqual((offline['name'], offline['unit'], offline['disabled'], offline['supported_features']),
                         ('Offline meter', 'kWh', True, 0))
        self.assertEqual(result['areas'], [{'area_id': 'hall', 'name': 'Hall', 'floor_id': 'ground'}])
        self.assertEqual(result['devices'], [{'id': 'dev1', 'name': 'Hall hub', 'area_id': 'hall'},
                                             {'id': 'dev2', 'name': 'Thermostat', 'area_id': None}])
        self.assertEqual(result['floors'], [{'floor_id': 'ground', 'name': 'Ground floor'}])
        self.assertEqual(result['labels'], [{'label_id': 'night', 'name': 'Night'}])

    def test_area_falls_back_to_device_area(self):
        _, entities = self.registry()
        self.assertEqual(entities['sensor.offline']['area_id'], 'hall')
        self.assertIsNone(entities['climate.living']['area_id'])

    def test_device_class_prefers_registry_then_original_then_state(self):
        _, entities = self.registry()
        self.assertEqual(entities['sensor.unnamed']['device_class'], 'power')
        self.assertEqual(entities['sensor.offline']['device_class'], 'energy')
        self.assertEqual(entities['sensor.live']['device_class'], 'temperature')

    def test_state_options_for_climate_select_and_other_domains(self):
        _, entities = self.registry()
        self.assertEqual(entities['climate.living']['state_options'], ['off', 'heat'])
        self.assertEqual(entities['select.mode']['state_options'], ['eco', 'boost'])
        self.assertEqual(entities['input_select.scene']['state_options'], ['day', 'night'])
        self.assertIsNone(entities['light.hall']['state_options'])
        self.assertIsNone(entities['sensor.offline']['state_options'])

    async def test_list_authorization_response_and_retryable_failure(self):
        env, hass = load_utils()
        for admin, fail in [(False, False), (True, False), (True, True)]:
            getter = Mock(side_effect=RuntimeError('private') if fail else lambda h: env['get_hass_entity_registry'](h))
            route = load_route('get_entities', {'utils': NS(get_hass_entity_registry=getter), 'logger': Mock()})
            connection = NS(user=NS(is_admin=admin), send_result=Mock(), send_error=Mock())
            await route(hass, connection, {'id': 7})
            if not admin:
                getter.assert_not_called()
                self.assertEqual(connection.send_error.call_args.args[1], 'unauthorized')
            elif fail:
                connection.send_error.assert_called_once()
                self.assertNotIn('private', str(connection.send_error.call_args))
            else:
                connection.send_result.assert_called_once_with(7, env['get_hass_entity_registry'](hass))

    async def test_refresh_passes_current_union_to_generator_and_preserves_response(self):
        env, hass = load_utils()
        generated = []
        async def execute(fn, *args):
            return fn(*args)
        hass.async_add_executor_job = execute
        route = load_route('reload_entities', {'utils': NS(get_hass_entities=env['get_hass_entities']),
                            'hass_sharp': NS(GenerateHassEntities=lambda ids: generated.append(set(ids))), 'logger': Mock()})
        connection = NS(user=NS(is_admin=True), send_result=Mock(), send_error=Mock())
        await route(hass, connection, {'id': 8})
        self.assertEqual(generated, [{'climate.living', 'input_select.scene', 'light.hall', 'select.mode',
                                      'sensor.live', 'sensor.offline', 'sensor.unnamed'}])
        connection.send_result.assert_called_once_with(8, {'success': True})
