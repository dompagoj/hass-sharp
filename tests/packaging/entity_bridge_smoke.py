"""Real Python.NET entity bridge boundary: a device class from a node library, and a graph of nodes, each become a running
definition. No Home Assistant.

Run in a fresh process against the published library: python tests/packaging/entity_bridge_smoke.py INTEGRATION_DIR [DOTNET_ROOT]
Python callables stand in for Home Assistant's bridge on the four entity delegates (sync, push, remove, prune), and the
sync is answered from Python as the bridge answers it, or the definition never becomes active. A manager runs over a
temporary configuration directory. For each definition, checks that the sync, the pushed states and a command's effect
arrive in Python as str on .NET's threads, and that delete removes the entities. For the class, that a command it
rejects fails with its message. For the graph, that its binary sensor follows a source entity Python answers for (through
PyInterop.Entity, changed through the manager as the bridge reports a change), that a switch's turn on and turn off flows
set its state, and that a command no flow handles fails naming the entity and the command.
Exits non-zero with a message on the first mismatch.
"""
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import threading
import time

root = Path(sys.argv[1]).resolve()
os.environ['DOTNET_SYSTEM_GLOBALIZATION_INVARIANT'] = 'true'
from pythonnet import load
kwargs = {'dotnet_root': sys.argv[2]} if len(sys.argv) > 2 else {}
load('coreclr', runtime_config=str(root / 'dotnet.runtimeconfig.json'), **kwargs)
sys.path.insert(0, str(root / 'out'))
import clr
clr.AddReference('HassSharp')
from HassSharp import HasEntityState, HassSharpManager, PyInterop
from System import Action, Func, Int32, Object, String
from System.Collections.Generic import Dictionary
from System.Threading.Tasks import Task

DEFINITION = 'smoke_plug'
TYPE = 'library:smoke:NodeLib.SmokePlug'
REFUSAL = 'the smoke plug refuses to turn on'
LIBRARY = '''
public class SmokePlug : SharpDevice
{
    readonly SharpSwitch relay;
    readonly SharpSensor power;
    public SmokePlug()
    {
        Manufacturer = "Smoke";
        relay = Add(new SharpSwitch("relay") { OnTurnOn = () => { relay.IsOn = true; return Task.CompletedTask; }, OnTurnOff = () => { relay.IsOn = false; return Task.CompletedTask; } });
        power = Add(new SharpSensor("power") { DeviceClass = "power", Unit = "W" });
        Add(new SharpBinarySensor("problem") { IsOn = false });
        Add(new SharpSwitch("guard") { OnTurnOn = () => throw new InvalidOperationException("''' + REFUSAL + '''") });
    }
    protected override Task Start()
    {
        Every(TimeSpan.FromSeconds(30), () => { power.Value = 42.5; return Task.CompletedTask; });
        return Task.CompletedTask;
    }
}
'''
EXPECTED = [('relay', 'switch'), ('power', 'sensor'), ('problem', 'binary_sensor'), ('guard', 'switch')]

# The graph definition: a binary sensor on while SOURCE is, a switch whose turn on and turn off flows set its state, and a
# switch with only a turn off flow, so that turning it on has no flow.
GRAPH_DEFINITION = 'smoke_graph'
SOURCE = 'binary_sensor.smoke_source'
NO_FLOW = 'Bare has no flow for Turn on'


def literal(type_, value):
    return {'kind': 'literal', 'type': type_, 'value': value}


def node(id_, kind, config):
    return {'id': id_, 'kind': kind, 'config': config}


def data(source, target, target_port):
    return {'id': f'{source}.value:{target}.{target_port}', 'kind': 'data', 'source': source, 'target': target, 'sourcePort': 'value', 'targetPort': target_port}


def next_(source, target):
    return {'id': f'{source}:next:{target}', 'source': source, 'target': target, 'port': 'next'}


