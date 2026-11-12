import ast
import pathlib
import unittest

class Subscriptions(unittest.TestCase):
    def test_unload_clears_registry_and_duplicate_unsubscribe_is_safe(self):
        source = pathlib.Path('custom_components/hass_sharp/__init__.py').read_text()
        setup = next(n for n in ast.parse(source).body if isinstance(n, ast.AsyncFunctionDef) and n.name == 'async_setup_entry')
        nodes = [n for n in setup.body if isinstance(n, ast.FunctionDef) and n.name in ('unsub_from_entity', 'unbsub_all')]
        calls = []
        env = {'unsubs': {'sensor.test': lambda: calls.append('unsub')}}
        exec(compile(ast.Module(body=nodes, type_ignores=[]), '<subscriptions>', 'exec'), env)
        env['unbsub_all']()
        self.assertEqual({}, env['unsubs'])
        env['unsub_from_entity']('sensor.test')
        self.assertEqual(['unsub'], calls)

if __name__ == '__main__': unittest.main()
