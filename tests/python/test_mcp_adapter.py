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
        self.service.history=AsyncMock(return_value={'version':2,'offset':0,'limit':50,'runs':[]})
        self.service.trace=AsyncMock(return_value=None)
        self.state=self.adapter.EntryState(self.service)
        self.state.ready=True
        self.api=self.adapter.HassSharpAPI(self.hass,self.state)
        self.instance=await self.api.async_get_api_instance(self.context)
    CATALOG=json.loads((ROOT/'custom_components/hass_sharp/visual_node_catalog.json').read_text())
    SERVICES=json.loads((ROOT/'tests/fixtures/visual-services.json').read_text())
    async def library(self,request,is_admin):
        if request['operation']=='services': return {'version':1,'ok':True,'result':self.SERVICES}
        if request['operation']=='service_ports':
            # Shaped like the C# service_ports result; the adapter must pass it through, not re-derive it.
            known=request['service'] in self.SERVICES['items']
            return {'version':1,'ok':True,'result':{'service':request['service'],'known':known,'response':'none' if known else None,'hasTarget':known,
                    'inputs':{'target':{'types':['target','entity_id:light'] if known else ['target','entity_id']}},'outputs':{}}}
        return {'version':1,'ok':True,'result':{'catalog':self.CATALOG,'generation':'g','servicesGeneration':self.SERVICES['servicesGeneration']}}
    async def asyncTearDown(self):
        await self.hass.async_stop()
        self.temp.cleanup()
    async def call(self,name,args,context=None):
        from homeassistant.helpers import llm
        tool=next(t for t in self.instance.tools if t.name=='hass_sharp__'+name)
        return await tool.async_call(self.hass,llm.ToolInput(tool_name=tool.name,tool_args=args),context or self.context)
    async def test_schema_validation_and_annotations(self):
        import probatio
        self.assertEqual(len(self.instance.tools),17)
        for tool in self.instance.tools:
            schema=probatio.to_openapi(tool.parameters,openapi_version='3.1.0')
            self.assertEqual(schema['type'],'object')
            self.assertEqual(tool.annotations.read_only,tool.name.split('__')[1] not in ('save','set_enabled','delete','run','library_save','library_delete'))
        save=next(t for t in self.instance.tools if t.name.endswith('__save'))
        schema=probatio.to_openapi(save.parameters,openapi_version='3.1.0')
        self.assertEqual(set(schema['required']),{'id','graph','expectedRevision'})
        self.assertEqual(schema['properties']['graph']['properties']['nodes']['type'],'array')
        for args in ({'id':'x','expectedRevision':True},{'id':'x','expectedRevision':-1},{'id':'x','expectedRevision':0,'extra':1}):
            self.assertTrue((await self.call('delete',args)).error)
        self.assertTrue((await self.call('history',{'id':'x','limit':101})).error)
        self.service.visual.assert_not_called()
    async def test_set_enabled_and_list_keep_their_shape(self):
        self.service.visual.return_value={'version':1,'ok':True,'result':{'id':'x','enabled':False}}
        result=await self.call('set_enabled',{'id':'x','enabled':False})
        self.assertFalse(result.error,result.data)
        self.service.visual.assert_awaited_with({'operation':'set_enabled','id':'x','enabled':False},is_admin=True)
        for args in ({'id':'x'},{'id':'x','enabled':1},{'id':'x','enabled':'off'}):
            self.assertTrue((await self.call('set_enabled',args)).error)
        self.assertFalse(next(t for t in self.instance.tools if t.name=='hass_sharp__set_enabled').annotations.read_only)
        # list asks for the plain listing (complete documents with enabled), never the automations page's view.
        self.service.visual.return_value={'version':1,'ok':True,'result':[]}
        await self.call('list',{})
        self.service.visual.assert_awaited_with({'operation':'list'},is_admin=True)
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
        graph={'version':5,'name':'Complete',
               'nodes':[{'id':'t','kind':'trigger','config':{'type':'manual'}},{'id':'log','kind':'log','config':{'value':{'kind':'literal','type':'string','value':'safe'}}}],
               'edges':[{'id':'flow','kind':'execution','source':'t','target':'log','port':'next'}]}
        # A model sends a Custom C# node's code; preview and save work its pins out from it.
        self.assertFalse((await self.call('preview',{'graph':graph})).error)
        self.service.visual.assert_awaited_with({'operation':'preview','graph':graph,'deriveSignatures':True},is_admin=True)
        await self.call('save',{'id':'x','expectedRevision':0,'graph':graph})
        self.service.visual.assert_awaited_with({'operation':'save','id':'x','expectedRevision':0,'graph':graph,'deriveSignatures':True},is_admin=True)
        # Removed features (usings, helper members, variables, exposed values) reach the backend validator, whose answer
        # says what to do instead; the tool schema does not refuse them first.
        variable={**graph,'nodes':[graph['nodes'][0],{'id':'log','kind':'log','config':{'value':{'kind':'variable','name':'reading'}}}]}
        exposed={**graph,'nodes':[graph['nodes'][0],{'id':'log','kind':'log','config':{'value':{'kind':'literal','type':'string','value':'x'},'output':'reading'}}]}
        for removed,message in (({**graph,'imports':['System.Linq']},'Usings are no longer part of an automation'),
                                ({**graph,'members':'int X;'},'Helper members are no longer part of an automation'),
                                (variable,'Message reads a variable, and variables are gone'),
                                (exposed,'Steps no longer expose values to C# by name'),
                                ({**graph,'version':4},'Expected graph version 5')):
            answer={'version':1,'ok':True,'result':{'valid':False,'diagnostics':[{'nodeId':None,'message':message,'severity':3}]}}
            self.service.visual.return_value=answer
            result=await self.call('preview',{'graph':removed})
            self.service.visual.assert_awaited_with({'operation':'preview','graph':removed,'deriveSignatures':True},is_admin=True)
            self.assertEqual(result.data,answer)
        self.service.visual.return_value={'version':1,'ok':True,'result':[]}
        # The tool's own arguments stay strict, and a refusal names the argument.
        refused=(await self.call('preview',{'graph':graph,'extra':1})).data['error']
        self.assertEqual(refused['code'],'invalid_request'); self.assertIn("extra",refused['detail'])
        refused=(await self.call('save',{'id':'x','graph':graph})).data['error']
        self.assertEqual(refused['code'],'invalid_request'); self.assertIn('expectedRevision',refused['detail'])
        self.service.node_library.return_value['result']['generation']='catalog-generation'
        result=await self.call('get_capabilities',{})
        self.assertFalse(result.error,result.data)
        self.assertEqual(result.data['result']['graphVersions'],[5])
        self.assertEqual(result.data['result']['examples']['library']['id'],'example')

    async def test_actual_registration_and_mcp_readonly_filter(self):
        from homeassistant.helpers import llm
        from homeassistant.components.mcp_server.server import async_get_api_instance, _format_tool
        self.adapter.register(self.hass,self.state)
        self.assertIn('hass_sharp',[api.id for api in llm.async_get_apis(self.hass)])
        instance=await async_get_api_instance(self.hass,'hass_sharp',self.context,True)
        self.assertEqual(len(instance.tools),11)
        for tool in instance.tools:
            self.assertTrue(_format_tool(tool,None).annotations.read_only_hint)
        cached=self.instance.tools[0]
        self.state.close()
        self.assertNotIn('hass_sharp',[api.id for api in llm.async_get_apis(self.hass)])
        self.assertTrue((await self.call('list',{})).error)
        with self.assertRaises(RuntimeError): self.adapter.register(self.hass,self.state)

    async def test_discovery_filters_before_paging_and_includes_services(self):
        self.hass.states.async_set('sensor.z_match','1',{'friendly_name':'A match'})
        self.hass.states.async_set('sensor.a_other','1')
        self.hass.states.async_set('sensor.b_match','1')
        result=await self.call('discover',{'kind':'entities','query':'match','offset':1,'limit':1})
        self.assertEqual(result.data['result']['total'],2)
        self.assertEqual(result.data['result']['items'][0]['id'],'sensor.z_match')
        self.hass.states.async_set('light.hall','on')
        result=await self.call('discover',{'kind':'entities','domain':'light'})
        self.assertEqual([i['id'] for i in result.data['result']['items']],['light.hall'])
        # Services come from the normalised schemas the validator uses, never from calling a service.
        self.service.node_library.side_effect=self.library
        result=await self.call('discover',{'kind':'services','domain':'light'})
        self.assertFalse(result.error,result.data)
        self.assertEqual([i['id'] for i in result.data['result']['items']],['light.turn_off','light.turn_on'])
        self.assertEqual(set(result.data['result']['items'][0]),{'id','name','description','hasTarget','response'})
        result=await self.call('discover',{'kind':'services','query':'forecast'})
        self.assertEqual(result.data['result']['items'],[{'id':'weather.get_forecasts','name':'get_forecasts','description':'','hasTarget':True,'response':'only'}])
        result=await self.call('discover',{'kind':'services','service':'light.turn_on'})
        fields=result.data['result']['service']['fields']
        self.assertEqual([f['order'] for f in fields.values()],sorted(f['order'] for f in fields.values()))
        self.assertEqual(result.data['result']['service']['target']['entity'][0]['domain'],['light'])
        self.assertTrue(result.data['result']['service']['known'])
        unknown=(await self.call('discover',{'kind':'services','service':'light.missing'})).data['result']['service']
        self.assertEqual(unknown,{'id':'light.missing','known':False,'note':self.adapter.UNKNOWN_SERVICE})
        result=await self.call('discover',{'kind':'services','domain':'light','query':'turn_off'})
        self.assertEqual([i['id'] for i in result.data['result']['items']],['light.turn_off'])
        self.assertEqual(result.data['result']['total'],1)
        self.assertTrue((await self.call('discover',{'kind':'areas','domain':'light'})).error)
        self.assertTrue((await self.call('discover',{'kind':'entities','service':'light.turn_on'})).error)
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

    async def test_node_schema_service_fields_trigger_outputs_and_types(self):
        self.service.node_library.side_effect=self.library
        result=(await self.call('get_node_schema',{'kind':'service','service':'light.turn_on'})).data['result']
        self.assertIn('entity_id',result['parametricTypes'])
        self.service.node_library.assert_awaited_with({'operation':'service_ports','service':'light.turn_on'},is_admin=True)
        self.assertEqual(result['service'],(await self.library({'operation':'service_ports','service':'light.turn_on'},True))['result'])
        unknown=(await self.call('get_node_schema',{'kind':'service','service':'light.missing'})).data['result']['service']
        self.assertEqual((unknown['known'],unknown['note']),(False,self.adapter.UNKNOWN_SERVICE))
        self.assertEqual(unknown['inputs']['target']['types'],['target','entity_id'])
        self.assertFalse(hasattr(self.adapter,'service_target_domains') or hasattr(self.adapter,'FIELD_CONSTRAINTS'))
        trigger=(await self.call('get_node_schema',{'kind':'trigger'})).data['result']
        self.assertEqual(trigger['triggerOutputs']['entity']['entity_id']['refineFrom'],'entity')
        self.assertNotIn('triggerOutputs',(await self.call('get_node_schema',{'kind':'log'})).data['result'])
        self.assertTrue((await self.call('get_node_schema',{'kind':'log','service':'light.turn_on'})).error)
        self.assertTrue((await self.call('get_node_schema',{'service':'light.turn_on'})).error)
        capabilities=(await self.call('get_capabilities',{})).data['result']
        self.assertEqual(set(capabilities['semantics']),{'execution','types','triggers','service','runs','csharp'})
        self.assertIn('Value nodes (catalog pure: true',capabilities['semantics']['execution'])
        self.assertIn('[Node(',capabilities['semantics']['csharp'])
        for removed in ('imports','members','kind: variable','config.output','lastError','entitiesExpression'):
            self.assertNotIn(removed,' '.join(capabilities['semantics'].values()))

    async def test_graph_schema_has_no_node_size(self):
        graph={'version':5,'name':'Sized','nodes':[{'id':'t','kind':'trigger','config':{'type':'manual'},'size':{'width':220,'height':112}}],'edges':[]}
        refused=(await self.call('preview',{'graph':graph})).data['error']
        self.assertEqual(refused['code'],'invalid_request'); self.assertIn('nodes[0].size',refused['detail'])
        self.service.visual.assert_not_called()
        self.service.visual.return_value={'version':1,'ok':True,'result':{'id':'x','revision':2,'graph':graph}}
        result=await self.call('read',{'id':'x'})
        self.assertNotIn('size',result.data['result']['graph']['nodes'][0])

    async def test_history_status_and_trace_views(self):
        self.service.visual.return_value={'version':1,'ok':True,'result':{'id':'x','automationId':'auto'}}
        result=await self.call('history',{'id':'x','status':['Failed','Stopped']})
        self.assertFalse(result.error,result.data)
        self.service.history.assert_awaited_with('auto',0,50,['Failed','Stopped'],is_admin=True)
        self.assertEqual(result.data['result']['version'],2)
        self.assertTrue((await self.call('history',{'id':'x','status':['Bogus']})).error)
        self.assertEqual((await self.call('trace',{'runId':'r1'})).data['error']['code'],'not_found')
        self.service.trace.return_value={'version':1,'run':{'runId':'r1','status':'Failed','hasTrace':True},'live':False,
            'snapshot':{'revision':'abc','visualId':'x','visualRevision':3,'graph':{'nodes':['g']},'catalog':{'nodes':{'big':{}}}},
            'trace':{'format':1,'runId':'r1','rootNodeId':'t','startedUtc':'2026-11-13T00:00:00Z','complete':True,'truncated':False,'dropped':0,
                     'trigger':{'platform':'manual'},
                     'events':[{'seq':0,'node':'t','frame':'','t':0,'ms':0.1,'status':'ok','port':'next','outputs':{'data':{'type':'object','v':{}}}},
                               {'seq':1,'node':'svc','frame':'','t':1,'ms':2,'status':'failed','inputs':{'field:x':{'type':'number','v':1}},'error':{'type':'E','message':'boom'}}],
                     'summary':{'t':{'count':1,'failed':0,'last':'ok'},'svc':{'count':1,'failed':1,'last':'failed'}}}}
        view=(await self.call('trace',{'runId':'r1'})).data['result']
        self.service.trace.assert_awaited_with('r1',is_admin=True)
        self.assertEqual(view['snapshot'],{'revision':'abc','visualId':'x','visualRevision':3})
        self.assertEqual([e['node'] for e in view['trace']['events']],['t','svc'])
        self.assertTrue(all('inputs' not in e and 'outputs' not in e for e in view['trace']['events']))
        self.assertEqual(view['trace']['events'][1]['error']['message'],'boom')
        self.assertNotIn('trigger',view['trace'])
        self.assertEqual(view['trace']['summary']['svc']['failed'],1)
        view=(await self.call('trace',{'runId':'r1','node':'svc','graph':True})).data['result']
        self.assertEqual(view['trace']['events'],[self.service.trace.return_value['trace']['events'][1]])
        self.assertEqual(view['snapshot']['graph'],{'nodes':['g']})
        self.assertNotIn('catalog',json.dumps(view))
        self.assertTrue(next(t for t in self.instance.tools if t.name=='hass_sharp__trace').annotations.read_only)
        self.assertTrue((await self.call('trace',{'runId':'r1','graph':'yes'})).error)
        self.assertEqual((await self.call('trace',{'runId':'r1','node':'never_ran'})).data['error']['code'],'not_found')
        view=(await self.call('trace',{'runId':'r1','offset':1,'limit':1})).data['result']['trace']
        self.assertEqual(([e['node'] for e in view['events']],view['totalEvents'],view['offset'],view['limit']),(['svc'],2,1,1))
        self.assertEqual((await self.call('trace',{'runId':'r1'})).data['result']['trace']['limit'],self.adapter.TRACE_PAGE)
        self.assertEqual((await self.call('trace',{'runId':'r1','node':'svc'})).data['result']['trace']['limit'],self.adapter.TRACE_NODE_PAGE)
        for args in ({'runId':'r1','limit':0},{'runId':'r1','limit':True},{'runId':'r1','offset':-1}):
            self.assertTrue((await self.call('trace',args)).error)

if __name__=='__main__': unittest.main()
