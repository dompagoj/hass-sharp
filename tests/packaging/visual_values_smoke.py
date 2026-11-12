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
