"""Exercise real Python.NET attributes on a managed worker thread.

After dotnet build: uv run --python 3.13 --with pythonnet==3.1.0 python
  tests/packaging/visual_values_smoke.py INTEGRATION_DIR/HassSharp/bin/Debug/net10.0/HassSharp.dll
A compatible Python.NET/Python version and .NET 10 runtime are required.
"""
from pathlib import Path
import sys
import time

assembly = Path(sys.argv[1]).resolve()
from pythonnet import load
load("coreclr", runtime_config=str(Path(__file__).resolve().parents[2] / "custom_components/hass_sharp/dotnet.runtimeconfig.json"))
import clr
clr.AddReference(str(assembly))
from HassSharp import VisualValues
from System import Array, Boolean, Double, Func, Object, String, Type
from System.Collections.Generic import Dictionary
from System.Linq.Expressions import Expression, ParameterExpression
from System.Threading.Tasks import Task

attributes = Dictionary[String, Object]()
for key, value in {"int": 42, "float": 21.5, "bool": True, "str": "on", "list": [1, 2], "dict": {"x": 3}, "null": None}.items():
    attributes[key] = value

# Build a managed delegate, not a Python callback. This ensures ConvertValue
# acquires the GIL itself on Task.Run's thread rather than inheriting Python's GIL.
method = clr.GetClrType(VisualValues).GetMethod("ConvertValue")

def convert(key, destination):
    call = Expression.Call(method, Array[Expression]([
        Expression.Constant(attributes[key], clr.GetClrType(Object)),
        Expression.Constant(destination, clr.GetClrType(String)),
    ]))
    invoke = Expression.Lambda[Func[Object]](call, Array[ParameterExpression]([])).Compile()
    task = Task.Run[Object](invoke)
    deadline = time.monotonic() + 10
    while not task.IsCompleted:
        if time.monotonic() > deadline:
            raise TimeoutError("Managed normalization did not finish; possible GIL deadlock")
        time.sleep(0.005)  # release the Python GIL while the worker acquires it
    return task.GetAwaiter().GetResult()

for key, destination, expected in [
    ("int", "number", 42.0), ("int", "string", "42"),
    ("float", "number", 21.5), ("float", "string", "21.5"),
    ("bool", "boolean", True), ("str", "boolean", True),
]:
    assert convert(key, destination) == expected, (key, destination)
for key in ("list", "dict", "null"):
    for destination in ("number", "boolean", "string"):
        try:
            convert(key, destination)
        except Exception as error:
            assert "cannot" in str(error).lower() or "only boolean" in str(error).lower(), str(error)
        else:
            raise AssertionError((key, destination, "must reject non-scalar"))
print("PASS: real Python.NET scalar normalization and collection/null rejection from managed worker threads")

# The bridge's actual attribute dictionary contains PyObject wrappers, including nested
# Python collections. Snapshot construction must detach them on a managed worker.
from HassSharp import EntityStateSnapshot, HasEntityState
from System import DateTimeOffset, Nullable
state = HasEntityState()
state.EntityId = "sensor.test"
state.State = "on"
state.Attributes = attributes
state.LastChanged = 123
state.LastReported = 456
snapshot_method = clr.GetClrType(EntityStateSnapshot).GetMethod("From")
snapshot_call = Expression.Call(snapshot_method, Array[Expression]([
    Expression.Constant(state, clr.GetClrType(HasEntityState)),
    Expression.Constant(None, clr.GetClrType(Nullable[DateTimeOffset])),
]))
snapshot_delegate = Expression.Lambda[Func[EntityStateSnapshot]](snapshot_call, Array[ParameterExpression]([])).Compile()

def finish(task):
    deadline = time.monotonic() + 20
    while not task.IsCompleted:
        if time.monotonic() > deadline:
            raise TimeoutError("Managed worker timed out; possible Python GIL deadlock")
        time.sleep(0.005)
    return task.GetAwaiter().GetResult()

