"""The hass_sharp/entities route's Python side: the Home Assistant triggers of a graph definition are validated on
preview and save, as an automation's are, with deriveSignatures passed on to that preview; the answered definitions
list their entity registry entries and their Trigger steps; run goes to .NET as it is.

.NET, the entity registry and Home Assistant's trigger schemas are fakes; the trigger check is native_triggers.py's
own `async_validate_graph_triggers`, with Home Assistant's normalization replaced.
"""
import ast
import asyncio
import copy
import json
import os
import pathlib
import re
import threading
import unittest
from types import SimpleNamespace as NS

from test_visual import EntryState, load_routes

ROOT = pathlib.Path(os.environ.get('HASS_SHARP_TEST_ROOT', pathlib.Path(__file__).resolve().parents[2]))
DOMAIN = 'hass_sharp'
ENTRY = 'entry1'
STATE_TRIGGER_ERROR = "required key not provided @ data['entity_id']"


def trigger_validation():
    """native_triggers.async_validate_graph_triggers as written, with a normalization that refuses a state trigger
    without entity_id with voluptuous's message, as Home Assistant does, and accepts any other trigger."""
    tree = ast.parse(pathlib.Path('custom_components/hass_sharp/native_triggers.py').read_text())
    tree.body = [n for n in tree.body if isinstance(n, ast.AsyncFunctionDef) and n.name == 'async_validate_graph_triggers']
    validated = []

    async def normalize(hass, configuration):
        validated.append(configuration)
        if configuration.get('trigger') == 'state' and 'entity_id' not in configuration:
            raise ValueError(STATE_TRIGGER_ERROR)
        return configuration
    env = {'_async_normalize': normalize}
    exec(compile(tree, '<native_triggers>', 'exec'), env)
    return NS(async_validate_graph_triggers=env['async_validate_graph_triggers']), validated


def entry(entity_id, unique_id, *, name=None, original_name=None, disabled_by=None, platform=DOMAIN, config_entry_id=ENTRY):
    return NS(entity_id=entity_id, unique_id=unique_id, domain=entity_id.split('.', 1)[0], platform=platform,
              config_entry_id=config_entry_id, name=name, original_name=original_name, disabled_by=disabled_by)


class Registry:
    """The entity registry, as far as er.async_entries_for_config_entry reads it; records each read and its thread."""
    def __init__(self, entries):
        self.entries = entries
        self.reads = []

    def for_config_entry(self, entry_id):
        self.reads.append((entry_id, threading.get_ident()))
        return [e for e in self.entries if e.config_entry_id == entry_id]


def definition(nodes=()):
    """A definition as a request sends it: its entity is an Entity node, before the given nodes."""
    relay = {'id': 'relay', 'kind': 'entity', 'config': {'key': 'relay', 'domain': 'switch', 'name': 'Relay'}}
    return {'name': 'Plug', 'settings': [], 'logic': {'kind': 'graph', 'graph': {'version': 5, 'nodes': [relay, *nodes], 'edges': []}}}


def trigger(node_id, configuration):
    return {'id': node_id, 'kind': 'trigger', 'config': {'type': 'ha', 'trigger': configuration}}


WARNING = {'nodeId': 'set', 'inputId': 'entity', 'severity': 2, 'message': 'Home Assistant has no entity light.gone',
           'code': 'entity.unknown'}


class EntityAuthoringTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.loop_thread = threading.get_ident()
        self.requests = []
        self.dotnet_threads = set()
        self.refuse = None  # an error envelope .NET answers instead, for the next request
        self.refuse_on = None  # the operation of the request self.refuse answers; None: whichever comes next
        self.documents = {}  # what .NET's documents of these ids carry besides its own fields, such as their logic
        self.preview = None  # what .NET's preview answers instead of its valid preview with one warning
        self.registry = Registry([
            entry('switch.porch_plug', 'plug:relay', original_name='Relay'),
            entry('sensor.plug_power', 'plug:power', name='Power use', original_name='Power', disabled_by='user'),
            entry('binary_sensor.plug_online', 'plug:online', disabled_by='integration'),
            # Another definition, whose id starts as this one's does.
            entry('switch.plug2_relay', 'plug2:relay', original_name='Relay'),
            # Another integration's entity with the same unique id prefix, and one of another config entry.
            entry('sensor.other_plug', 'plug:other', platform='other_integration'),
            entry('switch.old_plug', 'plug:legacy', config_entry_id='entry2'),
            # Not a definition's entity: no "<definition id>:".
            entry('sensor.plug', 'plug'),
        ])
        self.native, self.validated = trigger_validation()
        manager = NS(EntityRequest=self.dotnet, VisualRequest=lambda raw: self.fail('an entity request reached VisualRequest'))

        async def run_dotnet_task(hass, method, request):
            # As dotnet_tasks.async_run_dotnet_task: the call runs in Home Assistant's executor, the answer is awaited on the loop.
            return await asyncio.get_running_loop().run_in_executor(None, method, request)
        self.hass = NS(data={'hass_sharp_llm': {'unloading': EntryState(None), ENTRY: EntryState(None, ready=True)}})
        registry = self.registry
        handlers, self.env = load_routes(manager, self.hass,
            dotnet_tasks=NS(async_run_dotnet_task=run_dotnet_task), native_triggers=self.native, DOMAIN=DOMAIN,
            er=NS(async_get=lambda hass: registry if hass is self.hass else None,
                  async_entries_for_config_entry=lambda reg, entry_id: reg.for_config_entry(entry_id)))
        self.route = handlers['hass_sharp/entities']

    def dotnet(self, raw):
        """EntityRequest: answers as EntityApi does, in its shapes."""
        self.dotnet_threads.add(threading.get_ident())
        request = json.loads(raw)
        self.requests.append(request)
        operation = request.get('operation')
        if self.refuse is not None and self.refuse_on in (None, operation):
            refused, self.refuse, self.refuse_on = self.refuse, None, None
            return json.dumps(refused)
        def document(definition_id):
            return {'version': 1, 'id': definition_id, 'revision': 2, 'name': definition_id, 'status': 'off', 'liveEntities': [],
                    **copy.deepcopy(self.documents.get(definition_id, {}))}
        result = {
            'list': lambda: [document('lonely'), document('plug'), document('plug2')],
            'read': lambda: {**document(request['id']), 'source': 'class Plug {}', 'sourceMap': []},
            'preview': lambda: copy.deepcopy(self.preview) if self.preview is not None else {'valid': True, 'diagnostics': [copy.deepcopy(WARNING)], 'source': 'class Plug {}', 'sourceMap': []},
            'save': lambda: {'id': request['id'], 'saved': True, 'document': document(request['id']), 'active': False,
                             'activationError': 'Not active'},
            'set_enabled': lambda: {'id': request['id'], 'enabled': request['enabled'], 'active': False, 'activationError': None},
            'delete': lambda: {'deleted': True},
            'run': lambda: {'version': 1, 'runId': 'run1'},
        }[operation]()
        return json.dumps({'version': 1, 'ok': True, 'result': result})

    async def send(self, request):
        results, errors = [], []
        connection = NS(user=NS(is_admin=True), send_result=lambda *v: results.append(v), send_error=lambda *v: errors.append(v))
        await self.route(self.hass, connection, {'id': 1, 'request': request})
        self.assertEqual(errors, [])
        self.env['logger'].exception.assert_not_called()
        return results[0][1]

    def plug_entities(self):
        return [
            {'uniqueId': 'plug:online', 'entityId': 'binary_sensor.plug_online', 'domain': 'binary_sensor', 'name': None, 'disabled': True},
            {'uniqueId': 'plug:power', 'entityId': 'sensor.plug_power', 'domain': 'sensor', 'name': 'Power use', 'disabled': True},
            {'uniqueId': 'plug:relay', 'entityId': 'switch.porch_plug', 'domain': 'switch', 'name': 'Relay', 'disabled': False},
        ]

    async def test_graph_definition_triggers_are_validated_on_preview_and_save(self):
        invalid = definition(nodes=[trigger('poll', {'trigger': 'state'}),
                                    trigger('tick', {'trigger': 'time_pattern', 'minutes': '/5'}),
                                    {'id': 'set', 'kind': 'set_state', 'config': {}}])
        diagnostic = {'nodeId': 'poll', 'inputId': 'trigger', 'severity': 3,
                      'message': f'Invalid Home Assistant trigger configuration: {STATE_TRIGGER_ERROR}'}
        answer = await self.send({'operation': 'preview', 'definition': invalid})
        # After .NET's own diagnostics, as an automation's preview has them; only the trigger's error makes it invalid.
        self.assertEqual(answer, {'version': 1, 'ok': True, 'result': {
            'valid': False, 'diagnostics': [WARNING, diagnostic], 'source': 'class Plug {}', 'sourceMap': []}})
        self.assertEqual(self.requests, [{'operation': 'preview', 'definition': invalid}])
        self.assertEqual([c['trigger'] for c in self.validated], ['state', 'time_pattern'])

        # A save is refused in the validation form, with the preview as its detail, and .NET never stores it.
        self.requests.clear()
        save = {'operation': 'save', 'id': 'plug', 'expectedRevision': 0, 'enabled': False, 'definition': invalid}
        answer = await self.send(save)
        self.assertEqual(answer, {'version': 1, 'ok': False, 'error': {'code': 'validation', 'detail': {
            'valid': False, 'diagnostics': [WARNING, diagnostic], 'source': 'class Plug {}', 'sourceMap': []}}})
        self.assertEqual([r['operation'] for r in self.requests], ['preview'])

        # Valid triggers: .NET's warnings do not stop the save, which goes to .NET as it was sent.
        self.requests.clear()
        valid = definition(nodes=[trigger('poll', {'trigger': 'state', 'entity_id': 'switch.plug'})])
        save = {**save, 'definition': valid}
        answer = await self.send(save)
        self.assertTrue(answer['ok'])
        self.assertEqual(self.requests, [{'operation': 'preview', 'definition': valid}, save])

        # A preview .NET refuses is answered as it is.
        self.requests.clear()
        self.refuse = {'version': 1, 'ok': False, 'error': {'code': 'invalid_request', 'detail': 'Entity key relay is used twice'}}
        refused = copy.deepcopy(self.refuse)
        self.assertEqual(await self.send(save), refused)
        self.assertEqual([r['operation'] for r in self.requests], ['preview'])

        # A request of another shape, such as a logic of another kind than graph, goes to .NET untouched: .NET answers
        # what is wrong with it.
        self.validated.clear()
        for body in ({'name': 'Plug', 'settings': [], 'logic': {'kind': 'script'}}, None, 'plug', {'logic': None}, {'logic': {'kind': 'graph'}},
                     {'logic': {'kind': 'graph', 'graph': ['not', 'an', 'object']}}):
            for operation in ('preview', 'save'):
                with self.subTest(definition=body, operation=operation):
                    self.requests.clear()
                    request = {'operation': operation, 'id': 'plug', 'expectedRevision': 1,
                               **({} if body is None else {'definition': body})}
                    await self.send(request)
                    self.assertEqual(self.requests, [request])
        self.assertEqual(self.validated, [])

    async def test_a_definition_sent_back_with_its_devices_reaches_dotnet_as_sent(self):
        # A read answer lists the devices its Device nodes declare: a definition sent back with them has its triggers
        # checked as any graph's, and goes to .NET as it was sent (which ignores them), its answer passed on.
        devices = [{'key': 'plug', 'name': 'Hall plug', 'model': 'P1'}]
        body = {**definition(nodes=[trigger('poll', {'trigger': 'state', 'entity_id': 'switch.plug'})]), 'devices': devices}
        self.documents['plug'] = {'version': 3, 'devices': devices, 'entities': [{'key': 'relay', 'domain': 'switch', 'device': 'plug'}]}
        answer = await self.send({'operation': 'preview', 'definition': body})
        self.assertTrue(answer['ok'] and answer['result']['valid'])
        self.assertEqual(self.requests, [{'operation': 'preview', 'definition': body}])
        self.requests.clear()
        save = {'operation': 'save', 'id': 'plug', 'expectedRevision': 2, 'definition': body}
        answer = await self.send(save)
        self.assertTrue(answer['ok'])
        self.assertEqual(self.requests, [{'operation': 'preview', 'definition': body}, save])
        self.assertEqual(answer['result']['document']['devices'], devices)
        self.assertEqual(answer['result']['document']['entities'], [{'key': 'relay', 'domain': 'switch', 'device': 'plug'}])
        self.assertEqual([c['trigger'] for c in self.validated], ['state', 'state'])

    async def test_a_graph_request_listing_entities_has_nothing_else_checked(self):
        # .NET refuses a graph definition sent with entities and checks nothing else; its triggers are not checked either.
        listed = {'code': 'definition.entitiesNotAllowed', 'nodeId': None, 'inputId': 'entities', 'severity': 3,
                  'message': 'Entities are Entity nodes in the graph now: leave entities out and add one Entity node for each'}
        await self.refused_alone(listed, {**definition(nodes=[trigger('poll', {'trigger': 'state'})]), 'entities': []})

    async def test_a_graph_request_sending_a_device_has_nothing_else_checked(self):
        # The same for a graph definition sent with a device: its devices are Device nodes.
        sent = {'code': 'definition.deviceNotAllowed', 'nodeId': None, 'inputId': 'device', 'severity': 3,
                'message': "Devices are Device nodes in the graph now: leave device out, add a Device node and connect its "
                           "Entities pin to each entity's Device pin"}
        await self.refused_alone(sent, {**definition(nodes=[trigger('poll', {'trigger': 'state'})]), 'device': {'model': 'Plug'}})

    async def refused_alone(self, listed, body):
        """A preview and a save of `body`, which .NET refuses with `listed` alone: answered as .NET answered, with no
        trigger checked, and the save never stored."""
        self.preview = {'valid': False, 'diagnostics': [listed], 'source': '', 'sourceMap': []}
        answer = await self.send({'operation': 'preview', 'definition': body})
        self.assertEqual(answer, {'version': 1, 'ok': True, 'result': {'valid': False, 'diagnostics': [listed], 'source': '', 'sourceMap': []}})
        self.assertEqual(self.validated, [])
        # A save is refused the same way, and .NET never stores it.
        self.requests.clear()
        answer = await self.send({'operation': 'save', 'id': 'plug', 'expectedRevision': 0, 'definition': body})
        self.assertEqual(answer['error'], {'code': 'validation', 'detail': {'valid': False, 'diagnostics': [listed], 'source': '', 'sourceMap': []}})
        self.assertEqual([r['operation'] for r in self.requests], ['preview'])
        self.assertEqual(self.validated, [])

    async def test_documents_list_their_registered_entities(self):
        listed = await self.send({'operation': 'list'})
        self.assertEqual({d['id']: d['registeredEntities'] for d in listed['result']}, {
            'lonely': [],
            'plug': self.plug_entities(),
            'plug2': [{'uniqueId': 'plug2:relay', 'entityId': 'switch.plug2_relay', 'domain': 'switch', 'name': 'Relay',
                       'disabled': False}]})
        # The rest of each document as .NET answered it.
        self.assertEqual(listed['result'][1], {'version': 1, 'id': 'plug', 'revision': 2, 'name': 'plug', 'status': 'off',
                                               'liveEntities': [], 'registeredEntities': self.plug_entities(), 'triggers': []})
        # One pass over the serving config entry's entries for the whole answer, on the event loop, never in .NET's thread.
        self.assertEqual(self.registry.reads, [(ENTRY, self.loop_thread)])
        self.assertNotIn(self.loop_thread, self.dotnet_threads)

        self.registry.reads.clear()
        read = await self.send({'operation': 'read', 'id': 'plug'})
        self.assertEqual(read['result']['registeredEntities'], self.plug_entities())
        self.assertEqual(read['result']['source'], 'class Plug {}')
        saved = await self.send({'operation': 'save', 'id': 'plug', 'expectedRevision': 2, 'definition': definition()})
        self.assertEqual(saved['result']['document']['registeredEntities'], self.plug_entities())
        self.assertNotIn('registeredEntities', saved['result'])
        self.assertEqual(self.registry.reads, [(ENTRY, self.loop_thread)] * 2)

        # The home view lists documents too.
        self.registry.reads.clear()
        home = await self.send({'operation': 'list', 'view': 'home', 'todayUtc': '2026-11-18T00:00:00Z'})
        self.assertEqual(home['result'][1]['registeredEntities'], self.plug_entities())

        # A registry entry changed since: the next answer reads it again.
        self.registry.entries[0].name = 'Porch plug'
        self.registry.entries[2].disabled_by = None
        read = await self.send({'operation': 'read', 'id': 'plug'})
        self.assertEqual([(e['name'], e['disabled']) for e in read['result']['registeredEntities']],
                         [(None, False), ('Power use', True), ('Porch plug', False)])

    async def test_derive_signatures_reaches_the_preview(self):
        graph = definition(nodes=[trigger('poll', {'trigger': 'state', 'entity_id': 'switch.plug'})])
        preview = {'operation': 'preview', 'definition': graph, 'deriveSignatures': True}
        await self.send(preview)
        self.assertEqual(self.requests, [preview])
        # A save checks the derived graph's triggers in a preview that derives too, then goes to .NET as it was sent.
        self.requests.clear()
        save = {'operation': 'save', 'id': 'plug', 'expectedRevision': 0, 'definition': graph, 'deriveSignatures': True}
        self.assertTrue((await self.send(save))['ok'])
        self.assertEqual(self.requests, [preview, save])
        # Anything but true derives nothing in the preview.
        self.requests.clear()
        await self.send({**save, 'deriveSignatures': 'yes'})
        self.assertEqual(self.requests[0], {'operation': 'preview', 'definition': graph})

    async def test_documents_list_their_trigger_steps(self):
        graph = {'version': 5, 'edges': [], 'nodes': [
            {'id': 'press', 'kind': 'trigger', 'config': {'type': 'manual'}},
            {'id': 'closed', 'kind': 'trigger', 'config': {'type': 'manual', 'manual': False}},
            {'id': 'set', 'kind': 'set_entity_state', 'config': {'entity': 'relay'}}]}
        self.documents = {'plug': {'logic': {'kind': 'graph', 'graph': graph}, 'currentRevisionActive': True, 'status': 'running'},
                          'plug2': {'logic': {'kind': 'graph', 'graph': {'version': 5, 'nodes': [], 'edges': []}}, 'currentRevisionActive': True}}
        steps = [{'triggerId': 'press', 'method': 'Run_' + 'press'.encode().hex().upper(), 'manual': True, 'runnable': True},
                 {'triggerId': 'closed', 'method': 'Run_' + 'closed'.encode().hex().upper(), 'manual': False, 'runnable': False}]
        listed = await self.send({'operation': 'list'})
        # As an automation's are given; a graph without Trigger steps has none, nor one whose logic is not there.
        self.assertEqual({d['id']: d['triggers'] for d in listed['result']}, {'lonely': [], 'plug': steps, 'plug2': []})
        self.assertEqual((await self.send({'operation': 'read', 'id': 'plug'}))['result']['triggers'], steps)
        saved = await self.send({'operation': 'save', 'id': 'plug', 'expectedRevision': 2, 'definition': definition()})
        self.assertEqual(saved['result']['document']['triggers'], steps)
        self.assertNotIn('triggers', saved['result'])
        self.assertEqual((await self.send({'operation': 'read', 'id': 'plug2'}))['result']['triggers'], [])
        # One whose saved revision runs but has not started, or whose start failed, has none that can be run, as run refuses them.
        for status in ('setup', 'failed'):
            with self.subTest(status=status):
                self.documents['plug']['status'] = status
                self.assertEqual([t['runnable'] for t in (await self.send({'operation': 'read', 'id': 'plug'}))['result']['triggers']], [False, False])
        # Nor one whose saved revision does not run.
        self.documents['plug'].update(status='running', currentRevisionActive=False)
        self.assertEqual([t['runnable'] for t in (await self.send({'operation': 'read', 'id': 'plug'}))['result']['triggers']], [False, False])

    async def test_other_operations_are_passed_through(self):
        for request in ({'operation': 'set_enabled', 'id': 'plug', 'enabled': False},
                        {'operation': 'run', 'id': 'plug', 'triggerId': 'press'},
                        {'operation': 'delete', 'id': 'plug', 'expectedRevision': 2}):
            with self.subTest(operation=request['operation']):
                self.requests.clear()
                answer = await self.send(request)
                self.assertEqual(self.requests, [request])
                self.assertEqual(answer, json.loads(self.dotnet(json.dumps(request))))
        # Error answers of the operations that answer documents are .NET's as they are.
        for request, refusal in (({'operation': 'list', 'view': 'week'}, ('invalid_request', 'Unknown list view; the entity definitions list has the view home')),
                                 ({'operation': 'read', 'id': 'gone'}, ('not_found', 'Entity definition not found')),
                                 ({'operation': 'save', 'id': 'plug', 'expectedRevision': 1, 'definition': definition()},
                                  ('conflict', 'Revision conflict; reload the current document')),
                                 ({'operation': 'rename', 'id': 'plug'}, ('invalid_request', 'Unknown entity operation')),
                                 ({'operation': 'run', 'id': 'plug', 'triggerId': 'set'},
                                  ('invalid_request', 'Only a Trigger step can be run by hand; Set state is not one'))):
            with self.subTest(operation=request['operation']):
                # A graph's save is previewed first: the refusal is the save's own.
                self.requests.clear()
                self.refuse, self.refuse_on = {'version': 1, 'ok': False, 'error': {'code': refusal[0], 'detail': refusal[1]}}, request['operation']
                expected = copy.deepcopy(self.refuse)
                self.assertEqual(await self.send(request), expected)
                self.assertEqual(self.requests[-1], request)
        self.assertEqual(self.registry.reads, [])
        self.assertEqual(self.validated, [])


