"""Entity discovery and generated-symbol refresh without a running HA instance."""
import ast
from pathlib import Path
from types import SimpleNamespace as NS
import unittest
from unittest.mock import Mock


def load_utils():
    tree = ast.parse(Path('custom_components/hass_sharp/utils.py').read_text())
    functions = [n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name.startswith('get_hass_entit')]
    registry = NS(entities={'sensor.offline': NS(name='Offline meter', original_name='Meter'),
                            'light.hall': NS(name='Registry hall', original_name=None),
                            'sensor.unnamed': NS(name=None, original_name=None)})
    env = {'HomeAssistant': object, 'er': NS(async_get=lambda _: registry)}
    exec(compile(ast.Module(body=functions, type_ignores=[]), '<utils>', 'exec'), env)
    hass = NS(states=NS(async_entity_ids=lambda: ['light.hall', 'sensor.live'],
                        get=lambda id: NS(attributes={'friendly_name': 'Hall lamp'}) if id == 'light.hall' else None))
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
    def test_state_and_registry_union_with_optional_names(self):
        env, hass = load_utils()
        self.assertEqual(env['get_hass_entity_suggestions'](hass), [
            {'entity_id': 'light.hall', 'friendly_name': 'Hall lamp'},
            {'entity_id': 'sensor.live'},
            {'entity_id': 'sensor.offline', 'friendly_name': 'Offline meter'},
            {'entity_id': 'sensor.unnamed'},
        ])

    async def test_list_authorization_response_and_retryable_failure(self):
        env, hass = load_utils()
        for admin, fail in [(False, False), (True, False), (True, True)]:
            getter = Mock(side_effect=RuntimeError('private') if fail else lambda h: env['get_hass_entity_suggestions'](h))
            route = load_route('get_entities', {'utils': NS(get_hass_entity_suggestions=getter), 'logger': Mock()})
            connection = NS(user=NS(is_admin=admin), send_result=Mock(), send_error=Mock())
            await route(hass, connection, {'id': 7})
            if not admin:
                getter.assert_not_called()
                self.assertEqual(connection.send_error.call_args.args[1], 'unauthorized')
            elif fail:
                connection.send_error.assert_called_once()
                self.assertNotIn('private', str(connection.send_error.call_args))
            else:
                connection.send_result.assert_called_once_with(7, {'entities': env['get_hass_entity_suggestions'](hass)})

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
        self.assertEqual(generated, [{'light.hall', 'sensor.live', 'sensor.offline', 'sensor.unnamed'}])
        connection.send_result.assert_called_once_with(8, {'success': True})