GRAPH = {'version': 5, 'name': 'Smoke graph', 'nodes': [
    node('source', 'entity_is', {'entity': literal('entity_id', SOURCE), 'state': literal('string', 'on')}),
    node('motion', 'entity_state', {'entity': 'motion'}),
    node('on', 'entity_command', {'entity': 'relay', 'command': 'turn_on'}),
    node('set_on', 'set_entity_state', {'entity': 'relay', 'is_on': literal('boolean', True)}),
    node('off', 'entity_command', {'entity': 'relay', 'command': 'turn_off'}),
    node('set_off', 'set_entity_state', {'entity': 'relay', 'is_on': literal('boolean', False)}),
    node('bare_off', 'entity_command', {'entity': 'bare', 'command': 'turn_off'}),
    node('bare_set_off', 'set_entity_state', {'entity': 'bare', 'is_on': literal('boolean', False)}),
], 'edges': [data('source', 'motion', 'is_on'), next_('on', 'set_on'), next_('off', 'set_off'), next_('bare_off', 'bare_set_off')]}
GRAPH_ENTITIES = [('motion', 'binary_sensor', 'Motion', []), ('relay', 'switch', 'Relay', ['turn_on', 'turn_off']), ('bare', 'switch', 'Bare', ['turn_on', 'turn_off'])]

lock = threading.Lock()
python_threads = {threading.get_ident()}  # every thread Python started: a delegate called on one of these is a mismatch
calls = {'sync': [], 'push': [], 'remove': [], 'prune': []}  # each: (thread ident, argument)
logs, others = [], []
# The manager's configuration directory: a failure exits at once, so it removes the directory itself.
config = tempfile.mkdtemp(prefix='entity-bridge-smoke-')
# Without the definitions' directory startup prunes nothing (a partly restored .storage); with an empty one it prunes
# every definition's entities, which takes the prune through Python.NET.
os.makedirs(os.path.join(config, '.storage', 'hass_sharp', 'entities'))


def fail(message):
    print('FAIL: ' + message, file=sys.stderr)
    with lock:
        for level, line in logs[-20:]:
            print(f'  [C# {level}] {line}', file=sys.stderr)
    sys.stdout.flush()
    sys.stderr.flush()
    shutil.rmtree(config, ignore_errors=True)
    os._exit(1)  # .NET's timers and threads must not keep a failed smoke alive


def check(condition, message):
    if not condition:
        fail(message)


def record(kind, argument):
    with lock:
        calls[kind].append((threading.get_ident(), argument))


def recorded(kind):
    with lock:
        return list(calls[kind])


def later(callback, *args):
    """Answer .NET from another Python thread, as the bridge answers from Home Assistant's event loop."""
    def answer():
        with lock:
            python_threads.add(threading.get_ident())
        callback(*args)
    threading.Thread(target=answer, daemon=True).start()


def sync(definition_json, success, error):
    record('sync', definition_json)
    try:
        definition = json.loads(definition_json)
        entities = {e['uniqueId']: {'entityId': f"{e['domain']}.{definition['definitionId']}_{e['key']}"} for e in definition['entities']}
        later(success, json.dumps({'entities': entities}))
    except Exception as problem:  # never into .NET's thread
        later(error, 'the smoke cannot read the sync: ' + str(problem))


def remove(definition_id, done, error):
    record('remove', definition_id)
    later(done)


def prune(definition_ids_json, done, error):
    record('prune', definition_ids_json)
    later(done)


def push(batch_json):
    record('push', batch_json)


def log(level, message):
    with lock:
        logs.append((level, message))


def other(name):
    def called(*args):
        with lock:
            others.append(name)
    return called


PyInterop.BeginSyncEntities = Action[String, Action[String], Action[String]](sync)
PyInterop.BeginRemoveEntities = Action[String, Action, Action[String]](remove)
PyInterop.BeginPruneEntities = Action[String, Action, Action[String]](prune)
PyInterop.PushEntityStates = Action[String](push)
PyInterop.Log = Action[Int32, String](log)
PyInterop.LogLevel = 20
sources = {SOURCE: 'off'}  # Home Assistant's states, as the graph's State reads them; under the lock


def entity_state(entity_id, state):
    entity = HasEntityState()
    entity.EntityId, entity.State = entity_id, state
    entity.Domain, entity.ObjectId = entity_id.split('.', 1)
    entity.Attributes = Dictionary[String, Object]()
    return entity


def entity(entity_id):
    with lock:
        state = sources.get(entity_id)
    return None if state is None else entity_state(entity_id, state)


