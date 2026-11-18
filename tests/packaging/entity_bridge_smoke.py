"""Real Python.NET entity bridge boundary: a device class from a node library, and a graph of nodes, each become a running
definition. No Home Assistant.

Run in a fresh process against the published library: python tests/packaging/entity_bridge_smoke.py INTEGRATION_DIR [DOTNET_ROOT]
Python callables stand in for Home Assistant's bridge on the five entity delegates (sync, push, remove, prune, camera snapshot), and the
sync is answered from Python as the bridge answers it, or the definition never becomes active. A manager runs over a
temporary configuration directory. For each definition, checks that the sync, the pushed states and a command's effect
arrive in Python as str on .NET's threads, and that delete removes the entities. For the class, that a command it
rejects fails with its message, and that its number is described with its range, takes a value a command sets and refuses
one beyond it with a message naming it. For the graph, that a dimmable light's turn on flow reads the brightness its
command gives and its turn off flow keeps it, and that its binary sensor follows a source entity Python answers for (through
PyInterop.Entity, asked with a str on .NET's threads, and changed through the manager as the bridge reports a change),
that it subscribes to that source with a str and unsubscribes from it on delete, that a switch's turn on and turn off
flows set its state, that the save warns of the one command no flow handles, naming its entity, that a command no flow
handles fails naming the entity and the command, and that nothing logged at warning or above names the graph.
For pictures, which cross as bytes both ways: a class camera shows a blank picture, which comes out of CameraImage as a
System.Byte[]; another, whose frames take a picture of the first through BeginCameraSnapshot (answered as the bridge
answers, with a System.Byte[] the bridge's own code builds) and draw a rectangle and a label on it, gives a JPEG that
differs from its source, which takes libjpeg-turbo and the embedded font. A camera that only shows its frames keeps its
picture across a save: the request right after it gets the frame the replaced revision showed, which the new one never
shows itself.
Exits non-zero with a message on the first mismatch.
"""
import ast
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
from HassSharp import HasEntityState, HassSharpManager, PyInterop, SharpImage
from System import Action, AppContext, Array, Boolean, Byte, Func, Int32, Object, String
from System.Collections.Generic import Dictionary
from System.Threading.Tasks import Task

DEFINITION = 'smoke_plug'
# The cameras' definitions, and the entity id the smoke's sync answers for the still one.
STILL, LENS = 'smoke_still', 'smoke_lens'
# A camera that shows a green frame as it starts only while the smoke says so (AppContext data), so that a revision saved
# without it shows none of its own.
PUSH, PUSH_SHOWS = 'smoke_push', 'HassSharp.Smoke.PushShows'
STILL_CAMERA = f'camera.{STILL}_main'
TYPE = 'library:smoke:NodeLib.SmokePlug'
REFUSAL = 'the smoke plug refuses to turn on'
LIBRARY = '''
public class SmokePlug : SharpDevice
{
    readonly SharpSwitch relay;
    readonly SharpSensor power;
    readonly SharpNumber target;
    public SmokePlug()
    {
        Manufacturer = "Smoke";
        relay = Add(new SharpSwitch("relay") { OnTurnOn = () => { relay.IsOn = true; return Task.CompletedTask; }, OnTurnOff = () => { relay.IsOn = false; return Task.CompletedTask; } });
        power = Add(new SharpSensor("power") { DeviceClass = "power", Unit = "W" });
        Add(new SharpBinarySensor("problem") { IsOn = false });
        Add(new SharpSwitch("guard") { OnTurnOn = () => throw new InvalidOperationException("''' + REFUSAL + '''") });
        target = Add(new SharpNumber("target", "Target") { Min = 5, Max = 30, Step = 0.5, Unit = "°C", OnSetValue = value => { target.Value = value; return Task.CompletedTask; } });
    }
    protected override Task Start()
    {
        Every(TimeSpan.FromSeconds(30), () => { power.Value = 42.5; return Task.CompletedTask; });
        return Task.CompletedTask;
    }
}
public class SmokeStill : SharpCamera
{
    protected override Task Start() { Show(SharpImage.Blank(64, 48, SharpColor.Blue)); return Task.CompletedTask; }
}
public class SmokePush : SharpCamera
{
    protected override Task Start()
    {
        if (AppContext.GetData("''' + PUSH_SHOWS + '''") is true) Show(SharpImage.Blank(64, 48, SharpColor.Green));
        return Task.CompletedTask;
    }
}
public class SmokeLens : SharpCamera
{
    protected override async Task<SharpImage?> Frame() =>
        (await CameraSnapshot("''' + STILL_CAMERA + '''")).DrawRect(4, 4, 40, 24, SharpColor.Yellow, 3).DrawText("Smoke", 6, 30, SharpColor.White, 14);
}
'''
EXPECTED = [('relay', 'switch'), ('power', 'sensor'), ('problem', 'binary_sensor'), ('guard', 'switch'), ('target', 'number')]
BEYOND = 'Target (target) takes a number from 5 to 30, not 45'

