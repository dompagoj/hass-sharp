"""Run with HA 2026.11 in an isolated process; no user config or devices."""
import asyncio
import importlib.util
import json
import os
import pathlib
import sys
import tempfile
import types
import unittest
from unittest.mock import AsyncMock

ROOT = pathlib.Path(os.environ.get("HASS_SHARP_TEST_ROOT", pathlib.Path(__file__).resolve().parents[2]))
HAS_NEW_HA = importlib.util.find_spec('probatio') is not None

@unittest.skipUnless(HAS_NEW_HA, 'Requires actual HA 2026.11/probatio')
class McpTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        from homeassistant.core import HomeAssistant, Context
        from homeassistant.helpers import llm
        self.temp = tempfile.TemporaryDirectory()
        self.hass = HomeAssistant(self.temp.name)
        self.admin = types.SimpleNamespace(is_admin=True)
        self.hass.auth = types.SimpleNamespace(async_get_user=AsyncMock(return_value=self.admin))
        self.context = llm.LLMContext(platform='test', context=Context(user_id='admin'), language='en', assistant=None, device_id=None)
        parent=types.ModuleType('custom_components'); parent.__path__=[str(ROOT/'custom_components')]; sys.modules.setdefault('custom_components',parent)
        package='custom_components.hass_sharp'
        module=types.ModuleType(package); module.__path__=[str(ROOT/'custom_components/hass_sharp')]
        sys.modules[package]=module
        import importlib
        self.adapter = importlib.import_module(package+'.llm')
        self.service=types.SimpleNamespace(visual=AsyncMock(return_value={'version':1,'ok':True,'result':[]}), node_library=AsyncMock(return_value={'version':1,'ok':True,'result':{'catalog':{'nodes':{},'types':{},'limits':{}}}}))
        self.state=self.adapter.EntryState(self.service)
        self.state.ready=True
        self.api=self.adapter.HassSharpAPI(self.hass,self.state)
        self.instance=await self.api.async_get_api_instance(self.context)
    async def asyncTearDown(self):
        await self.hass.async_stop()
        self.temp.cleanup()
    async def call(self,name,args,context=None):
        from homeassistant.helpers import llm
        tool=next(t for t in self.instance.tools if t.name=='hass_sharp__'+name)
        return await tool.async_call(self.hass,llm.ToolInput(tool_name=tool.name,tool_args=args),context or self.context)
    async def test_schema_validation_and_annotations(self):
        import probatio
        self.assertEqual(len(self.instance.tools),15)
        for tool in self.instance.tools:
            schema=probatio.to_openapi(tool.parameters,openapi_version='3.1.0')
            self.assertEqual(schema['type'],'object')
            self.assertEqual(tool.annotations.read_only,tool.name.split('__')[1] not in ('save','delete','run','library_save','library_delete'))
        save=next(t for t in self.instance.tools if t.name.endswith('__save'))
        schema=probatio.to_openapi(save.parameters,openapi_version='3.1.0')
        self.assertEqual(set(schema['required']),{'id','graph','expectedRevision'})
        self.assertEqual(schema['properties']['graph']['properties']['nodes']['type'],'array')
        for args in ({'id':'x','expectedRevision':True},{'id':'x','expectedRevision':-1},{'id':'x','expectedRevision':0,'extra':1}):
            self.assertTrue((await self.call('delete',args)).error)
        self.assertTrue((await self.call('history',{'id':'x','limit':101})).error)
        self.service.visual.assert_not_called()
    async def test_admin_and_cached_readiness(self):
        self.assertFalse((await self.call('list',{})).error)
        self.admin.is_admin=False
        self.assertTrue((await self.call('list',{})).error)
        with self.assertRaises(PermissionError): await self.api.async_get_api_instance(self.context)
        self.hass.auth.async_get_user.return_value=None
        self.assertTrue((await self.call('list',{})).error)
        self.hass.auth.async_get_user.return_value=types.SimpleNamespace(is_admin=True)
        self.state.ready=False
        self.assertTrue((await self.call('list',{})).error)
        self.assertEqual(self.service.visual.await_count,1)
    async def test_editor_graph_schema_and_capability_examples(self):
        graph={'version':4,'name':'Complete','imports':'using System;',
               'nodes':[{'id':'t','kind':'trigger','config':{'type':'manual'}},{'id':'log','kind':'log','config':{'value':{'kind':'literal','type':'string','value':'safe'}}}],
               'edges':[{'id':'flow','kind':'execution','source':'t','target':'log','port':'next'}]}
        self.assertFalse((await self.call('preview',{'graph':graph})).error)
        self.service.visual.assert_awaited_with({'operation':'preview','graph':graph},is_admin=True)
        self.service.node_library.return_value['result']['generation']='catalog-generation'
        result=await self.call('get_capabilities',{})
        self.assertFalse(result.error,result.data)
        self.assertEqual(result.data['result']['graphVersions'],[4])
        self.assertEqual(result.data['result']['examples']['library']['id'],'example')

    async def test_actual_registration_and_mcp_readonly_filter(self):
        from homeassistant.helpers import llm
        from homeassistant.components.mcp_server.server import async_get_api_instance, _format_tool
        self.adapter.register(self.hass,self.state)
        self.assertIn('hass_sharp',[api.id for api in llm.async_get_apis(self.hass)])
        instance=await async_get_api_instance(self.hass,'hass_sharp',self.context,True)
        self.assertEqual(len(instance.tools),10)
        for tool in instance.tools:
            self.assertTrue(_format_tool(tool,None).annotations.read_only_hint)
        cached=self.instance.tools[0]
        self.state.close()
        self.assertNotIn('hass_sharp',[api.id for api in llm.async_get_apis(self.hass)])
        self.assertTrue((await self.call('list',{})).error)
        with self.assertRaises(RuntimeError): self.adapter.register(self.hass,self.state)

    async def test_discovery_filters_before_paging_and_includes_services(self):
        import voluptuous as vol
        self.hass.states.async_set('sensor.z_match','1',{'friendly_name':'A match'})
        self.hass.states.async_set('sensor.a_other','1')
        self.hass.states.async_set('sensor.b_match','1')
        result=await self.call('discover',{'kind':'entities','query':'match','offset':1,'limit':1})
        self.assertEqual(result.data['result']['total'],2)
        self.assertEqual(result.data['result']['items'][0]['id'],'sensor.z_match')
        async def no_effect(call): raise AssertionError('Discovery must not call services')
        self.hass.services.async_register('test','safe',no_effect,schema=vol.Schema({vol.Required('value'):str}))
        from homeassistant.helpers import service
        service.async_set_service_schema(self.hass,'test','safe',{'name':'Safe','description':'A test service','fields':{'value':{'description':'Input','required':True}},'target':{}})
        result=await self.call('discover',{'kind':'services','query':'test.safe'})
        self.assertEqual(result.data['result']['items'][0]['fields']['value']['description'],'Input')
        from homeassistant.helpers import area_registry, device_registry
        await area_registry.async_get(self.hass).async_load()
        device_registry.async_setup(self.hass)
        await device_registry.async_load(self.hass)
        from homeassistant import loader
        loader.async_setup(self.hass)
        from homeassistant.helpers import trigger
        await trigger.async_setup(self.hass)
        for kind in ('areas','devices','triggers'):
            result=await self.call('discover',{'kind':kind})
            self.assertFalse(result.error,result.data)
        self.assertIn('event',[item['id'] for item in result.data['result']['items']])

    async def test_missing_user_context_and_backend_failure(self):
        from dataclasses import replace
        result=await self.call('list',{},replace(self.context,context=None))
        self.assertEqual(result.data['error']['code'],'unauthorized')
        self.service.visual.side_effect=RuntimeError('/private/secret-source.cs')
        with self.assertLogs('custom_components.HassSharp',level='ERROR'):
            result=await self.call('list',{})
        self.assertEqual(result.data['error']['code'],'internal')
        self.assertNotIn('/private',json.dumps(result.data))

    async def test_conflict_library_and_run_contract(self):
        conflict={'version':1,'ok':False,'error':{'code':'conflict','detail':'reload'}}
        self.service.visual.return_value=conflict
        result=await self.call('delete',{'id':'x','expectedRevision':1})
        self.assertTrue(result.error); self.assertEqual(result.data,conflict)
        await self.call('run',{'id':'x','triggerId':'start'})
        self.service.visual.assert_awaited_with({'operation':'run','id':'x','triggerId':'start'},is_admin=True)
        await self.call('library_save',{'id':'lib','name':'Safe','source':'','expectedRevision':0})
        self.service.node_library.assert_awaited_with({'operation':'save','id':'lib','name':'Safe','source':'','expectedRevision':0},is_admin=True)

if __name__=='__main__': unittest.main()