PyInterop.Entity = Func[String, HasEntityState](entity)
# Harmless stand-ins for the rest: a library and a definition use none of them, and Init and unload need them set.
PyInterop.CallService = Action[String, String, String, Action, Action[String]](other('CallService'))
PyInterop.CallServiceWithResponse = Action[String, String, String, Action[String], Action[String]](other('CallServiceWithResponse'))
PyInterop.SubscribeToEntityTracking = Action[String](other('SubscribeToEntityTracking'))
PyInterop.UnSubscribeFromEntityTracking = Action[String](other('UnSubscribeFromEntityTracking'))
PyInterop.UnSubscribeAllFromEntityTracking = Action(other('UnSubscribeAllFromEntityTracking'))


def finish(task, what, seconds=120):
    """Wait for a .NET Task without holding the GIL, so .NET's threads can call back into Python meanwhile."""
    deadline = time.monotonic() + seconds
    while not task.IsCompleted:
        if time.monotonic() > deadline:
            fail(f'{what} did not finish in {seconds} s; possible GIL deadlock')
        time.sleep(0.005)
    return task.GetAwaiter().GetResult()


def until(condition, what, seconds=10):
    deadline = time.monotonic() + seconds
    while not condition():
        if time.monotonic() > deadline:
            fail('timed out waiting: ' + what)
        time.sleep(0.01)


def call(start, what):
    """Make a call into the manager from a .NET pool thread and wait for the Task it returns; its result.

    In Home Assistant Python calls the manager from an executor thread; here a .NET thread makes every call, so a
    delegate the manager calls before its first await arrives on a .NET thread too, and none may come on Python's.
    """
    started = []

    def begin():
        started.append(start())
        return started[0]
    finish(Task.Run(Func[Task](begin)), what)
    return started[0].GetAwaiter().GetResult()


def request(method, body, what):
    answer = call(lambda: method(json.dumps(body)), what)
    check(isinstance(answer, str), f'{what}: the answer is {type(answer).__name__}, not str')
    answer = json.loads(answer)
    check(answer.get('ok') is True, f'{what} failed: {answer}')
    return answer['result']


def arguments(kind):
    """The arguments of every call of a delegate so far, each checked to be str and to have come on a .NET thread."""
    seen = recorded(kind)
    with lock:
        python = set(python_threads)
    for thread, argument in seen:
        check(isinstance(argument, str), f'{kind} got {type(argument).__name__}, not str')
        check(thread not in python, f'{kind} was called on a thread Python started, not on a .NET thread')
    return [argument for _, argument in seen]


def snapshots():
    return [s for batch in arguments('push') for s in json.loads(batch)]


def shows(unique_id, field, value):
    return lambda: any(s['uniqueId'] == unique_id and s['available'] is True and s['values'].get(field) == value for s in snapshots())


def lasts(unique_id, field, value):
    """Whether the last snapshot pushed for the unique id is available with the value."""
    def holds():
        pushed = [s for s in snapshots() if s['uniqueId'] == unique_id]
        return bool(pushed) and pushed[-1]['available'] is True and pushed[-1]['values'].get(field) == value
    return holds


def change(entity_id, state):
    """The source changes in Home Assistant: the bridge hands .NET the new and old state; completes once the runs it started ended."""
    with lock:
        old = sources.get(entity_id)
        sources[entity_id] = state
    call(lambda: manager.OnTrackedEntityChange(entity_id, entity_state(entity_id, state), None if old is None else entity_state(entity_id, old)),
         f'the change of {entity_id} to {state}')