class GuidanceTests(unittest.TestCase):
    """What an author learns from, the shipped example definitions and the MCP guidance and guides, builds every entity
    of a graph as an Entity node, with its state and commands on it."""

    def test_every_graph_example_declares_its_entities_as_nodes(self):
        # Every example is a graph: each entity is an Entity node, or a C# entity or C# device node whose class makes it.
        # A C# class is written in a node, never picked from a node library as the whole definition.
        examples = json.loads((ROOT / 'custom_components/hass_sharp/authoring_examples.json').read_text())
        graphs = [example['definition'] for example in examples['definitions'] if example['definition']['logic']['kind'] == 'graph']
        self.assertEqual(len(graphs), 6)
        self.assertEqual(len(graphs), len(examples['definitions']))
        self.assertFalse([example['title'] for example in examples['definitions'] if 'library' in example])
        for definition in graphs:
            kinds = [node['kind'] for node in definition['logic']['graph']['nodes']]
            with self.subTest(definition['name']):
                self.assertNotIn('entities', definition)
                self.assertTrue({'entity', 'csharp_entity', 'csharp_device'} & set(kinds))
                self.assertFalse({'entity_state', 'entity_command', 'camera_frame'} & set(kinds))
        csharp = {example['title']: [node['kind'] for node in example['definition']['logic']['graph']['nodes'] if node['kind'].startswith('csharp_')]
                  for example in examples['definitions']}
        self.assertEqual({title: kinds for title, kinds in csharp.items() if kinds},
                         {'Plug in C#': ['csharp_device'], 'Thermometer over HTTP': ['csharp_entity']})

    def test_each_csharp_node_example_takes_its_settings_from_a_setting_or_a_value(self):
        # A C# node's setting pin takes a value typed in or a wire from a Setting step; no secret is typed in an example.
        examples = json.loads((ROOT / 'custom_components/hass_sharp/authoring_examples.json').read_text())
        for example in examples['definitions']:
            definition = example['definition']
            graph = definition['logic']['graph']
            nodes = {node['id']: node for node in graph['nodes']}
            for node in (n for n in graph['nodes'] if n['kind'] in ('csharp_entity', 'csharp_device')):
                with self.subTest(example['title'], node=node['id']):
                    settings = {s['key']: s for s in node['config']['signature']['settings']}
                    wired = {edge['targetPort']: nodes[edge['source']] for edge in graph['edges'] if edge.get('kind') == 'data' and edge['target'] == node['id']}
                    self.assertTrue(wired, 'the example wires a Setting into the node')
                    for pin, source in wired.items():
                        if pin != 'device':
                            self.assertIn(pin, settings)
                            self.assertEqual(source['kind'], 'setting', pin)
                            self.assertIn(source['config']['key'], [s['key'] for s in definition['settings']], pin)
                    for pin, value in node['config'].get('arguments', {}).items():
                        self.assertIn(pin, settings)
                        self.assertNotIn(pin, wired)
                        self.assertFalse(settings[pin]['secret'], f'{pin} is a secret typed in')
                        self.assertEqual(value.get('kind'), 'literal', pin)
            self.assertFalse([s['key'] for s in definition['settings'] if s.get('secret') and s.get('value') is not None])

    def test_no_graph_example_sends_a_device(self):
        # A graph definition's devices are its Device nodes: no graph example sends the definition's own device, and the
        # plug is a Device node whose Entities pin is wired to the Device pin of each of its two entities.
        examples = json.loads((ROOT / 'custom_components/hass_sharp/authoring_examples.json').read_text())
        graphs = {example['title']: example['definition'] for example in examples['definitions'] if example['definition']['logic']['kind'] == 'graph'}
        self.assertEqual(len(graphs), 6)
        for title, definition in graphs.items():
            with self.subTest(title):
                self.assertNotIn('device', definition)
        graph = graphs['Plug over HTTP']['logic']['graph']
        devices = [node for node in graph['nodes'] if node['kind'] == 'device']
        self.assertEqual(len(devices), 1)
        plug = devices[0]
        self.assertEqual(plug['config'], {'key': 'plug', 'name': 'Plug over HTTP', 'model': 'Smart plug', 'suggestedArea': 'Living room'})
        wired = sorted(edge['target'] for edge in graph['edges'] if edge['source'] == plug['id'])
        self.assertEqual(wired, ['power', 'relay'])
        for edge in (edge for edge in graph['edges'] if edge['source'] == plug['id']):
            self.assertEqual((edge.get('kind'), edge.get('sourcePort'), edge.get('targetPort')), ('data', 'entities', 'device'), edge['id'])
        # The plug written in C# is a C# device node: its own device, with no Device node beside it.
        plug = graphs['Plug in C#']['logic']['graph']
        self.assertEqual([node['kind'] for node in plug['nodes'] if node['kind'] in ('device', 'csharp_device')], ['csharp_device'])

    def test_the_guidance_describes_a_device_as_a_node(self):
        # SEMANTICS["definitions"] (read from llm.py's source: importing it needs Home Assistant) and the custom entities
        # guide describe a device as a Device node wired from its Entities output, not as the definition's own.
        tree = ast.parse((ROOT / 'custom_components/hass_sharp/llm.py').read_text())
        semantics = next(ast.literal_eval(node.value) for node in tree.body
                         if isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id == 'SEMANTICS' for t in node.targets))
        definitions = semantics['definitions']
        guide = (ROOT / 'docs/custom-entities.md').read_text()
        for name, text in (('SEMANTICS["definitions"]', definitions), ('docs/custom-entities.md', guide)):
            with self.subTest(name):
                self.assertRegex(text, r'kind\s+`?device`?')
                self.assertRegex(text, r'`?entities`?\s+output')
                self.assertRegex(text, r'`?device`?\s+input')
                self.assertNotIn('Group these entities as a device', text)
        self.assertIn('{kind: data, source: <the Device node>, sourcePort: entities, target: <the Entity node>, targetPort: device}', definitions)
        self.assertIn('The definition you send is {name, settings?, logic}', definitions)
        self.assertNotIn('{name, device?, settings?, logic}', definitions)

    def test_the_guidance_does_not_name_the_removed_steps(self):
        text = ''.join((ROOT / path).read_text() for path in ('custom_components/hass_sharp/llm.py', 'docs/custom-entities.md', 'docs/mcp-authoring.md'))
        # A whole name: Set state's kind, set_entity_state, stays.
        for gone in ('entity_state', 'entity_command', 'camera_frame', 'When commanded'):
            self.assertIsNone(re.search(rf'(?<![A-Za-z0-9_]){gone}(?![A-Za-z0-9_])', text), gone)
        self.assertIn('set_entity_state', text)


if __name__ == '__main__':
    unittest.main()
