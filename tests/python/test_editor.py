import ast
import pathlib
import types
import unittest
from unittest.mock import Mock

class EditorPermissions(unittest.IsolatedAsyncioTestCase):
    async def test_all_editor_routes(self):
        tree = ast.parse(pathlib.Path("custom_components/hass_sharp/websocket.py").read_text())
        register = next(n for n in tree.body if isinstance(n, ast.FunctionDef))
        names = ["reload_entities", "get_hover", "get_diagnostics", "get_completions", "format_source"]
        for name in names:
            for admin in (False, True):
                for fail in (False, True):
                    with self.subTest(route=name, admin=admin, fail=fail):
                        node = next(n for n in register.body if isinstance(n, ast.AsyncFunctionDef) and n.name == "websocket_" + name)
                        node.decorator_list = []
                        for arg in node.args.args: arg.annotation = None
                        calls = []
                        def backend(*args):
                            calls.append(args)
                            if fail: raise RuntimeError("private error")
                            return []
                        async def execute(fn, *args): return fn(*args)
                        async def dotnet(hass, fn, *args): return fn(*args)
                        env = {"hass_sharp": types.SimpleNamespace(**{n: backend for n in ["GenerateHassEntities", "GetHoverDiagnostics", "GetCompilationDiagnostics", "GetCodeCompletions", "FormatCode"]}),
                               "dotnet_tasks": types.SimpleNamespace(async_run_dotnet_task=dotnet),
                               "utils": types.SimpleNamespace(get_hass_entities=lambda _: []), "logger": Mock()}
                        exec(compile(ast.Module(body=[node], type_ignores=[]), "<editor>", "exec"), env)
                        conn = types.SimpleNamespace(user=types.SimpleNamespace(is_admin=admin), send_result=Mock(), send_error=Mock())
                        await env[node.name](types.SimpleNamespace(async_add_executor_job=execute), conn, {"id": 1, "source": "x", "position": 0})
                        self.assertEqual(len(calls), int(admin))
                        if admin and not fail: conn.send_result.assert_called_once()
                        else:
                            conn.send_error.assert_called_once()
                            self.assertNotIn("private error", str(conn.send_error.call_args))
                            if not admin: self.assertEqual(conn.send_error.call_args.args[1], "unauthorized")
