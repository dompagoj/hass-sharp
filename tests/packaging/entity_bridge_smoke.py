"""Real Python.NET entity bridge boundary: a device class from a node library becomes a running definition. No Home Assistant.

Run in a fresh process against the published library: python tests/packaging/entity_bridge_smoke.py INTEGRATION_DIR [DOTNET_ROOT]
Python callables stand in for Home Assistant's bridge on the four entity delegates (sync, push, remove, prune), and the
sync is answered from Python as the bridge answers it, or the definition never becomes active. A manager runs over a
temporary configuration directory. Checks that the sync, the pushed states and a command's effect arrive in Python as
str on .NET's threads, that a command the class rejects fails with its message, and that delete removes the entities.
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
from System import Action, Func, Int32, String
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
# Harmless stand-ins for the rest: a library and a definition use none of them, and Init and unload need them set.
PyInterop.Entity = Func[String, HasEntityState](lambda entity_id: None)
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
    finally:
        call(lambda: manager.UnloadAsync(False), 'unload')
    errors = [line for level, line in logs if level >= 40]
    check(not errors, f'.NET logged errors: {errors}')
finally:
    shutil.rmtree(config, ignore_errors=True)

print(f"entity bridge smoke OK ({len(recorded('push'))} pushes; other delegates called: {sorted(set(others)) or 'none'})")
