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
        nodes = [n for n in register.body if isinstance(n, ast.AsyncFunctionDef) and n.name in ("websocket_run_now", "websocket_run_history")]
        for n in nodes:
            n.decorator_list = []
        env = {"hass_sharp": backend, "json": json, "logger": Mock()}
        exec(compile(ast.Module(body=nodes, type_ignores=[]), "<history>", "exec"), env)
        results, errors = [], []
        connection = types.SimpleNamespace(user=types.SimpleNamespace(is_admin=admin),
            send_result=lambda *args: results.append(args), send_error=lambda *args: errors.append(args))
        async def executor(fn, *args):
            return fn(*args)
        return env, types.SimpleNamespace(async_add_executor_job=executor), connection, results, errors

    async def test_dispatch_and_pagination(self):
        calls = []
        backend = types.SimpleNamespace(
            RunNow=lambda *args: calls.append(args) or '{"version":1,"runId":"run"}',
            GetRunHistory=lambda *args: calls.append(args) or '{"version":1,"runs":[]}')
        env, hass, connection, results, errors = self.handlers(backend)
        await env["websocket_run_now"](hass, connection, {"id":1,"automationId":"a","className":"C","method":"Run"})
        await env["websocket_run_history"](hass, connection, {"id":2,"automationId":"a","offset":10,"limit":5})
        self.assertEqual([("a","C","Run"),("a",10,5)], calls)
        self.assertEqual("run", results[0][1]["runId"])
        self.assertEqual([], errors)

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
