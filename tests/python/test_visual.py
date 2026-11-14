import ast
import asyncio
import contextlib
import dataclasses
import json
import pathlib
import re
import types
import unittest
from unittest.mock import Mock


def load_entry_state():
    """llm.EntryState as written, without importing llm.py (it needs probatio, which only newer Home Assistant has)."""
    tree = ast.parse(pathlib.Path('custom_components/hass_sharp/llm.py').read_text())
    tree.body = [n for n in tree.body if isinstance(n, ast.ClassDef) and n.name == 'EntryState']
    env = {'asyncio': asyncio, 'contextmanager': contextlib.contextmanager, 'dataclass': dataclasses.dataclass,
           'field': dataclasses.field, 'AuthoringService': object}
    exec(compile(tree, '<llm>', 'exec'), env)
    return env['EntryState']


EntryState = load_entry_state()


class VisualApiTests(unittest.IsolatedAsyncioTestCase):
    async def test_admin_enforcement_and_json_bridge(self):
        calls = []
        def request(raw):
            calls.append(json.loads(raw))
            return '{"version":1,"ok":true,"result":[]}'
        hass = types.SimpleNamespace(data={'hass_sharp_llm': {'entry': EntryState(None, ready=True)}})
        handlers, env = load_routes(types.SimpleNamespace(VisualRequest=request), hass)
        env['websocket_visual'] = handlers['hass_sharp/visual']
        for admin in (False, True):
            results, errors = [], []
            connection = types.SimpleNamespace(user=types.SimpleNamespace(is_admin=admin),
                send_result=lambda *v: results.append(v), send_error=lambda *v: errors.append(v))
            await env['websocket_visual'](hass, connection, {'id': 1, 'request': {'operation': 'list'}})
            self.assertEqual(bool(results), admin)
            self.assertEqual(bool(errors), not admin)
        self.assertEqual(calls, [{'operation': 'list'}])

    async def test_set_enabled_is_admin_only_and_home_list_has_no_trigger_list(self):
        calls = []
        graph = {'nodes': [{'id': 't', 'kind': 'trigger', 'config': {}}]}
        def run(request):
            request = json.loads(request)
            calls.append(request)
            if request['operation'] == 'set_enabled':
                return json.dumps({'version': 1, 'ok': True, 'result': {'id': request['id'], 'enabled': request['enabled']}})
            documents = [{'id': 'a', 'outline': {'nodes': []}}] if request.get('view') == 'home' else [{'id': 'a', 'graph': graph, 'enabled': False, 'currentRevisionActive': True}]
            return json.dumps({'version': 1, 'ok': True, 'result': documents})
        hass = types.SimpleNamespace(data={'hass_sharp_llm': {'entry': EntryState(None, ready=True)}})
        handlers, env = load_routes(types.SimpleNamespace(VisualRequest=run), hass)
        env['websocket_visual'] = handlers['hass_sharp/visual']
        async def send(admin, request):
            results, errors = [], []
            connection = types.SimpleNamespace(user=types.SimpleNamespace(is_admin=admin),
                send_result=lambda *v: results.append(v), send_error=lambda *v: errors.append(v))
            await env['websocket_visual'](hass, connection, {'id': 1, 'request': request})
            return results, errors
        results, errors = await send(False, {'operation': 'set_enabled', 'id': 'a', 'enabled': False})
        self.assertEqual((results, calls), ([], []))
        self.assertEqual(errors[0][1], 'unauthorized')
        results, errors = await send(True, {'operation': 'set_enabled', 'id': 'a', 'enabled': False})
        self.assertEqual(results[0][1]['result'], {'id': 'a', 'enabled': False})
        self.assertEqual(calls, [{'operation': 'set_enabled', 'id': 'a', 'enabled': False}])
        results, _ = await send(True, {'operation': 'list'})
        self.assertEqual(results[0][1]['result'][0]['enabled'], False)
        self.assertEqual(results[0][1]['result'][0]['triggers'][0]['triggerId'], 't')
        results, _ = await send(True, {'operation': 'list', 'view': 'home', 'todayUtc': '2026-11-14T00:00:00Z'})
        self.assertEqual(results[0][1]['result'], [{'id': 'a', 'outline': {'nodes': []}}])


