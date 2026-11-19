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
import pathlib
import threading
import unittest
from types import SimpleNamespace as NS

from test_visual import EntryState, load_routes

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


def definition(kind='graph', nodes=()):
    logic = {'kind': 'graph', 'graph': {'version': 5, 'nodes': list(nodes), 'edges': []}} if kind == 'graph' \
        else {'kind': 'class', 'className': 'Porch'}
    return {'name': 'Plug', 'entities': [{'key': 'relay', 'domain': 'switch'}], 'settings': [], 'logic': logic}


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
        self.documents = {}  # what .NET's documents of these ids carry besides its own fields, such as their logic
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
        if self.refuse is not None:
            refused, self.refuse = self.refuse, None
            return json.dumps(refused)
        operation = request.get('operation')
        def document(definition_id):
            return {'version': 1, 'id': definition_id, 'revision': 2, 'name': definition_id, 'status': 'off', 'liveEntities': [],
                    **copy.deepcopy(self.documents.get(definition_id, {}))}
        result = {
            'types': lambda: {'types': [], 'generation': 'g1'},
            'list': lambda: [document('lonely'), document('plug'), document('plug2')],
            'read': lambda: {**document(request['id']), 'source': 'class Plug {}', 'sourceMap': []},
            'preview': lambda: {'valid': True, 'diagnostics': [copy.deepcopy(WARNING)], 'source': 'class Plug {}', 'sourceMap': []},
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

        # A class definition, and a request of another shape, go to .NET untouched: .NET answers what is wrong with it.
        self.validated.clear()
        for body in (definition('class'), None, 'plug', {'logic': None}, {'logic': {'kind': 'graph'}},
                     {'logic': {'kind': 'graph', 'graph': ['not', 'an', 'object']}}):
            for operation in ('preview', 'save'):
                with self.subTest(definition=body, operation=operation):
                    self.requests.clear()
                    request = {'operation': operation, 'id': 'plug', 'expectedRevision': 1,
                               **({} if body is None else {'definition': body})}
                    await self.send(request)
                    self.assertEqual(self.requests, [request])
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
        saved = await self.send({'operation': 'save', 'id': 'plug', 'expectedRevision': 2, 'definition': definition('class')})
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
                          'plug2': {'logic': {'kind': 'class', 'type': 'library:shelly:NodeLib.Plug'}, 'currentRevisionActive': True}}
        steps = [{'triggerId': 'press', 'method': 'Run_' + 'press'.encode().hex().upper(), 'manual': True, 'runnable': True},
                 {'triggerId': 'closed', 'method': 'Run_' + 'closed'.encode().hex().upper(), 'manual': False, 'runnable': False}]
        listed = await self.send({'operation': 'list'})
        # As an automation's are given; a class definition has none, nor one whose logic is not there.
        self.assertEqual({d['id']: d['triggers'] for d in listed['result']}, {'lonely': [], 'plug': steps, 'plug2': []})
        self.assertEqual((await self.send({'operation': 'read', 'id': 'plug'}))['result']['triggers'], steps)
        saved = await self.send({'operation': 'save', 'id': 'plug', 'expectedRevision': 2, 'definition': definition('class')})
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
        for request in ({'operation': 'types'}, {'operation': 'set_enabled', 'id': 'plug', 'enabled': False},
                        {'operation': 'run', 'id': 'plug', 'triggerId': 'press'},
                        {'operation': 'delete', 'id': 'plug', 'expectedRevision': 2},
                        {'operation': 'preview', 'definition': definition('class')}):
            with self.subTest(operation=request['operation']):
                self.requests.clear()
                answer = await self.send(request)
                self.assertEqual(self.requests, [request])
                self.assertEqual(answer, json.loads(self.dotnet(json.dumps(request))))
        # Error answers of the operations that answer documents are .NET's as they are.
        for request, refusal in (({'operation': 'list', 'view': 'week'}, ('invalid_request', 'Unknown list view; the entity definitions list has the view home')),
                                 ({'operation': 'read', 'id': 'gone'}, ('not_found', 'Entity definition not found')),
                                 ({'operation': 'save', 'id': 'plug', 'expectedRevision': 1, 'definition': definition('class')},
                                  ('conflict', 'Revision conflict; reload the current document')),
                                 ({'operation': 'rename', 'id': 'plug'}, ('invalid_request', 'Unknown entity operation')),
                                 ({'operation': 'run', 'id': 'plug', 'triggerId': 'set'},
                                  ('invalid_request', 'Only a Trigger step can be run by hand; Set state is not one'))):
            with self.subTest(operation=request['operation']):
                self.refuse = {'version': 1, 'ok': False, 'error': {'code': refusal[0], 'detail': refusal[1]}}
                expected = copy.deepcopy(self.refuse)
                self.assertEqual(await self.send(request), expected)
        self.assertEqual(self.registry.reads, [])
        self.assertEqual(self.validated, [])


if __name__ == '__main__':
    unittest.main()