try:
    manager = HassSharpManager(config)
    try:
        call(manager.Init, 'Init')
        check(arguments('prune') == ['[]'], f"Init prunes every definition but none: {recorded('prune')}")

        library = request(manager.NodeLibraryRequest, {'operation': 'save', 'id': 'smoke', 'name': 'Smoke', 'expectedRevision': 0, 'source': LIBRARY}, 'the library save')
        check(library.get('saved') is True, f'the library is not saved: {library}')
        types = request(manager.EntityRequest, {'operation': 'types'}, 'types')
        check(any(t['id'] == TYPE and t['kind'] == 'device' for t in types['types']), f'types does not list {TYPE}: {types}')

        saved = request(manager.EntityRequest, {'operation': 'save', 'id': DEFINITION, 'expectedRevision': 0, 'definition': {
            'name': 'Smoke plug', 'settings': [], 'logic': {'kind': 'class', 'type': TYPE}}}, 'the definition save')
        check(saved.get('active') is True and saved['document']['status'] == 'running', f'the definition does not run: {saved}')
        live = {e['uniqueId']: e['entityId'] for e in saved['document']['liveEntities']}
        check(live == {f'{DEFINITION}:{key}': f'{domain}.{DEFINITION}_{key}' for key, domain in EXPECTED},
              f"the live entities are not the entity ids Python answered: {saved['document']['liveEntities']}")

        synced = arguments('sync')
        check(len(synced) == 1, f'one save synced {len(synced)} times')
        definition = json.loads(synced[0])
        check(definition['definitionId'] == DEFINITION and definition['name'] == 'Smoke plug', f'the sync names another definition: {definition}')
        check(definition['device'] is not None and definition['device']['name'] == 'Smoke plug' and definition['device']['manufacturer'] == 'Smoke',
              f"the sync's device: {definition['device']}")
        described = [(e['uniqueId'], e['key'], e['domain']) for e in definition['entities']]
        check(described == [(f'{DEFINITION}:{key}', key, domain) for key, domain in EXPECTED], f'the sync describes {described}')
        power = definition['entities'][1]
        check(power['deviceClass'] == 'power' and power['options']['unit'] == 'W', f'the power sensor is described as {power}')
        check(definition['entities'][0]['commands'] == ['turn_on', 'turn_off'], f"the relay's commands: {definition['entities'][0]}")

        # Every's first run, and the binary sensor's first state, come in a push from .NET's pump.
        until(shows(f'{DEFINITION}:power', 'value', 42.5), 'the power sensor pushed 42.5')
        until(shows(f'{DEFINITION}:problem', 'is_on', False), 'the binary sensor pushed off')

        call(lambda: manager.EntityCommand(f'{DEFINITION}:relay', 'turn_on', '{}'), 'turn_on')
        until(shows(f'{DEFINITION}:relay', 'is_on', True), 'the relay pushed on after turn_on')
        call(lambda: manager.EntityCommand(f'{DEFINITION}:relay', 'turn_off', '{}'), 'turn_off')
        until(lambda: [s['values'].get('is_on') for s in snapshots() if s['uniqueId'] == f'{DEFINITION}:relay'][-1] is False, 'the relay pushed off after turn_off')

        # A handler that throws faults the command's Task with its own message, which the bridge reads as .Message.
        try:
            call(lambda: manager.EntityCommand(f'{DEFINITION}:guard', 'turn_on', '{}'), 'the rejected turn_on')
        except Exception as error:
            check(getattr(error, 'Message', None) == REFUSAL, f'the rejected command failed with {getattr(error, "Message", error)!r}, not {REFUSAL!r}')
        else:
            fail('a command whose handler throws succeeded')

        deleted = request(manager.EntityRequest, {'operation': 'delete', 'id': DEFINITION, 'expectedRevision': 1}, 'the delete')
        check(deleted == {'deleted': True}, f'the delete answered {deleted}')
        check(arguments('remove') == [DEFINITION], f"delete asked to remove {recorded('remove')}")
        try:
            call(lambda: manager.EntityCommand(f'{DEFINITION}:relay', 'turn_on', '{}'), 'a command after delete')
        except Exception as error:
            check(getattr(error, 'Message', None) == 'The entity is not running', f'a command after delete failed with {error}')
        else:
            fail('a command after delete succeeded')
        check(len(recorded('sync')) == 1, f"syncs after the save: {recorded('sync')}")

        # ---- A definition whose logic is a graph of nodes ----
        saved = request(manager.EntityRequest, {'operation': 'save', 'id': GRAPH_DEFINITION, 'expectedRevision': 0, 'definition': {
            'name': 'Smoke graph', 'device': None, 'settings': [],
            'entities': [{'key': key, 'domain': domain, 'name': name} for key, domain, name, _ in GRAPH_ENTITIES],
            'logic': {'kind': 'graph', 'graph': GRAPH}}}, 'the graph definition save')
        check(saved.get('active') is True and saved['document']['status'] == 'running', f'the graph definition does not run: {saved}')
        # The saved libraries' compiler warnings come along (the class above has some); of the graph, only the one command.
        own = [d.get('code') for d in saved.get('diagnostics') or [] if not str(d.get('nodeId')).startswith('$library:')]
        check(own == ['command.unhandled'], f"the save warns only of the command no flow handles: {saved.get('diagnostics')}")
        live = {e['uniqueId']: e['entityId'] for e in saved['document']['liveEntities']}
        check(live == {f'{GRAPH_DEFINITION}:{key}': f'{domain}.{GRAPH_DEFINITION}_{key}' for key, domain, _, _ in GRAPH_ENTITIES},
              f"the graph's live entities are not the entity ids Python answered: {saved['document']['liveEntities']}")

        synced = arguments('sync')
        check(len(synced) == 2, f'the graph save synced {len(synced) - 1} times')
        definition = json.loads(synced[1])
        check(definition['definitionId'] == GRAPH_DEFINITION and definition['name'] == 'Smoke graph' and definition['device'] is None,
              f'the graph sync: {definition}')
        described = [(e['uniqueId'], e['key'], e['domain'], e['name'], e['commands']) for e in definition['entities']]
        check(described == [(f'{GRAPH_DEFINITION}:{key}', key, domain, name, commands) for key, domain, name, commands in GRAPH_ENTITIES],
              f'the graph sync describes {described}')

        # The State's first run reads the source through PyInterop.Entity; each change of it runs the State again.
        motion = f'{GRAPH_DEFINITION}:motion'
        until(lasts(motion, 'is_on', False), 'the motion sensor pushed off from the source as it starts')
        change(SOURCE, 'on')
        until(lasts(motion, 'is_on', True), 'the motion sensor pushed on after the source turned on')
        change(SOURCE, 'off')
        until(lasts(motion, 'is_on', False), 'the motion sensor pushed off after the source turned off')

        relay = f'{GRAPH_DEFINITION}:relay'
        call(lambda: manager.EntityCommand(relay, 'turn_on', '{}'), 'the graph turn_on')
        until(lasts(relay, 'is_on', True), "the graph relay pushed on after its turn on flow")
        call(lambda: manager.EntityCommand(relay, 'turn_off', '{}'), 'the graph turn_off')
        until(lasts(relay, 'is_on', False), "the graph relay pushed off after its turn off flow")

        try:
            call(lambda: manager.EntityCommand(f'{GRAPH_DEFINITION}:bare', 'turn_on', '{}'), 'the turn_on with no flow')
        except Exception as error:
            check(getattr(error, 'Message', None) == NO_FLOW, f'the command with no flow failed with {getattr(error, "Message", error)!r}, not {NO_FLOW!r}')
        else:
            fail('a command with no flow succeeded')
        call(lambda: manager.EntityCommand(f'{GRAPH_DEFINITION}:bare', 'turn_off', '{}'), "the bare switch's turn_off")
        until(lasts(f'{GRAPH_DEFINITION}:bare', 'is_on', False), 'the bare switch pushed off after its turn off flow')

        deleted = request(manager.EntityRequest, {'operation': 'delete', 'id': GRAPH_DEFINITION, 'expectedRevision': 1}, 'the graph delete')
        check(deleted == {'deleted': True}, f'the graph delete answered {deleted}')
        check(arguments('remove') == [DEFINITION, GRAPH_DEFINITION], f"the graph delete asked to remove {recorded('remove')}")
        try:
            call(lambda: manager.EntityCommand(relay, 'turn_on', '{}'), 'a graph command after delete')
        except Exception as error:
            check(getattr(error, 'Message', None) == 'The entity is not running', f'a graph command after delete failed with {error}')
        else:
            fail('a graph command after delete succeeded')
        check(len(recorded('sync')) == 2, f"syncs after the graph save: {recorded('sync')}")
    finally:
        call(lambda: manager.UnloadAsync(False), 'unload')
    errors = [line for level, line in logs if level >= 40]
    check(not errors, f'.NET logged errors: {errors}')
finally:
    shutil.rmtree(config, ignore_errors=True)

print(f"entity bridge smoke OK ({len(recorded('push'))} pushes; other delegates called: {sorted(set(others)) or 'none'})")
