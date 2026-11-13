import ast
import copy
import json
import pathlib
import types
import unittest
from unittest.mock import AsyncMock

class AuthoringTests(unittest.IsolatedAsyncioTestCase):
    def service(self):
        tree=ast.parse(pathlib.Path('custom_components/hass_sharp/authoring.py').read_text())
        tree.body=[n for n in tree.body if not isinstance(n,ast.ImportFrom)]
        self.requests=[]
        async def run(hass,method,request):
            self.requests.append(json.loads(request))
            return json.dumps({'version':1,'ok':True,'result':{'document': {'graph':json.loads(request)['graph']}} if json.loads(request)['operation']=='save' else {'valid':True,'diagnostics':[]}})
        self.native=AsyncMock(return_value=[{'nodeId':'t','inputId':'trigger','severity':'error','message':'invalid'}])
        env={'dotnet_tasks':types.SimpleNamespace(async_run_dotnet_task=run),'native_triggers':types.SimpleNamespace(async_validate_graph_triggers=self.native)}
        exec(compile(tree,'<authoring>','exec'),env)
        return env['AuthoringService'](None,types.SimpleNamespace(VisualRequest=object()))
    async def test_native_diagnostics_prevent_save_and_match_preview(self):
        service=self.service()
        graph={'version':5,'name':'safe','nodes':[{'id':'t','kind':'trigger','config':{'type':'ha','trigger':{'trigger':'bad'}}}],'edges':[]}
        preview=await service.visual({'operation':'preview','graph':graph},is_admin=True)
        self.assertFalse(preview['result']['valid'])
        self.assertEqual(preview['result']['diagnostics'][0]['severity'],3)
        result=await service.visual({'operation':'save','id':'safe','expectedRevision':0,'graph':graph},is_admin=True)
        self.assertEqual(result['error']['code'],'validation')
        self.assertFalse(any(r['operation']=='save' for r in self.requests))
    async def test_layout_is_preserved_for_authoritative_managed_store(self):
        service=self.service();self.native.return_value=[]
        graph={'version':5,'name':'safe','nodes':[{'id':'b','kind':'trigger','config':{},'position':{'x':12,'y':34}},{'id':'a','kind':'log','config':{}}],'edges':[]}
        original=copy.deepcopy(graph)
        await service.visual({'operation':'save','id':'safe','expectedRevision':0,'graph':graph},is_admin=True)
        saved=self.requests[-1]['graph']
        self.assertEqual(saved['nodes'][0]['position'],{'x':12,'y':34})
        self.assertNotIn('position',saved['nodes'][1])  # The managed store fills missing layout.
        self.assertEqual(graph,original)
    async def test_snapshot_survives_mutation_during_native_validation_and_warnings_save(self):
        import asyncio
        service=self.service()
        entered=asyncio.Event();release=asyncio.Event()
        async def validate(hass,graph):
            entered.set();await release.wait()
            return [{'nodeId':'t','severity':2,'message':'warning'}]
        self.native.side_effect=validate
        graph={'version':5,'name':'safe','nodes':[{'id':'t','kind':'trigger','config':{'type':'manual'}}],'edges':[]}
        task=asyncio.create_task(service.visual({'operation':'save','id':'safe','expectedRevision':0,'graph':graph},is_admin=True))
        await entered.wait();graph['name']='mutated';graph['nodes'][0]['config']['type']='startup';release.set()
        self.assertTrue((await task)['ok'])
        self.assertEqual(self.requests[-1]['graph']['name'],'safe')
        self.assertEqual(self.requests[-1]['graph']['nodes'][0]['config']['type'],'manual')