def load_routes(manager, hass):
    """Every websocket command of websocket.py, registered against fakes of Home Assistant's websocket API."""
    tree = ast.parse(pathlib.Path('custom_components/hass_sharp/websocket.py').read_text())
    tree.body = [n for n in tree.body if not isinstance(n, (ast.Import, ast.ImportFrom))]
    service_tree = ast.parse(pathlib.Path('custom_components/hass_sharp/authoring.py').read_text())
    service_tree.body = [n for n in service_tree.body if not isinstance(n, (ast.Import, ast.ImportFrom))]
    async def run(hass, method, request):
        return method(request)
    async def executor(fn, *args):
        return fn(*args)
    hass.async_add_executor_job = executor
    handlers = {}
    anything = lambda *a, **k: (a[0] if a else None)
    env = {'json': json, 'logger': Mock(), 'dotnet_tasks': types.SimpleNamespace(async_run_dotnet_task=run), 'native_triggers': None,
           'utils': types.SimpleNamespace(get_hass_entity_registry=lambda h: {'entities': []}),
           'vol': types.SimpleNamespace(Schema=anything, Required=anything, Optional=anything, All=anything, Range=anything, In=anything),
           'websocket_api': types.SimpleNamespace(decorators=types.SimpleNamespace(async_response=lambda f: f),
                                                  async_register_command=lambda h, name, handler, schema: handlers.__setitem__(name, handler))}
    exec(compile(service_tree, '<authoring>', 'exec'), env)
    exec(compile(tree, '<websocket>', 'exec'), env)
    env['register_websocket_routes'](hass, manager)
    return handlers, env


