import ast
import pathlib
import types
import unittest


class ScriptHttpValidation(unittest.IsolatedAsyncioTestCase):
    async def test_open_and_save_reject_invalid_names_before_backend_access(self):
        source = pathlib.Path('custom_components/hass_sharp/http.py').read_text()
        view = next(n for n in ast.parse(source).body if isinstance(n, ast.ClassDef) and n.name == 'AutomationByIdView')
        for operation in ('get', 'post'):
            handler = next(n for n in view.body if isinstance(n, ast.AsyncFunctionDef) and n.name == operation)
            handler.decorator_list = []
            handler.returns = None
            for arg in handler.args.args:
                arg.annotation = None
            env = {}
            exec(compile(ast.Module(body=[handler], type_ignores=[]), '<script-http>', 'exec'), env)
            async def forbidden(*args):
                self.fail("invalid file name reached request/backend processing")
            instance = types.SimpleNamespace(json=lambda body, status: (body, status),
                hass=types.SimpleNamespace(async_add_executor_job=forbidden),
                hass_sharp=types.SimpleNamespace(GetUserScript=lambda _: None))
            request = types.SimpleNamespace(json=forbidden)
            for name in ('', '.', '..', '../outside', 'folder\\name', 'bad\0name'):
                with self.subTest(operation=operation, name=name):
                    body, status = await env[operation](instance, request, name)
                    self.assertEqual(status, 400)
                    self.assertEqual(body, {'error': 'Invalid automation file name'})
