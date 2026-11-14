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
        stop_services=Mock(side_effect=lambda:calls.append('services'))
        stop_listener=Mock()
        hass=types.SimpleNamespace(is_stopping=False,data={'hass_sharp_llm':{'entry':state},'hass_sharp_native_triggers':{'entry':bridge},'hass_sharp_services':{'entry':stop_services},'hass_sharp_stop':{'entry':stop_listener}},config_entries=types.SimpleNamespace(async_unload_platforms=AsyncMock(return_value=True)))
        manager=types.SimpleNamespace(UnloadAsync=object())
        env={'utils':types.SimpleNamespace(get_hass_sharp_manager=lambda h:manager), 'dotnet_tasks':types.SimpleNamespace(async_run_dotnet_task=AsyncMock()),'frontend':types.SimpleNamespace(async_remove_panel=Mock()),
             'logger':Mock(),'PANEL_URL':'hass-sharp'}
        exec(compile(ast.Module(body=[fn],type_ignores=[]),'<entry>','exec'),env)
        self.assertTrue(await env['async_unload_entry'](hass,types.SimpleNamespace(entry_id='entry',disabled_by=None)))
        # A reload or removal, not a stop: the runs it cancels record "Automation reloaded".
        env['dotnet_tasks'].async_run_dotnet_task.assert_awaited_once_with(hass,manager.UnloadAsync,False)
        stop_listener.assert_called_once()
        self.assertNotIn('entry',hass.data['hass_sharp_stop'])
        hass.config_entries.async_unload_platforms.assert_awaited_once()
        self.assertIn('unregister',calls)
        self.assertFalse(state.ready)
        bridge.async_shutdown.assert_awaited_once()
        # A reload keeps the panel: removing it sends an open panel to Home Assistant's default dashboard.
        env['frontend'].async_remove_panel.assert_not_called()
        stop_services.assert_called_once()
        self.assertNotIn('entry',hass.data['hass_sharp_services'])
        hass.is_stopping=True
        hass.data['hass_sharp_native_triggers']['entry']=bridge
        env['dotnet_tasks'].async_run_dotnet_task.reset_mock()
        self.assertTrue(await env['async_unload_entry'](hass,types.SimpleNamespace(entry_id='entry',disabled_by=None)))
        env['dotnet_tasks'].async_run_dotnet_task.assert_awaited_once_with(hass,manager.UnloadAsync,True)
        env['frontend'].async_remove_panel.assert_not_called()
        # Turning the entry off removes the panel.
        hass.is_stopping=False
        self.assertTrue(await env['async_unload_entry'](hass,types.SimpleNamespace(entry_id='entry',disabled_by='user')))
        env['frontend'].async_remove_panel.assert_called_once_with(hass,'hass-sharp',warn_if_unknown=False)

    async def test_a_failing_unload_is_logged_and_still_unloads(self):
        # Otherwise Home Assistant marks the entry "failed to unload", and only a restart recovers it.
        tree=ast.parse(pathlib.Path('custom_components/hass_sharp/__init__.py').read_text())
        fn=next(n for n in tree.body if isinstance(n,ast.AsyncFunctionDef) and n.name=='async_unload_entry')
        fn.returns=None
        for a in fn.args.args: a.annotation=None
        bridge=types.SimpleNamespace(close=Mock(),async_shutdown=AsyncMock(side_effect=RuntimeError('Native trigger cleanup failed')))
        hass=types.SimpleNamespace(is_stopping=False,data={'hass_sharp_native_triggers':{'entry':bridge}},
                                   config_entries=types.SimpleNamespace(async_unload_platforms=AsyncMock(return_value=True)))
        env={'utils':types.SimpleNamespace(get_hass_sharp_manager=lambda h:types.SimpleNamespace(UnloadAsync=object())),
             'dotnet_tasks':types.SimpleNamespace(async_run_dotnet_task=AsyncMock(side_effect=RuntimeError('unload failed'))),
             'frontend':types.SimpleNamespace(async_remove_panel=Mock()),'logger':Mock(),'PANEL_URL':'hass-sharp'}
        exec(compile(ast.Module(body=[fn],type_ignores=[]),'<entry>','exec'),env)
        self.assertTrue(await env['async_unload_entry'](hass,types.SimpleNamespace(entry_id='entry',disabled_by='user')))
        self.assertEqual(env['logger'].exception.call_count,2)
        bridge.async_shutdown.assert_awaited_once()
        env['frontend'].async_remove_panel.assert_called_once()

    async def test_removing_the_entry_removes_the_panel(self):
        tree=ast.parse(pathlib.Path('custom_components/hass_sharp/__init__.py').read_text())
        fn=next(n for n in tree.body if isinstance(n,ast.AsyncFunctionDef) and n.name=='async_remove_entry')
        fn.returns=None
        for a in fn.args.args: a.annotation=None
        env={'frontend':types.SimpleNamespace(async_remove_panel=Mock()),'PANEL_URL':'hass-sharp'}
        exec(compile(ast.Module(body=[fn],type_ignores=[]),'<entry>','exec'),env)
        await env['async_remove_entry']('hass',types.SimpleNamespace(entry_id='entry'))
        env['frontend'].async_remove_panel.assert_called_once_with('hass','hass-sharp',warn_if_unknown=False)
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
        names=('async_setup_entry','async_unload_entry','register_panel','on_loop')
        functions=[n for n in tree.body if isinstance(n,(ast.FunctionDef,ast.AsyncFunctionDef)) and n.name in names]
        for fn in functions:
            fn.returns=None
            for a in fn.args.args: a.annotation=None
        tree=RemoveImports().visit(ast.Module(body=functions,type_ignores=[]))
        bridge=types.SimpleNamespace(begin_remove=Mock(),close=Mock(),async_shutdown=AsyncMock())
        manager=types.SimpleNamespace(Init=object(),UnloadAsync=object(),CancelRunsForShutdown=Mock())
        listeners=[]
        def listen(event,handler):
            remove=Mock();listeners.append((event,handler,remove));return remove
        async def executor(fn,*args): return fn(*args)
        import asyncio
        hass=types.SimpleNamespace(data={},is_stopping=False,bus=types.SimpleNamespace(async_listen=listen),async_add_executor_job=executor,
                                   loop=asyncio.get_running_loop(),
                                   config_entries=types.SimpleNamespace(async_forward_entry_setups=AsyncMock(),async_unload_platforms=AsyncMock(return_value=True)))
        state_instances=[]; registration=[]
        def state_factory(service):
            state=State(service);state_instances.append(state);return state
        def register(hass,state):
            self.assertTrue(state.ready);registration.append(state)
        order=[]
        unloads=[]
        def run(hass,method,*args):
            order.append('init' if method is manager.Init else 'other')
            if method is manager.UnloadAsync: unloads.append(args)
        runner=AsyncMock(side_effect=run)
        stops=[]
        def track(hass,target):
            self.assertIs(target,manager);stop=Mock();stops.append(stop);return stop
        async def start(h,m):
            # Listens first, then pushes once (services.async_start).
            stop=track(h,m);order.append('push');return stop
        services=types.SimpleNamespace(async_start=AsyncMock(side_effect=start))
        def add_panel(hass,component,title,icon,visible,url,config,admin):
            hass.data.setdefault('frontend_panels',{})[url]=component
        def remove_panel(hass,url,warn_if_unknown=True):
            hass.data.get('frontend_panels',{}).pop(url,None)
        panel=Mock(side_effect=add_panel);unpanel=Mock(side_effect=remove_panel)
        import threading
        tracked=[]
        def track_state(h,entity_id,action):
            tracked.append((entity_id,threading.current_thread()));return Mock()
        pyinterop=types.SimpleNamespace()
        env=dict(json=json,EVENT_HOMEASSISTANT_STOP='homeassistant_stop',Action=Delegate,Func=Delegate,Int32=int,String=str,Task=object,
                 HasEntityState=object,PyInterop=pyinterop,Event=Delegate,EventStateChangedData=object,
                 callback=lambda f:f,logger=Mock(),python_log=Mock(),unsubs={},PANEL_URL='hass-sharp',
                 async_track_state_change_event=track_state,
                 utils=types.SimpleNamespace(get_hass_sharp_manager=lambda h:manager,get_hass_entities=lambda h:[]),
                 native_triggers=types.SimpleNamespace(NativeTriggerBridge=lambda h:bridge),
                 dotnet_tasks=types.SimpleNamespace(async_run_dotnet_task=runner),
                 frontend=types.SimpleNamespace(async_register_built_in_panel=panel,async_remove_panel=unpanel,DATA_PANELS='frontend_panels'),
                 llm=types.SimpleNamespace(EntryState=state_factory,register=register),AuthoringService=lambda h,m:None,
                 services=services,memory_report=types.SimpleNamespace(enabled=lambda:False))
        exec(compile(tree,'<entry>','exec'),env)
        entry=types.SimpleNamespace(entry_id='test',disabled_by=None)
        self.assertTrue(await env['async_setup_entry'](hass,entry))
        self.assertTrue(state_instances[-1].ready)
        # .NET subscribes from its worker threads; Home Assistant's event helpers run only on the loop.
        worker=threading.Thread(target=lambda:(pyinterop.SubscribeToEntityTracking('binary_sensor.door'),pyinterop.SubscribeToEntityTracking('binary_sensor.door')))
        worker.start();worker.join()
        self.assertEqual(tracked,[])
        await asyncio.sleep(0)
        self.assertEqual(tracked,[('binary_sensor.door',threading.current_thread())])
        unsubscribe=env['unsubs']['binary_sensor.door']
        worker=threading.Thread(target=lambda:pyinterop.UnSubscribeFromEntityTracking('binary_sensor.door'))
        worker.start();worker.join()
        unsubscribe.assert_not_called()
        await asyncio.sleep(0)
        unsubscribe.assert_called_once_with()
        pyinterop.SubscribeToEntityTracking('light.hall');await asyncio.sleep(0)
        worker=threading.Thread(target=pyinterop.UnSubscribeAllFromEntityTracking)
        worker.start();worker.join();await asyncio.sleep(0)
        self.assertEqual(env['unsubs'],{})
        services.async_start.assert_awaited_once_with(hass,manager)
        self.assertEqual(order[:2],['push','init'])
        self.assertIs(hass.data['hass_sharp_services']['test'],stops[-1])
        # A stop cancels the runs with the stopping reason without unloading the entry.
        event,handler,remove=listeners[-1]
        self.assertEqual('homeassistant_stop',event)
        self.assertIs(hass.data['hass_sharp_stop']['test'],remove)
        await handler(None)
        manager.CancelRunsForShutdown.assert_called_once_with()
        self.assertEqual([],unloads)
        await env['async_unload_entry'](hass,entry)
        remove.assert_called_once()
        self.assertNotIn('test',hass.data['hass_sharp_stop'])
        self.assertEqual([(False,)],unloads)
        self.assertTrue(state_instances[-1].closed)
        stops[-1].assert_called_once()
        self.assertNotIn('test',hass.data['hass_sharp_services'])
        # The reload kept the panel, and setup does not register it a second time (Home Assistant would refuse it).
        unpanel.assert_not_called()
        self.assertTrue(await env['async_setup_entry'](hass,entry))
        self.assertEqual(panel.call_count,1)
        self.assertIn('hass-sharp',hass.data['frontend_panels'])
        self.assertEqual(len(registration),2)
        await env['async_unload_entry'](hass,entry)
        async def fail_init(hass,method,*args):
            if method is manager.Init: raise RuntimeError('init failed')
        runner.side_effect=fail_init
        bridge.async_shutdown.side_effect=RuntimeError('cleanup failed')
        with self.assertRaisesRegex(RuntimeError,'cleanup failed'):
            await env['async_setup_entry'](hass,entry)
        # Nothing is behind the panel after a failed setup; one never registered is no warning.
        unpanel.assert_called_once_with(hass,'hass-sharp',warn_if_unknown=False)
        self.assertNotIn('hass-sharp',hass.data['frontend_panels'])
        self.assertTrue(state_instances[-1].closed)
        self.assertNotIn('test',hass.data['hass_sharp_llm'])
        self.assertNotIn('test',hass.data['hass_sharp_native_triggers'])
        self.assertEqual(len(stops),3)
        stops[-1].assert_called_once()
        self.assertEqual(3,len(listeners))
        listeners[-1][2].assert_called_once()
        self.assertNotIn('test',hass.data['hass_sharp_stop'])
        self.assertEqual((False,),unloads[-1])
        self.assertNotIn('test',hass.data['hass_sharp_services'])
        self.assertEqual(hass.config_entries.async_unload_platforms.await_count,3)
        self.assertEqual(bridge.async_shutdown.await_count,3)