class WebsocketReadinessTests(unittest.IsolatedAsyncioTestCase):
    async def send(self, handler, hass, msg):
        results, errors = [], []
        connection = types.SimpleNamespace(user=types.SimpleNamespace(is_admin=True),
            send_result=lambda *v: results.append(v), send_error=lambda *v: errors.append(v))
        await handler(hass, connection, {'id': 1, **msg})
        return results, errors

    async def test_commands_answer_not_ready_while_no_entry_is_loaded(self):
        calls = []
        manager = types.SimpleNamespace(
            VisualRequest=lambda r: calls.append('visual') or '{"version":1,"ok":true,"result":[]}',
            NodeLibraryRequest=lambda r: calls.append('library') or '{"version":1,"ok":true,"result":{}}',
            RunNow=lambda *a: calls.append('run') or '{"runId":"r"}',
            GetRunHistory=lambda *a: calls.append('history') or '{"runs":[]}',
            GetRunTrace=lambda *a: calls.append('trace') or '{"version":1}')
        state = EntryState(None)
        hass = types.SimpleNamespace(data={'hass_sharp_llm': {'entry': state}})
        handlers, _ = load_routes(manager, hass)
        messages = {'hass_sharp/visual': {'request': {'operation': 'list'}}, 'hass_sharp/node_library': {'request': {'operation': 'list'}},
                    'hass_sharp/run_now': {'automationId': 'a', 'className': 'VisualAutomation', 'method': 'Run_74'},
                    'hass_sharp/run_history': {'automationId': 'a', 'offset': 0, 'limit': 50},
                    'hass_sharp/run_trace': {'runId': 'r'}}
        for command, msg in messages.items():
            with self.subTest(command=command):
                results, errors = await self.send(handlers[command], hass, msg)
                self.assertEqual(results, [])
                self.assertEqual(errors[0][1], 'not_ready')
        hass.data = {}
        results, errors = await self.send(handlers['hass_sharp/visual'], hass, messages['hass_sharp/visual'])
        self.assertEqual(errors[0][1], 'not_ready')
        self.assertEqual(calls, [])
        state.ready = True
        hass.data = {'hass_sharp_llm': {'entry': state}}
        for command, msg in messages.items():
            with self.subTest(command=command, ready=True):
                results, errors = await self.send(handlers[command], hass, msg)
                self.assertEqual(errors, [])
        self.assertEqual(calls, ['visual', 'library', 'run', 'history', 'trace'])

    def test_every_refusal_run_now_answers_in_csharp_is_known(self):
        # The messages Manager.RunNow, VisualApi.DispatchVisual and UserScriptManager.DispatchManual refuse with, read
        # from the C# source, are exactly the refusals the websocket shows as they are (and does not log as defects).
        library = pathlib.Path('custom_components/hass_sharp/HassSharp')
        def body(path, start):
            text = (library / path).read_text()
            begin = text.index(start)
            return text[begin:text.index('\n    }\n', begin)]
        thrown = re.compile(r'throw new (?:InvalidOperationException|ArgumentException)\("([^"]+)"\)')
        messages = set(thrown.findall(body('Manager.cs', 'public string RunNow(')))
        messages |= set(thrown.findall(body('Visual/VisualApi.cs', 'object DispatchVisual(')))
        dispatch = body('UserScriptManager/UserScriptManager.cs', 'public string DispatchManual(')
        self.assertIn('throw new InvalidOperationException(NotRunning)', dispatch)
        messages.add(re.search(r'const string NotRunning = "([^"]+)";', (library / 'UserScriptManager/UserScriptManager.cs').read_text()).group(1))
        source = pathlib.Path('custom_components/hass_sharp/websocket.py').read_text()
        assignment = next(n for n in ast.parse(source).body if isinstance(n, ast.Assign) and any(getattr(t, 'id', None) == 'REFUSALS' for t in n.targets))
        refusals = eval(compile(ast.Expression(assignment.value), '<refusals>', 'eval'), {'frozenset': frozenset})
        self.assertGreaterEqual(len(messages), 7)
        self.assertEqual(refusals, messages)

    async def test_run_now_on_an_automation_that_is_not_active_is_a_plain_refusal(self):
        # Shaped like Python.NET's exceptions: the .NET type name, Message without the stack trace, str() with it.
        class InvalidOperationException(Exception):
            Message = 'Automation is not active'
        class ArgumentException(Exception):
            Message = 'Trigger not found'
        class Defect(InvalidOperationException):
            Message = 'Sequence contains no elements'
        refusals = iter([InvalidOperationException('Automation is not active\n   at HassSharp.HassSharpManager.RunNow'),
                         ArgumentException('Trigger not found\n   at ...'), KeyError('defect'),
                         Defect('Sequence contains no elements\n   at System.Linq.ThrowHelper')])
        def run_now(*args):
            raise next(refusals)
        hass = types.SimpleNamespace(data={'hass_sharp_llm': {'entry': EntryState(None, ready=True)}})
        handlers, env = load_routes(types.SimpleNamespace(RunNow=run_now), hass)
        msg = {'automationId': 'a', 'className': 'VisualAutomation', 'method': 'Run_74'}
        _, errors = await self.send(handlers['hass_sharp/run_now'], hass, msg)
        self.assertEqual(errors[0][1:], ('invalid_request', 'Automation is not active'))
        _, errors = await self.send(handlers['hass_sharp/run_now'], hass, msg)
        self.assertEqual(errors[0][1:], ('invalid_request', 'Trigger not found'))
        env['logger'].exception.assert_not_called()
        _, errors = await self.send(handlers['hass_sharp/run_now'], hass, msg)
        self.assertEqual(errors[0][1], 'run_failed')
        env['logger'].exception.assert_called_once()
        # The same .NET type with a message RunNow does not refuse with is a defect below the dispatch: it is logged
        # with its traceback and answered as an internal error, never shown as a refusal.
        _, errors = await self.send(handlers['hass_sharp/run_now'], hass, msg)
        self.assertEqual(errors[0][1:], ('run_failed', 'Could not dispatch automation; check integration logs'))
        self.assertEqual(env['logger'].exception.call_count, 2)
