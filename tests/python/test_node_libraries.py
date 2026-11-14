import ast
import json
import pathlib
import types
import unittest
from unittest.mock import Mock


class NodeLibraryApiTests(unittest.IsolatedAsyncioTestCase):
    async def test_admin_authoring_boundary_and_websocket_envelope(self):
        source_text = pathlib.Path('custom_components/hass_sharp/websocket.py').read_text()
        tree = ast.parse(source_text)
        helpers = [n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name in ('loaded', 'refusal')
                   or isinstance(n, ast.Assign) and any(getattr(t, 'id', None) in ('NOT_READY', 'REFUSALS') for t in n.targets)]
        register = next(n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == 'register_websocket_routes')
        handler = next((n for n in register.body if isinstance(n, ast.AsyncFunctionDef) and n.name == 'websocket_node_library'), None)
        self.assertIsNotNone(handler, 'library WebSocket routes through shared authoring service')
        source = ast.parse(pathlib.Path('custom_components/hass_sharp/authoring.py').read_text())
        source.body = [n for n in source.body if not isinstance(n, ast.ImportFrom)]
        calls = []
        async def run(hass, method, request):
            calls.append(json.loads(request))
            return '{"version":1,"ok":false,"error":{"code":"conflict","detail":"reload"}}'
        env = {'json':json, 'dotnet_tasks':types.SimpleNamespace(async_run_dotnet_task=run)}
        exec(compile(source, '<authoring>', 'exec'), env)
        service = env['AuthoringService'](None, types.SimpleNamespace(NodeLibraryRequest=object()))
        with self.assertRaises(PermissionError):
            await service.node_library({'operation':'delete','id':'shared','expectedRevision':1}, is_admin=False)
        self.assertEqual(calls, [])
        handler.decorator_list=[]
        env.update(authoring=service, logger=Mock())
        exec(compile(ast.Module(body=helpers+[handler],type_ignores=[]), '<node-library>', 'exec'), env)
        hass=types.SimpleNamespace(data={'hass_sharp_llm':{'entry':types.SimpleNamespace(ready=True)}})
        for admin in (False,True):
            connection=types.SimpleNamespace(user=types.SimpleNamespace(is_admin=admin),send_result=Mock(),send_error=Mock())
            await env['websocket_node_library'](hass,connection,{'id':7,'request':{'operation':'save','id':'shared','source':'class C {}','name':'Shared.cs','expectedRevision':1}})
            if admin:
                self.assertEqual(connection.send_result.call_args.args[1]['error']['code'],'conflict')
            else: self.assertEqual(connection.send_error.call_args.args[1],'unauthorized')
        self.assertEqual(len(calls),1)
        self.assertEqual(calls[0]['expectedRevision'],1)
