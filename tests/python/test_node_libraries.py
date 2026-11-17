import json
import types
import unittest
from unittest.mock import Mock

from test_visual import EntryState, load_routes


class NodeLibraryApiTests(unittest.IsolatedAsyncioTestCase):
    async def test_admin_authoring_boundary_and_websocket_envelope(self):
        calls = []
        def library_request(request):
            calls.append(json.loads(request))
            return '{"version":1,"ok":false,"error":{"code":"conflict","detail":"reload"}}'
        hass=types.SimpleNamespace(data={'hass_sharp_llm':{'entry':EntryState(None, ready=True)}})
        manager = types.SimpleNamespace(NodeLibraryRequest=library_request)
        # The library WebSocket routes through the shared authoring service, admin_command (the admin and readiness
        # check) included.
        handlers, env = load_routes(manager, hass)
        service = env['AuthoringService'](hass, manager)
        with self.assertRaises(PermissionError):
            await service.node_library({'operation':'delete','id':'shared','expectedRevision':1}, is_admin=False)
        self.assertEqual(calls, [])
        for admin in (False,True):
            connection=types.SimpleNamespace(user=types.SimpleNamespace(is_admin=admin),send_result=Mock(),send_error=Mock())
            await handlers['hass_sharp/node_library'](hass,connection,{'id':7,'request':{'operation':'save','id':'shared','source':'class C {}','name':'Shared.cs','expectedRevision':1}})
            if admin:
                self.assertEqual(connection.send_result.call_args.args[1]['error']['code'],'conflict')
            else: self.assertEqual(connection.send_error.call_args.args[1],'unauthorized')
        self.assertEqual(len(calls),1)
        self.assertEqual(calls[0]['expectedRevision'],1)
