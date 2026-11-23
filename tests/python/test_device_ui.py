"""The hass_sharp/device_ui route's Python side: an administrator's request goes to .NET's DeviceUiRequest as its JSON,
and the envelope .NET answers comes back as it is, a refusal included; device pages reach no other Python code.

.NET is a fake that answers each operation in DeviceUiApi's shapes.
"""
import asyncio
import json
import os
import pathlib
import threading
import unittest
from types import SimpleNamespace as NS

from test_visual import EntryState, load_routes

ROOT = pathlib.Path(os.environ.get('HASS_SHARP_TEST_ROOT', pathlib.Path(__file__).resolve().parents[2]))
ENTRY = 'entry1'
PAGE = {'nodeKey': 'door', 'title': 'Door', 'revision': 'r1', 'attention': 1, 'lists': [
    {'id': 'faces', 'title': 'Faces', 'attention': 1, 'empty': None, 'cards': [
        {'id': 'face1', 'image': 'ab' * 32, 'title': 'Unknown', 'detail': None,
         'fields': [{'key': 'name', 'label': 'Name', 'type': 'text', 'value': ''}],
         'actions': [{'id': 'save', 'label': 'Save', 'style': 'primary'}]}]}]}
# Each operation as the panel sends it, with what DeviceUiApi answers it.
OPERATIONS = {
    'read': ({'operation': 'read', 'definitionId': 'door', 'knownRevision': None},
             {'running': True, 'revision': '3:1', 'pages': [PAGE]}),
    'image': ({'operation': 'image', 'definitionId': 'door', 'nodeKey': 'door', 'hash': 'ab' * 32},
              {'jpeg': '/9j/' + 'A' * 4096}),
    'act': ({'operation': 'act', 'definitionId': 'door', 'nodeKey': 'door', 'list': 'faces', 'card': 'face1',
             'action': 'save', 'values': {'name': 'Ana', 'count': 2}},
            {'status': 'done', 'message': None}),
    'counts': ({'operation': 'counts'}, {'attention': {'door': {'door': 1}}, 'stored': {'door': ['door']}}),
    'stores': ({'operation': 'stores'}, {'shared': [{'name': 'faces', 'bytes': 1024, 'users': ['door']}]}),
    'delete_store': ({'operation': 'delete_store', 'name': 'faces'}, {'deleted': True}),
}


class DeviceUiTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.requests = []
        self.answer = None  # an envelope .NET answers instead, for every request
        self.raises = None  # an exception .NET raises instead
        self.hass = NS(data={'hass_sharp_llm': {'unloading': EntryState(None), ENTRY: EntryState(None, ready=True)}})
        manager = NS(DeviceUiRequest=self.dotnet,
                     EntityRequest=lambda raw: self.fail('a device page request reached EntityRequest'),
                     VisualRequest=lambda raw: self.fail('a device page request reached VisualRequest'))
        handlers, self.env = load_routes(manager, self.hass)
        self.route = handlers['hass_sharp/device_ui']

    def dotnet(self, raw):
        """DeviceUiRequest: answers as DeviceUiApi does, in its envelope."""
        self.assertIsInstance(raw, str)
        request = json.loads(raw)
        self.requests.append(request)
        if self.raises is not None:
            raise self.raises
        if self.answer is not None:
            return json.dumps(self.answer)
        return json.dumps({'version': 1, 'ok': True, 'result': OPERATIONS[request['operation']][1]})

    async def send(self, request, *, admin=True):
        results, errors = [], []
        connection = NS(user=NS(is_admin=admin), send_result=lambda *v: results.append(v), send_error=lambda *v: errors.append(v))
        await self.route(self.hass, connection, {'id': 7, 'type': 'hass_sharp/device_ui', 'request': request})
        return results, errors

    async def test_an_admin_request_reaches_dotnet(self):
        for operation, (request, result) in OPERATIONS.items():
            with self.subTest(operation=operation):
                self.requests.clear()
                results, errors = await self.send(request)
                self.assertEqual(errors, [])
                self.assertEqual(self.requests, [request])
                self.assertEqual(results, [(7, {'version': 1, 'ok': True, 'result': result})])
        self.env['logger'].exception.assert_not_called()

    async def test_a_non_admin_is_refused(self):
        results, errors = await self.send(OPERATIONS['counts'][0], admin=False)
        self.assertEqual((results, errors), ([], [(7, 'unauthorized', 'Administrator required')]))
        self.assertEqual(self.requests, [])
        # The service refuses one too, and a request that is not an object, before .NET sees either.
        service = self.env['AuthoringService'](self.hass, NS(DeviceUiRequest=self.dotnet))
        with self.assertRaises(PermissionError):
            await service.device_ui({'operation': 'counts'}, is_admin=False)
        for request in (None, [], 'counts'):
            with self.subTest(request=request), self.assertRaises(ValueError):
                await service.device_ui(request, is_admin=True)
        self.assertEqual(self.requests, [])

    async def test_not_ready(self):
        for data in ({'hass_sharp_llm': {'unloading': EntryState(None)}}, {}):
            with self.subTest(data=data):
                self.hass.data = data
                results, errors = await self.send(OPERATIONS['read'][0])
                self.assertEqual(results, [])
                self.assertEqual([e[:2] for e in errors], [(7, 'not_ready')])
        self.assertEqual(self.requests, [])

    async def test_a_failure_is_answered_as_device_ui_failed(self):
        self.raises = RuntimeError('the runtime is gone')
        results, errors = await self.send(OPERATIONS['act'][0])
        self.assertEqual((results, errors), ([], [(7, 'device_ui_failed', 'Device page request failed; check integration logs')]))
        # Logged as the subject alone: never what the request carried.
        self.env['logger'].exception.assert_called_once_with('%s request failed', 'Device page')

    async def test_refusals_come_through_as_results(self):
        for code, detail in (('not_running', 'The entity definition is not running'),
                             ('not_found', "The device's page shows no such image"),
                             ('in_use', 'faces is in use by Door: turn them off first.'),
                             ('invalid_request', 'Unknown device page operation')):
            with self.subTest(code=code):
                self.answer = {'version': 1, 'ok': False, 'error': {'code': code, 'detail': detail}}
                results, errors = await self.send({'operation': 'image', 'definitionId': 'door', 'nodeKey': 'door', 'hash': 'x'})
                self.assertEqual(errors, [])
                self.assertEqual(results, [(7, self.answer)])
        self.env['logger'].exception.assert_not_called()

    async def test_a_slow_act_does_not_hold_up_other_requests(self):
        # As dotnet_tasks.async_run_dotnet_task: the call runs in Home Assistant's executor, the answer is awaited on the
        # loop. A press whose handler takes its time does not keep a read, nor another press, waiting.
        entered, released = threading.Event(), threading.Event()
        def dotnet(raw):
            request = json.loads(raw)
            if request.get('card') == 'slow':
                entered.set()
                released.wait(10)
            return json.dumps({'version': 1, 'ok': True, 'result': OPERATIONS[request['operation']][1]})
        async def run_dotnet_task(hass, method, request):
            return await asyncio.get_running_loop().run_in_executor(None, method, request)
        handlers, _ = load_routes(NS(DeviceUiRequest=dotnet), self.hass, dotnet_tasks=NS(async_run_dotnet_task=run_dotnet_task))
        self.route = handlers['hass_sharp/device_ui']
        slow = asyncio.create_task(self.send({**OPERATIONS['act'][0], 'card': 'slow'}))
        try:
            # Its handler is running before the other requests come.
            self.assertTrue(await asyncio.to_thread(entered.wait, 5))
            for request in (OPERATIONS['read'][0], OPERATIONS['act'][0], OPERATIONS['counts'][0]):
                results, _ = await asyncio.wait_for(self.send(request), 5)
                self.assertTrue(results[0][1]['ok'])
            self.assertFalse(slow.done())
        finally:
            released.set()
        results, _ = await slow
        self.assertEqual(results[0][1]['result'], {'status': 'done', 'message': None})

    def test_device_pages_are_not_an_llm_tool(self):
        # Pages, images and stored data reach administrators through the websocket only: no MCP tool, no HTTP route.
        integration = ROOT / 'custom_components' / 'hass_sharp'
        found = sorted(str(path.relative_to(integration)) for path in integration.rglob('*.py')
                       if 'DeviceUiRequest' in (text := path.read_text()) or 'device_ui' in text)
        self.assertEqual(found, ['authoring.py', 'websocket.py'])