snapshot = finish(Task.Run[EntityStateSnapshot](snapshot_delegate))
assert snapshot.Attributes.GetProperty("int").GetInt64() == 42
assert snapshot.Attributes.GetProperty("list")[1].GetInt32() == 2
assert snapshot.Attributes.GetProperty("dict").GetProperty("x").GetInt32() == 3
assert snapshot.LastUpdated is None
attributes["int"] = 99
assert snapshot.Attributes.GetProperty("int").GetInt64() == 42, "snapshot retains no mutable Python aliases"
attributes["int"] = 42

# Execute the real generated typed Read entity -> custom class -> custom record chain.
import json
import tempfile
from HassSharp import DiagnosticsProvider, NodeLibraryCompiler, NodeLibrarySource, VisualGenerator, CodeCompiler, HassPath, PyInterop
from System.Text.Json.Nodes import JsonNode
from System import Action, Int32

diagnostics = DiagnosticsProvider()
source = '''
public sealed class Box { public EntityStateSnapshot State {get;set;} = null!; }
public sealed record Reading(long Value);
public static class Smoke {
 [Node("Box")] public static Box Box(EntityStateSnapshot state) => new() { State=state };
 [Node("Read")] public static Reading Read(Box box) => new(box.State.Attributes.GetProperty("int").GetInt64());
 [Node("Consume")] public static void Consume(Reading reading) { if(reading.Value!=42) throw new Exception("typed Python snapshot lost"); }
}
'''
library = NodeLibraryCompiler(diagnostics).Preview(Array[NodeLibrarySource]([NodeLibrarySource("smoke", source, 1)]))
assert library.Valid, str(library.Diagnostics)
kinds = {method.MethodName: method.Kind for method in library.Snapshot.Methods.Values}
nodes = [
    {"id": "t", "kind": "trigger", "config": {"type": "manual"}},
    {"id": "entity", "kind": "read_entity", "config": {"entity": {"kind": "literal", "type": "entity_id", "value": "sensor.test"}}},
    *[{"id": name.lower(), "kind": kinds[name], "config": {"arguments": {}}} for name in ("Box", "Read", "Consume")],
]
edges = [{"id": "e" + str(i), "source": a["id"], "target": b["id"]} for i, (a, b) in enumerate(zip(nodes, nodes[1:]))]
for i, (source_id, source_port, target, target_port) in enumerate([
    ("entity", "value", "box", "state"), ("box", "result", "read", "box"), ("read", "result", "consume", "reading")
]):
    edges.append({"id": "d" + str(i), "kind": "data", "source": source_id, "sourcePort": source_port, "target": target, "targetPort": target_port})
graph = JsonNode.Parse(json.dumps({"version": 5, "name": "Python snapshot", "nodes": nodes, "edges": edges}))
preview = VisualGenerator(diagnostics).Preview(graph, library.Snapshot)
assert preview.Valid, str(preview.Diagnostics)
PyInterop.Entity = Func[String, HasEntityState](lambda _: state)
PyInterop.Log = Action[Int32, String](lambda *_: None)
with tempfile.TemporaryDirectory(prefix="visual-pythonnet-") as temporary:
    HassPath.HassConfiguration = temporary
    compiled = finish(CodeCompiler(diagnostics).CompileSingleFile(str(Path(temporary) / "smoke.cs"), preview.Source))
    try:
        from System import Activator
        automation_type = compiled.Assembly.GetType("VisualAutomation")
        instance = Activator.CreateInstance(automation_type)
        call = Expression.Call(Expression.Constant(instance, automation_type), automation_type.GetMethod("Run_74"))
        invoke = Expression.Lambda[Func[Task]](call, Array[ParameterExpression]([])).Compile()
        finish(Task.Run(invoke))
    finally:
        compiled.Unload()
print("PASS: real Python.NET worker snapshot detachment and compiled entity -> custom class -> custom record consumers")