# The graph definition: a binary sensor on while SOURCE is, a switch whose turn on and turn off flows set its state, a
# switch with only a turn off flow, so that turning it on has no flow, and a dimmable light turned on at the brightness its
# command gives.
GRAPH_DEFINITION = 'smoke_graph'
SOURCE = 'binary_sensor.smoke_source'
NO_FLOW = 'Bare has no flow for Turn on'


def literal(type_, value):
    return {'kind': 'literal', 'type': type_, 'value': value}


def node(id_, kind, config):
    return {'id': id_, 'kind': kind, 'config': config}


def data(source, target, target_port, source_port='value'):
    return {'id': f'{source}.{source_port}:{target}.{target_port}', 'kind': 'data', 'source': source, 'target': target, 'sourcePort': source_port, 'targetPort': target_port}


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
    node('lamp_on', 'entity_command', {'entity': 'lamp', 'command': 'turn_on'}),
    node('lamp_bright', 'set_entity_state', {'entity': 'lamp', 'is_on': literal('boolean', True)}),
    node('lamp_off', 'entity_command', {'entity': 'lamp', 'command': 'turn_off'}),
    node('lamp_dark', 'set_entity_state', {'entity': 'lamp', 'is_on': literal('boolean', False)}),
], 'edges': [data('source', 'motion', 'is_on'), next_('on', 'set_on'), next_('off', 'set_off'), next_('bare_off', 'bare_set_off'),
             next_('lamp_on', 'lamp_bright'), next_('lamp_off', 'lamp_dark'), data('lamp_on', 'lamp_bright', 'brightness', 'brightness')]}
# Each: key, domain, name, commands, options.
GRAPH_ENTITIES = [('motion', 'binary_sensor', 'Motion', [], {}), ('relay', 'switch', 'Relay', ['turn_on', 'turn_off'], {}),
                  ('bare', 'switch', 'Bare', ['turn_on', 'turn_off'], {}), ('lamp', 'light', 'Lamp', ['turn_on', 'turn_off'], {'dimmable': True})]

lock = threading.Lock()
# Every thread Python started: a delegate called on one of these is a mismatch. Kept as thread objects, not idents: a
# thread that ended leaves its ident free for a thread .NET starts, which Python sees as a thread of its own (a dummy).
python_threads = {threading.current_thread()}
calls = {'sync': [], 'push': [], 'remove': [], 'prune': [], 'subscribe': [], 'unsubscribe': [], 'entity': [], 'snapshot': []}  # each: (thread, argument)
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
        calls[kind].append((threading.current_thread(), argument))


def recorded(kind):
    with lock:
        return list(calls[kind])


def later(callback, *args):
    """Answer .NET from another Python thread, as the bridge answers from Home Assistant's event loop."""
    def answer():
        with lock:
            python_threads.add(threading.current_thread())
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
    record('entity', entity_id)
    with lock:
        state = sources.get(entity_id)
    return None if state is None else entity_state(entity_id, state)


PyInterop.Entity = Func[String, HasEntityState](entity)


def bridge_function(name):
    """A function of the entity bridge, compiled from its source: the bridge itself imports Home Assistant."""
    tree = ast.parse((root / 'entity_bridge.py').read_text())
    found = [n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == name]
    check(len(found) == 1, f'entity_bridge.py has no function {name}')
    env = {}
    exec(compile(ast.Module(body=found, type_ignores=[]), str(root / 'entity_bridge.py'), 'exec'), env)
    return env[name]


dotnet_bytes = bridge_function('_dotnet_bytes')


def camera_snapshot(definition_id, entity_id, on_bytes, on_error):
    """As the bridge answers: from another thread, with the picture Home Assistant's camera gives; here that is the
    still camera's frame, as CameraImage gives it to Home Assistant."""
    record('snapshot', definition_id)
    record('snapshot', entity_id)

    def answer():
        if entity_id != STILL_CAMERA:
            on_error('Camera not found')
            return
        picture = call(lambda: manager.CameraImage(f'{STILL}:main'), "the still camera's frame for the snapshot")
        on_bytes(dotnet_bytes(bytes(picture)))
    later(answer)


