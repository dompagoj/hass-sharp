"""HA-shaped entry teardown without loading Python.NET into the test process."""
import ast
import pathlib
import types
import unittest
from unittest.mock import AsyncMock, Mock

class EntryLifecycleTests(unittest.IsolatedAsyncioTestCase):
    async def test_unload_unloads_platform_invalidates_tools_and_removes_panel(self):
        tree=ast.parse(pathlib.Path('custom_components/hass_sharp/__init__.py').read_text())
        fn=next(n for n in tree.body if isinstance(n,ast.AsyncFunctionDef) and n.name=='async_unload_entry')
        fn.returns=None
        for a in fn.args.args: a.annotation=None
        calls=[]
        state=types.SimpleNamespace(ready=True,close=lambda:calls.append('unregister'))
        bridge=types.SimpleNamespace(close=lambda:calls.append('close'),async_shutdown=AsyncMock())
        hass=types.SimpleNamespace(data={'hass_sharp_llm':{'entry':state},'hass_sharp_native_triggers':{'entry':bridge}},config_entries=types.SimpleNamespace(async_unload_platforms=AsyncMock(return_value=True)))
        env={'utils':types.SimpleNamespace(get_hass_sharp_manager=lambda h:types.SimpleNamespace(UnloadAsync=object())), 'dotnet_tasks':types.SimpleNamespace(async_run_dotnet_task=AsyncMock()),'frontend':types.SimpleNamespace(async_remove_panel=Mock())}
        exec(compile(ast.Module(body=[fn],type_ignores=[]),'<entry>','exec'),env)
        self.assertTrue(await env['async_unload_entry'](hass,types.SimpleNamespace(entry_id='entry')))
        hass.config_entries.async_unload_platforms.assert_awaited_once()
        self.assertIn('unregister',calls)
        self.assertFalse(state.ready)
        bridge.async_shutdown.assert_awaited_once()
        env['frontend'].async_remove_panel.assert_called_once()
    def test_panel_registration_is_entry_owned_and_minimum_guard_precedes_runtime(self):
        tree=ast.parse(pathlib.Path('custom_components/hass_sharp/__init__.py').read_text())
        setup=next(n for n in tree.body if isinstance(n,ast.AsyncFunctionDef) and n.name=='async_setup')
        entry=next(n for n in tree.body if isinstance(n,ast.AsyncFunctionDef) and n.name=='async_setup_entry')
        self.assertIn('register_panel',ast.unparse(entry))
        self.assertNotIn('async_register_built_in_panel',ast.unparse(setup))
        self.assertLess(ast.unparse(setup).index('AwesomeVersion'),ast.unparse(setup).index('initialize_dotnet_runtime'))
    async def test_setup_reload_and_failed_init_cleanup(self):
        import json
        class Delegate:
            def __class_getitem__(cls,key): return cls
            def __new__(cls,value=None): return value
        class State:
            def __init__(self,service): self.ready=False;self.closed=False
            def close(self): self.ready=False;self.closed=True
        class RemoveImports(ast.NodeTransformer):
            def visit_ImportFrom(self,node): return None
        tree=ast.parse(pathlib.Path('custom_components/hass_sharp/__init__.py').read_text())
        names=('async_setup_entry','async_unload_entry','register_panel')
        functions=[n for n in tree.body if isinstance(n,(ast.FunctionDef,ast.AsyncFunctionDef)) and n.name in names]
        for fn in functions:
            fn.returns=None
            for a in fn.args.args: a.annotation=None
        tree=RemoveImports().visit(ast.Module(body=functions,type_ignores=[]))
        bridge=types.SimpleNamespace(begin_remove=Mock(),close=Mock(),async_shutdown=AsyncMock())
        manager=types.SimpleNamespace(Init=object(),UnloadAsync=object())
        hass=types.SimpleNamespace(data={},config_entries=types.SimpleNamespace(async_forward_entry_setups=AsyncMock(),async_unload_platforms=AsyncMock(return_value=True)))
        state_instances=[]; registration=[]
        def state_factory(service):
            state=State(service);state_instances.append(state);return state
        def register(hass,state):
            self.assertTrue(state.ready);registration.append(state)
        runner=AsyncMock()
        panel=Mock();remove=Mock()
        env=dict(json=json,Action=Delegate,Func=Delegate,Int32=int,String=str,Task=object,
                 HasEntityState=object,PyInterop=types.SimpleNamespace(),Event=Delegate,EventStateChangedData=object,
                 callback=lambda f:f,logger=Mock(),python_log=Mock(),unsubs={},
                 utils=types.SimpleNamespace(get_hass_sharp_manager=lambda h:manager,get_hass_entities=lambda h:[]),
                 native_triggers=types.SimpleNamespace(NativeTriggerBridge=lambda h:bridge),
                 dotnet_tasks=types.SimpleNamespace(async_run_dotnet_task=runner),
                 frontend=types.SimpleNamespace(async_register_built_in_panel=panel,async_remove_panel=remove),
                 llm=types.SimpleNamespace(EntryState=state_factory,register=register),AuthoringService=lambda h,m:None)
        exec(compile(tree,'<entry>','exec'),env)
        entry=types.SimpleNamespace(entry_id='test')
        self.assertTrue(await env['async_setup_entry'](hass,entry))
        self.assertTrue(state_instances[-1].ready)
        await env['async_unload_entry'](hass,entry)
        self.assertTrue(state_instances[-1].closed)
        self.assertTrue(await env['async_setup_entry'](hass,entry))
        self.assertEqual(panel.call_count,2)
        self.assertEqual(len(registration),2)
        await env['async_unload_entry'](hass,entry)
        async def fail_init(hass,method,*args):
            if method is manager.Init: raise RuntimeError('init failed')
        runner.side_effect=fail_init
        bridge.async_shutdown.side_effect=RuntimeError('cleanup failed')
        with self.assertRaisesRegex(RuntimeError,'cleanup failed'):
            await env['async_setup_entry'](hass,entry)
        self.assertTrue(state_instances[-1].closed)
        self.assertNotIn('test',hass.data['hass_sharp_llm'])
        self.assertNotIn('test',hass.data['hass_sharp_native_triggers'])
        self.assertEqual(hass.config_entries.async_unload_platforms.await_count,3)
        self.assertEqual(bridge.async_shutdown.await_count,3)
