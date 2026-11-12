import ast
import json
import pathlib
import types
import unittest
from unittest.mock import Mock


class VisualApiTests(unittest.IsolatedAsyncioTestCase):
    async def test_admin_enforcement_and_json_bridge(self):
        source = pathlib.Path('custom_components/hass_sharp/websocket.py').read_text()
        register = next(n for n in ast.parse(source).body if isinstance(n, ast.FunctionDef) and n.name == 'register_websocket_routes')
        handler = next(n for n in register.body if isinstance(n, ast.AsyncFunctionDef) and n.name == 'websocket_visual')
        handler.decorator_list = []
        calls = []
        async def run(hass, method, request):
            calls.append(json.loads(request))
            return '{"version":1,"ok":true,"result":[]}'
        env = {'json': json, 'logger': Mock(), 'hass_sharp': types.SimpleNamespace(VisualRequest=object()),
               'dotnet_tasks': types.SimpleNamespace(async_run_dotnet_task=run)}
        exec(compile(ast.Module(body=[handler], type_ignores=[]), '<visual>', 'exec'), env)
        for admin in (False, True):
            results, errors = [], []
            connection = types.SimpleNamespace(user=types.SimpleNamespace(is_admin=admin),
                send_result=lambda *v: results.append(v), send_error=lambda *v: errors.append(v))
            await env['websocket_visual'](None, connection, {'id': 1, 'request': {'operation': 'list'}})
            self.assertEqual(bool(results), admin)
            self.assertEqual(bool(errors), not admin)
        self.assertEqual(calls, [{'operation': 'list'}])
