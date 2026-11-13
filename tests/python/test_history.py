import ast
import asyncio
import json
import pathlib
from unittest.mock import Mock
import types
import unittest

class HistoryApi(unittest.IsolatedAsyncioTestCase):
    def handlers(self, backend, admin=True):
        source = pathlib.Path("custom_components/hass_sharp/websocket.py").read_text()
        register = next(n for n in ast.parse(source).body if isinstance(n, ast.FunctionDef) and n.name == "register_websocket_routes")
        nodes = [n for n in register.body if isinstance(n, ast.AsyncFunctionDef) and n.name in ("websocket_run_now", "websocket_run_history", "websocket_run_trace")]
        for n in nodes:
            n.decorator_list = []
        env = {"hass_sharp": backend, "json": json, "logger": Mock()}
        exec(compile(ast.Module(body=nodes, type_ignores=[]), "<history>", "exec"), env)
        results, errors = [], []
        connection = types.SimpleNamespace(user=types.SimpleNamespace(is_admin=admin),
            send_result=lambda *args: results.append(args), send_error=lambda *args: errors.append(args))
        async def executor(fn, *args):
            return fn(*args)
        hass=types.SimpleNamespace(async_add_executor_job=executor)
        service_tree=ast.parse(pathlib.Path("custom_components/hass_sharp/authoring.py").read_text())
        service_tree.body=[n for n in service_tree.body if not isinstance(n,ast.ImportFrom)]
        exec(compile(service_tree,"<authoring>","exec"),env)
        env["authoring"]=env["AuthoringService"](hass,backend)
        return env, hass, connection, results, errors

    async def test_dispatch_and_pagination(self):
        calls = []
        backend = types.SimpleNamespace(
            RunNow=lambda *args: calls.append(args) or '{"version":1,"runId":"run"}',
            GetRunHistory=lambda *args: calls.append(args) or '{"version":2,"runs":[]}')
        env, hass, connection, results, errors = self.handlers(backend)
        await env["websocket_run_now"](hass, connection, {"id":1,"automationId":"a","className":"C","method":"Run"})
        await env["websocket_run_history"](hass, connection, {"id":2,"automationId":"a","offset":10,"limit":5})
        await env["websocket_run_history"](hass, connection, {"id":3,"automationId":"a","offset":0,"limit":5,"status":["Failed","Stopped"]})
        self.assertEqual([("a","C","Run"),("a",10,5,None),("a",0,5,["Failed","Stopped"])], calls)
        self.assertEqual("run", results[0][1]["runId"])
        self.assertEqual(2, results[1][1]["version"])
        self.assertEqual([], errors)

    async def test_run_trace_is_admin_only_and_reports_unknown_runs(self):
        calls = []
        response = {"version": 1, "run": {"runId": "r"}, "live": False, "snapshot": None, "trace": None}
        backend = types.SimpleNamespace(GetRunTrace=lambda run_id: calls.append(run_id) or ("null" if run_id == "missing" else json.dumps(response)))
        env, hass, connection, results, errors = self.handlers(backend, admin=False)
        await env["websocket_run_trace"](hass, connection, {"id":1,"runId":"r"})
        self.assertEqual(([], [], "unauthorized"), (calls, results, errors[0][1]))
        env, hass, connection, results, errors = self.handlers(backend)
        await env["websocket_run_trace"](hass, connection, {"id":2,"runId":"r"})
        await env["websocket_run_trace"](hass, connection, {"id":3,"runId":"missing"})
        self.assertEqual(["r", "missing"], calls)
        self.assertEqual([(2, response)], results)
        self.assertEqual([(3, "not_found", "Run not found")], errors)
        with self.assertRaises(PermissionError):
            await env["authoring"].trace("r", is_admin=False)

    async def test_trace_failure_hides_details(self):
        def failed(*args):
            raise RuntimeError("private database error")
        env, hass, connection, results, errors = self.handlers(types.SimpleNamespace(GetRunTrace=failed))
        await env["websocket_run_trace"](hass, connection, {"id":1,"runId":"r"})
        self.assertEqual([], results)
        self.assertEqual("trace_failed", errors[0][1])
        self.assertNotIn("private database error", errors[0][2])

    def test_schemas_accept_status_filter_and_register_run_trace(self):
        source = pathlib.Path("custom_components/hass_sharp/websocket.py").read_text()
        tree = ast.parse(source)
        body = [n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == "register_websocket_routes"
                or isinstance(n, ast.Assign) and any(getattr(t, "id", None) == "RUN_STATUSES" for t in n.targets)]
        commands = {}
        marker = lambda name: (lambda key, **kwargs: (name, key))
        vol = types.SimpleNamespace(Schema=lambda schema, **kwargs: schema, Required=marker("Required"), Optional=marker("Optional"),
            In=lambda values: ("In", tuple(values)), All=lambda *args: ("All",) + args, Range=lambda **kwargs: ("Range", kwargs),
            Coerce=lambda kind: ("Coerce", kind), ALLOW_EXTRA="extra")
        api = types.SimpleNamespace(decorators=types.SimpleNamespace(async_response=lambda f: f),
            connection=types.SimpleNamespace(ActiveConnection=object),
            async_register_command=lambda hass, command, handler, schema: commands.__setitem__(command, schema))
        env = {"vol": vol, "websocket_api": api, "HomeAssistant": object, "AuthoringService": lambda *args: None, "Any": object}
        exec(compile(ast.Module(body=body, type_ignores=[]), "<websocket>", "exec"), env)
        env["register_websocket_routes"](None, None)
        statuses = ("Pending", "Running", "Succeeded", "Failed", "Cancelled", "Skipped", "Interrupted", "Stopped")
        self.assertEqual([("In", statuses)], commands["hass_sharp/run_history"][("Optional", "status")])
        self.assertEqual({("Required", "id"): int, ("Required", "type"): "hass_sharp/run_trace", ("Required", "runId"): str},
                         commands["hass_sharp/run_trace"])

    async def test_unauthorized_and_storage_failure(self):
        def failed(*args):
            raise RuntimeError("private database error")
        backend = types.SimpleNamespace(RunNow=failed, GetRunHistory=failed)
        for admin in (False, True):
            env, hass, connection, results, errors = self.handlers(backend, admin)
            await env["websocket_run_now"](hass, connection, {"id":1,"automationId":"a","className":"C","method":"Run"})
            self.assertEqual([], results)
            self.assertEqual("run_failed" if admin else "unauthorized", errors[0][1])
            self.assertNotIn("private database error", errors[0][2])
            self.assertEqual(admin, env["logger"].exception.called)