PyInterop.BeginCameraSnapshot = Action[String, String, Action[Array[Byte]], Action[String]](camera_snapshot)
# Harmless stand-ins for the rest: a library and a definition use none of them, and Init and unload need them set.
PyInterop.CallService = Action[String, String, String, Action, Action[String]](other('CallService'))
PyInterop.CallServiceWithResponse = Action[String, String, String, Action[String], Action[String]](other('CallServiceWithResponse'))
PyInterop.SubscribeToEntityTracking = Action[String](lambda entity_id: record('subscribe', entity_id))
PyInterop.UnSubscribeFromEntityTracking = Action[String](lambda entity_id: record('unsubscribe', entity_id))
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
        target = definition['entities'][4]
        check(target['name'] == 'Target' and target['commands'] == ['set_value']
              and target['options'] == {'min': 5, 'max': 30, 'step': 0.5, 'mode': 'auto', 'unit': '°C'}, f'the number is described as {target}')

        # Every's first run, and the binary sensor's first state, come in a push from .NET's pump.
        until(shows(f'{DEFINITION}:power', 'value', 42.5), 'the power sensor pushed 42.5')
        until(shows(f'{DEFINITION}:problem', 'is_on', False), 'the binary sensor pushed off')

        call(lambda: manager.EntityCommand(f'{DEFINITION}:relay', 'turn_on', '{}'), 'turn_on')
        until(shows(f'{DEFINITION}:relay', 'is_on', True), 'the relay pushed on after turn_on')
        call(lambda: manager.EntityCommand(f'{DEFINITION}:relay', 'turn_off', '{}'), 'turn_off')
        until(lambda: [s['values'].get('is_on') for s in snapshots() if s['uniqueId'] == f'{DEFINITION}:relay'][-1] is False, 'the relay pushed off after turn_off')

        call(lambda: manager.EntityCommand(f'{DEFINITION}:target', 'set_value', '{"value": 21.5}'), 'set_value')
        until(shows(f'{DEFINITION}:target', 'value', 21.5), 'the number pushed 21.5 after set_value')
        # A value beyond the range is refused before the handler runs, with a message naming the number and the value.
        try:
            call(lambda: manager.EntityCommand(f'{DEFINITION}:target', 'set_value', '{"value": 45}'), 'the set_value beyond the range')
        except Exception as error:
            check(getattr(error, 'Message', None) == BEYOND, f'the value beyond the range failed with {getattr(error, "Message", error)!r}, not {BEYOND!r}')
        else:
            fail('a value beyond the range was set')
        check(lasts(f'{DEFINITION}:target', 'value', 21.5)(), 'the refused value changed the number')

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
            'entities': [{'key': key, 'domain': domain, 'name': name, **({'options': options} if options else {})}
                         for key, domain, name, _, options in GRAPH_ENTITIES],
            'logic': {'kind': 'graph', 'graph': GRAPH}}}, 'the graph definition save')
        check(saved.get('active') is True and saved['document']['status'] == 'running', f'the graph definition does not run: {saved}')
        # The saved libraries' compiler warnings come along (the class above has some); of the graph, only the one command.
        own = [d for d in saved.get('diagnostics') or [] if not str(d.get('nodeId')).startswith('$library:')]
        check([d.get('code') for d in own] == ['command.unhandled'], f"the save warns only of the command no flow handles: {saved.get('diagnostics')}")
        check('bare' in own[0].get('message', '') and 'Turn on' in own[0]['message'], f"the warning names the bare switch and its command: {own[0]}")
        live = {e['uniqueId']: e['entityId'] for e in saved['document']['liveEntities']}
        check(live == {f'{GRAPH_DEFINITION}:{key}': f'{domain}.{GRAPH_DEFINITION}_{key}' for key, domain, _, _, _ in GRAPH_ENTITIES},
              f"the graph's live entities are not the entity ids Python answered: {saved['document']['liveEntities']}")

        synced = arguments('sync')
        check(len(synced) == 2, f'the graph save synced {len(synced) - 1} times')
        definition = json.loads(synced[1])
        check(definition['definitionId'] == GRAPH_DEFINITION and definition['name'] == 'Smoke graph' and definition['device'] is None,
              f'the graph sync: {definition}')
        described = [(e['uniqueId'], e['key'], e['domain'], e['name'], e['commands']) for e in definition['entities']]
        check(described == [(f'{GRAPH_DEFINITION}:{key}', key, domain, name, commands) for key, domain, name, commands, _ in GRAPH_ENTITIES],
              f'the graph sync describes {described}')
        check(definition['entities'][3]['options'] == {'dimmable': True}, f"the graph's light is described as {definition['entities'][3]}")

        # The State's first run reads the source through PyInterop.Entity; each change of it runs the State again.
        motion = f'{GRAPH_DEFINITION}:motion'
        until(lasts(motion, 'is_on', False), 'the motion sensor pushed off from the source as it starts')
        check(SOURCE in arguments('subscribe'), f"the graph does not follow its source: subscribed to {arguments('subscribe')}")
        check(SOURCE in arguments('entity'), f"the State did not read its source through PyInterop.Entity: asked for {arguments('entity')}")
        change(SOURCE, 'on')
        until(lasts(motion, 'is_on', True), 'the motion sensor pushed on after the source turned on')
        change(SOURCE, 'off')
        until(lasts(motion, 'is_on', False), 'the motion sensor pushed off after the source turned off')

        relay = f'{GRAPH_DEFINITION}:relay'
        call(lambda: manager.EntityCommand(relay, 'turn_on', '{}'), 'the graph turn_on')
        until(lasts(relay, 'is_on', True), "the graph relay pushed on after its turn on flow")
        call(lambda: manager.EntityCommand(relay, 'turn_off', '{}'), 'the graph turn_off')
        until(lasts(relay, 'is_on', False), "the graph relay pushed off after its turn off flow")

        # The light's turn on flow reads the brightness its command gives; turned off, it keeps that brightness.
        lamp = f'{GRAPH_DEFINITION}:lamp'
        call(lambda: manager.EntityCommand(lamp, 'turn_on', '{"brightness": 128}'), "the graph light's turn_on")
        until(lambda: lasts(lamp, 'is_on', True)() and lasts(lamp, 'brightness', 128)(), 'the graph light pushed on at brightness 128')
        call(lambda: manager.EntityCommand(lamp, 'turn_off', '{}'), "the graph light's turn_off")
        until(lambda: lasts(lamp, 'is_on', False)() and lasts(lamp, 'brightness', 128)(), 'the graph light pushed off, keeping brightness 128')

        try:
            call(lambda: manager.EntityCommand(f'{GRAPH_DEFINITION}:bare', 'turn_on', '{}'), 'the turn_on with no flow')
        except Exception as error:
            check(getattr(error, 'Message', None) == NO_FLOW, f'the command with no flow failed with {getattr(error, "Message", error)!r}, not {NO_FLOW!r}')
        else:
            fail('a command with no flow succeeded')
        call(lambda: manager.EntityCommand(f'{GRAPH_DEFINITION}:bare', 'turn_off', '{}'), "the bare switch's turn_off")
        until(lasts(f'{GRAPH_DEFINITION}:bare', 'is_on', False), 'the bare switch pushed off after its turn off flow')

        check(SOURCE not in arguments('unsubscribe'), f"the graph stopped following its source while it runs: {arguments('unsubscribe')}")
        deleted = request(manager.EntityRequest, {'operation': 'delete', 'id': GRAPH_DEFINITION, 'expectedRevision': 1}, 'the graph delete')
        check(deleted == {'deleted': True}, f'the graph delete answered {deleted}')
        until(lambda: SOURCE in arguments('unsubscribe'), f'the graph delete unsubscribes from {SOURCE}')
        check(arguments('remove') == [DEFINITION, GRAPH_DEFINITION], f"the graph delete asked to remove {recorded('remove')}")
        try:
            call(lambda: manager.EntityCommand(relay, 'turn_on', '{}'), 'a graph command after delete')
        except Exception as error:
            check(getattr(error, 'Message', None) == 'The entity is not running', f'a graph command after delete failed with {error}')
        else:
            fail('a graph command after delete succeeded')
        check(len(recorded('sync')) == 2, f"syncs after the graph save: {recorded('sync')}")

        # ---- Pictures: bytes from .NET to Python and back ----
        for definition_id, type_ in ((STILL, 'SmokeStill'), (LENS, 'SmokeLens')):
            saved = request(manager.EntityRequest, {'operation': 'save', 'id': definition_id, 'expectedRevision': 0, 'definition': {
                'name': definition_id, 'settings': [], 'logic': {'kind': 'class', 'type': f'library:smoke:NodeLib.{type_}'}}},
                f'the {definition_id} save')
            check(saved.get('active') is True and saved['document']['status'] == 'running', f'{definition_id} does not run: {saved}')
        live = {e['uniqueId']: e['entityId'] for e in saved['document']['liveEntities']}
        check(live == {f'{LENS}:main': f'camera.{LENS}_main'}, f"the lens's live entities: {saved['document']['liveEntities']}")

        def frame(unique_id, what):
            """A camera's frame through CameraImage, as bytes; retried while its Start has not shown one yet."""
            deadline = time.monotonic() + 10
            while True:
                try:
                    picture = call(lambda: manager.CameraImage(unique_id), what)
                    break
                except Exception as error:
                    if time.monotonic() > deadline:
                        fail(f'{what} failed: {getattr(error, "Message", error)}')
                    time.sleep(0.05)
            check(picture.GetType().FullName == 'System.Byte[]', f'{what} is a {picture.GetType().FullName}, not System.Byte[]')
            return bytes(picture)
        still = frame(f'{STILL}:main', "the still camera's frame")
        check(still[:2] == b'\xff\xd8', f"the still camera's frame is not a JPEG: it starts {still[:4].hex()}")
        check(arguments('snapshot') == [], f"the still camera took a picture: {arguments('snapshot')}")
        lens = frame(f'{LENS}:main', "the lens's frame")
        check(arguments('snapshot') == [LENS, STILL_CAMERA], f"the lens asked for {arguments('snapshot')}, not a picture of {STILL_CAMERA} for {LENS}")
        check(lens[:2] == b'\xff\xd8' and lens[-2:] == b'\xff\xd9', f"the lens's frame is not a JPEG: it starts {lens[:4].hex()}, ends {lens[-2:].hex()}")
        check(lens != still, "the lens's frame is its source unchanged: nothing was drawn")
        drawn = SharpImage.Load(dotnet_bytes(lens))
        check((drawn.Width, drawn.Height) == (64, 48), f"the lens's frame is {drawn.Width}x{drawn.Height}, not 64x48")
        for definition_id in (STILL, LENS):
            deleted = request(manager.EntityRequest, {'operation': 'delete', 'id': definition_id, 'expectedRevision': 1}, f'the {definition_id} delete')
            check(deleted == {'deleted': True}, f'the {definition_id} delete answered {deleted}')

        # ---- A camera that only shows its frames, across a save ----
        def save_push(revision):
            saved = request(manager.EntityRequest, {'operation': 'save', 'id': PUSH, 'expectedRevision': revision, 'definition': {
                'name': PUSH, 'settings': [], 'logic': {'kind': 'class', 'type': 'library:smoke:NodeLib.SmokePush'}}},
                f'the {PUSH} save at revision {revision}')
            check(saved.get('active') is True and saved['document']['status'] == 'running', f'{PUSH} does not run: {saved}')
        AppContext.SetData(PUSH_SHOWS, Boolean(True))
        save_push(0)
        shown = frame(f'{PUSH}:main', "the push camera's frame")
        check(shown[:2] == b'\xff\xd8', f"the push camera's frame is not a JPEG: it starts {shown[:4].hex()}")
        AppContext.SetData(PUSH_SHOWS, None)
        save_push(1)
        try:
            kept = bytes(call(lambda: manager.CameraImage(f'{PUSH}:main'), "the push camera's frame right after the save"))
        except Exception as error:
            fail(f"the push camera's frame right after the save failed: {getattr(error, 'Message', error)}")
        check(kept == shown, "the push camera's frame right after the save is not the one the replaced revision showed")
        deleted = request(manager.EntityRequest, {'operation': 'delete', 'id': PUSH, 'expectedRevision': 2}, f'the {PUSH} delete')
        check(deleted == {'deleted': True}, f'the {PUSH} delete answered {deleted}')
    finally:
        call(lambda: manager.UnloadAsync(False), 'unload')
    errors = [line for level, line in logs if level >= 40]
    check(not errors, f'.NET logged errors: {errors}')
    # The graph's runs all went well: its host warns of nothing.
    graph_warnings = [line for level, line in logs if level >= 30 and ('Smoke graph' in line or GRAPH_DEFINITION in line)]
    check(not graph_warnings, f'.NET warned of the graph definition: {graph_warnings}')
    # Every call of these arrived as str on a .NET thread.
    for kind in ('subscribe', 'unsubscribe', 'entity'):
        arguments(kind)
finally:
    shutil.rmtree(config, ignore_errors=True)

print(f"entity bridge smoke OK ({len(recorded('push'))} pushes; {len(recorded('snapshot')) // 2} picture taken; "
      f"a push camera's frame kept across a save; other delegates called: {sorted(set(others)) or 'none'})")
