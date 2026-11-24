# Writing C# nodes

C# enters a visual automation in one way: a public static method marked `[Node]`.
The method's parameters are the node's input pins and its return value is its output
pin. Write such a method in either of two places:

- **A Custom C# node** in one automation. Its code is one static class with exactly
  one `[Node]` method. Helper methods and types can sit beside it, and `using` lines
  go at the top. The node shows the method's pins, title and description.
- **A node library**, for methods several automations share. Each `[Node]` method
  in a library becomes a node kind in the catalog.

## Node methods

```csharp
public static class CustomNode
{
    /// <summary>Greets someone by name and writes the greeting to the log.</summary>
    /// <param name="name">Who to greet.</param>
    /// <returns>The greeting.</returns>
    [Node("Greeting")]
    public static string Greet(string name, NodeContext context)
    {
        var greeting = $"Hello, {name}";
        context.Log(greeting);
        return greeting;
    }
}
```

- **Title and description.** `[Node("Title")]` names the node. The XML `<summary>`
  is its description, each `<param>` describes an input and `<returns>` describes
  the output. For a tuple, `<returns>` describes the first element's output; the
  others have no description. In a node library, a `[Node]` method without a doc
  comment gets a warning; other types and members, and Custom C# code, get none.
- **Inputs.** The parameter name is the pin's id. Its label is the name in words
  (`roomName` becomes "Room name"), or the text of `[Port("Label")]` on the
  parameter. Optional parameters keep their C# defaults.
- **Outputs.** A plain return value is the output `result`, labelled "Result" or by
  `[return: Port("Label")]`. A tuple whose elements are all named gives one output
  per element. For example, `(double Average, double Max)` gives `average` and
  `max`, labelled "Average" and "Max". Unnamed or repeated element names are
  refused. `void`, `Task` and `ValueTask` give no output. A `Task<T>` or
  `ValueTask<T>` is awaited, and its value is the output.
- **Supplied by the run.** A `CancellationToken` or `NodeContext` parameter is not a
  pin; the run passes it in. `NodeContext` is a sealed class that may gain members
  later. It has:
  - `CancellationToken`: cancelled when the run stops;
  - `RunId`: as the run history shows it;
  - `Trigger`: the trigger's Data output;
  - `Error`: the most recent failure on this path, or null;
  - `State(entityId)`: reads an entity as Read entity does;
  - `Services`: this Home Assistant's services as typed methods, such as
    `context.Services.Light.TurnOn(...)`;
  - `CallService(service, target, data, returnResponse)`: calls a service by its id,
    as Call service does, and waits for it;
  - `Log(message, level)`: writes to the Home Assistant log.

  [States and services](#states-and-services) says how node code uses them.
- **Value nodes.** `[Node("Title", Pure = true)]` makes the method a value node, like
  Constant or Compare. It has no place in the run order and is worked out each time
  a step uses its result, in that step's scope. It must return a value.
- **Flows.** A parameter marked `[Port("Label", FlowControl = true)]` is a flow
  output, not an input pin; see [Flow outputs](#flow-outputs).
- **Numbers.** A pin of any CLR number type (`int`, `long`, `double`, `decimal`, …)
  and the built-in Number go into each other, wider or narrower. A value that does
  not fit fails the step when it runs, with a message such as "Count · How many
  needs a whole number from -2147483648 to 2147483647, not 2.5"; a value typed into
  the pin that cannot fit is refused at once ("How many must be a whole number
  between -2147483648 and 2147483647"). A nullable number output (`int?`) goes only
  into a pin that can hold null (`double?`), not into a Number or `long` input,
  because it may be missing.
- **Enums.** An enum pin offers its members by name in words, from the type's
  `enumLabels` (listed in the same order as `enumValues`).
- **Entities.** A parameter of type `Entity`, or of a domain's class such as
  `LightEntity`, is an entity pin; see [Entities](#entities).
- **Refused.** Methods that are not public static, open generic methods or
  containers, `ref`/`in`/`out` parameters, `ref` returns, pointer and ref-like
  types, static abstract interface methods and `async void`. Two node methods of the
  same name in one type are refused with "Give each node method its own name".
- **Nesting.** A pin's type nests at most 8 levels: the type itself is one, and each
  type argument, array element, tuple element and nullable wrapper inside it one more
  (`int` is 1, `List<int?>` is 3, `Dictionary<string, List<int>[]>` is 4). A deeper
  parameter, result or named tuple value is refused, at that parameter or value, with
  "This type is nested too deeply to be a pin; use a simpler type". This holds for
  node libraries and Custom C# nodes alike: working out what such a type converts to
  takes twice as long per level and cannot be stopped.

### Flow outputs

A node can choose which steps run next, and run them more than once, by declaring
its flows as parameters. Mark each one `[Port("Label", FlowControl = true)]` and give
it one of these types, in an `async` method that awaits it:

| Type | Calling it |
| --- | --- |
| `Func<Task>` | runs the flow's steps; the Task ends when they do |
| `Func<T, Task>` | the same, and the steps on that flow read the value as a pin |

```csharp
[Node("Classify temperature", Category = "Climate")]
public static async Task Classify(
    [Port("Cold", FlowControl = true)] Func<double, Task> cold,
    [Port("Comfortable", FlowControl = true)] Func<Task> ok,
    [Port("Hot", FlowControl = true)] Func<double, Task> hot,
    double temperature, double low = 19, double high = 24)
{
    if (temperature < low) await cold(low - temperature);
    else if (temperature > high) await hot(temperature - high);
    else await ok();
}

[Node("For each light", Category = "Lights")]
public static async Task EachLight(
    [Port("Body", FlowControl = true)] Func<(string Light, double Index), Task> body,
    string[] lights)
{
    for (var i = 0; i < lights.Length; i++) await body((lights[i], i));
}
```

The card has a row for each flow, labelled by `[Port]` (or the parameter's name in
words), then **Done** and **On error**. Calling a flow runs the steps wired to it at
once, in a scope of their own, as a loop runs its Body, and returns when they end.
When the method returns, the run goes on at Done. A node can call a flow any number
of times, and call several at once (`await Task.WhenAll(...)`): each call gets its
own scope, and the trace shows it as "Hot · Call 1".

- **Values.** The value a call carries is an output pin beside its flow: one pin with
  the flow's id and label for a single value, or one per element of a named tuple,
  such as `light` and `index` above. Each call's steps get their own copy, so a
  step that changes it changes neither another call's value nor the method's. Only
  steps on that flow can read it: not
  another flow's steps, and not the steps after Done. Values made by steps on a flow
  stay there too, as in a loop body. A value the method returns is set as usual and
  can be read after Done.
- **Always awaited.** A flow's steps may wait (a Delay, Wait until, a service call),
  and awaiting the flow holds no thread meanwhile. So a flow is never an `Action`,
  and a method with flows returns `Task` or `Task<T>`. Do not block on a flow with
  `.Wait()` or `.Result`: that holds a thread for as long as its steps wait.
- **Failures.** A step on a flow that fails throws out of the call, so the method
  can catch it. Uncaught, it fails the node, whose On error flow runs if it has one;
  the error is the inner step's. A Stop step on a flow stops the whole run.
- **Limits.** A step's flows can be called at most 1000 times in all. Flows nest
  like loops, 8 deep. A call still running when the method returns (one it did not
  wait for, such as the loser of `await Task.WhenAny(body(), Task.Delay(timeout))`)
  is cancelled, and the step waits for it to stop before the run goes on at Done. A
  flow called after its method has returned (kept in a field, say) fails the step
  that calls it: "Keep: Later was called after the step finished".
- **Refused.** An `Action` marked as a flow; a method with flows that does not return
  a `Task`; a flow on a value node (`Pure = true`); a flow of any other type, a
  nullable one or one with a default value; a tuple with unnamed elements; a flow
  named `next`, `error` or `condition`; two flows or values with the same name. A
  `Func<…, Task>` parameter without `FlowControl` stays an ordinary input pin, with a
  warning that suggests marking it.

A Custom C# node can have flows too. Its signature then carries `execution`,
`scopePorts` and `executionLabels`, as a library node's catalog entry does.

Pins carry the CLR type, never a refined catalog type, except for the entity classes
(see [Entities](#entities)). A `string` parameter accepts an
`entity_id` or `entity_id:light` value, because those refine `string`. A `string`
result does not fit an `entity_id` input such as Read entity's, or a domain-filtered
service target, because a string is not known to be an entity ID. Pass such a result
through a Convert node set to the input's type (`entity_id`, or `entity_id:light` for
a light target). An `EntityStateSnapshot` parameter can be wired directly from Read
entity or from a trigger's New state and Previous state outputs.

Pins support CLR classes, records, interfaces, enums, nullable types, arrays and
closed generics. The catalog's `assignableTo`, `nullable` and `literalKind` metadata
say what fits where. An arbitrary object type does not get a value editor.

**Properties.** The catalog entry of each class, record and struct your pins use
(and of HassSharp's own, such as `EntityStateSnapshot`) lists its `properties`:
`{name, type, nullable, description?}` for each public instance property that can be
read and each public instance field, inherited ones first, in declaration order.
Static members, constants, indexers, write-only, `protected` and `internal` members
and the record's `EqualityContract` are left out, and so is a member whose type a
pin could not have (a pointer, a ref-like type, a type your code cannot reach, or one
nested more than 8 levels). A property's description is its `<summary>`, or the
record's `<param>` for a positional property. A property's type joins the catalog as a
pin's type does, with its own properties. A generic type is read when a pin names it
or a pin's type relates to it (as a base type or interface); one first met as a
property's type (such as `Box<Box<T>>` inside `Box<T>`) is listed without properties,
which ends the walk. Enums, built-in types, JSON values, interfaces and the base
library's own types (such as `DateTimeOffset`, `List<T>` and `JsonElement`) have none.

So a property of your record can be read without writing a node for it: wire the
record into a **Select property** node and choose the property (see
[writing visual automations](writing-visual-automations.md#select-property)). A
Custom C# node's own types carry their properties in its `config.signature.types`;
a change of a property's type there makes the stored pins out of date only when a
Select property node reads that property.

The built-in types and the C# type a `[Node]` parameter uses to take each (the editor
shows both on every pin row, such as "True/False · `bool`"):

| Built-in type | Catalog id | C# parameter type |
| --- | --- | --- |
| True/False | `boolean` | `bool` |
| Number | `number` | `double` (or any other number type, see Numbers above) |
| Text | `string` | `string` |
| Object | `object` | `System.Text.Json.Nodes.JsonObject` |
| List | `array` | `System.Text.Json.Nodes.JsonArray` |
| Entity | `entity_id`, `entity_id:<domain>` | `string`, `Entity`, or the domain's class (`LightEntity` for `entity_id:light`) |
| Target | `target` | `System.Text.Json.Nodes.JsonObject` |
| Entity state | `global::HassSharp.EntityStateSnapshot` | `HassSharp.EntityStateSnapshot` |

The mapping goes one way: a `string` parameter takes an Entity value, but a `string`
result is Text and needs a Convert node to go into an Entity input (see above).

**Definitions.** Each type a node library declares is a link wherever a pin carries
it: on the editor's input and output rows, as a Select property's chosen property,
and in a run's trace. The link opens the declaration exactly as you wrote it in
the library, with its doc comments and attributes. A `partial` type shows all of
its parts, in file order. The dialog names the file, and **Open in library** opens
the file at the declaration. The same holds for types that a Custom C# node's code
declares, read from that node's code. HassSharp's own types show their public
signatures and `<summary>` text, taken from the documentation file that ships with
HassSharp. An entity class such as `LightEntity`, and its enums such as `LightState`,
show the code HassSharp generated for this Home Assistant, with nothing to open.
Backends and tools can ask for a definition with the `node_library`
operation `type_definition` and `{type, nodeId?, source?}`. To ask for a Custom C#
node's type, send that node's id and code. The reply is `{name, kind, text, origin,
line?, truncated?}`; `kind` is `source` or `metadata`, and generated code is `source`
with `origin.assembly` and no `line`. An unknown type answers
`not_found`. C#'s own types answer `invalid_request`, as do types from other
assemblies.

## Entities

A parameter can take an entity of one domain, as a handle on it with a typed state
and attributes:

```csharp
public static class MotionLights
{
    /// <summary>Turns the light on when the motion sensor sees motion and the light is off.</summary>
    /// <param name="context">Supplied by the run.</param>
    /// <param name="motionSensor">The motion sensor.</param>
    /// <param name="light">The light to turn on.</param>
    /// <returns>Whether the light was turned on.</returns>
    [Node("Motion lights")]
    public static async Task<bool> Run(NodeContext context, BinarySensorEntity motionSensor, LightEntity light)
    {
        if (motionSensor.State != BinarySensorState.On || light.State == LightState.On) return false;
        await context.Services.Light.TurnOn(light, brightnessPct: 80);
        return true;
    }
}
```

`motionSensor` is a pin of type `entity_id:binary_sensor` and `light` one of
`entity_id:light`. Only binary sensors reach `motionSensor`: the Entity output of a
trigger on a binary sensor connects to it, a light's is refused, and the entity
picker on the pin lists binary sensors only. The wire, a value picked on the pin and
the run's trace carry the entity ID (`binary_sensor.hallway`); the method gets the
handle. A handle is a service target as it is: `context.Services.Light.TurnOn(light)`.

**The classes.** Each domain has one class, named after the domain in PascalCase:
`LightEntity`, `BinarySensorEntity`, `MediaPlayerEntity`. They are in the namespace
`HassSharp.Entities`, which every node's code has in scope. HassSharp generates them
at startup from the states and attributes that Home Assistant lists for each domain
(its `entity_component` translations), so they follow this Home Assistant and its
integrations. There is no class per entity. Every class derives
from `Entity`, which takes an entity of any domain (pin type `entity_id`) and has:

- `EntityId`: the ID, such as `light.kitchen`.
- `RawState`: the state as Home Assistant's text (`on`, `21.5`, `unavailable`).
- `NumericState`: `RawState` as a number, or null when it is not one.
- `Attributes`: every attribute, as a JSON object; `Attribute("name")` gives one,
  or null when the entity has none by that name.
- `Snapshot()`: the whole state, as an `EntityStateSnapshot`.

A handle converts to its ID where a `string` is wanted: `string id = light;`,
`context.State(light)`. A domain for which Home Assistant lists nothing has a class
with these members and a `string State`.

**State.** A domain that lists its states has an enum of them, plus `Unknown`,
`Unavailable` and `Other`: `BinarySensorState` has `Off`, `On`, `Unknown`,
`Unavailable` and `Other`. A state that is not in the list reads as `Other`, and
`RawState` still has its text. `sensor`, whose state is often a measurement, and a
domain that lists no states have `string State`, the same text as `RawState`.

**Attributes.** Each attribute that Home Assistant declares for the domain is a
property, named in PascalCase (`color_mode` is `ColorMode`):

- An attribute with listed values is a nullable enum named after the domain and the
  attribute (`LightColorMode?`), or after the class and the attribute when that name
  is already one of HassSharp's types (`AutomationEntityMode?`, since
  `AutomationMode` is HassSharp's). It is null when the entity has no such attribute or
  its value is JSON null, and `Other` for a value that is not in the list. When
  several attributes of a domain list the same values (`color_mode` and
  `supported_color_modes`), only the shortest name gets the enum.
- An attribute whose listed values are exactly `false` and `true` is a `bool?`, such
  as `MediaPlayerEntity.IsVolumeMuted`. It is null when the entity has none, or when
  its value is not JSON true or false.
- Every other attribute is a `JsonElement?`, null when the entity has none. A JSON
  null value is a `JsonElement` whose `ValueKind` is `Null`, so check the kind:

  ```csharp
  /// <summary>The light's brightness from 0 to 255; null while it has none.</summary>
  [Node("Brightness", Pure = true)]
  public static int? Brightness(LightEntity light) =>
      light.Brightness is { ValueKind: System.Text.Json.JsonValueKind.Number } brightness ? brightness.GetInt32() : null;
  ```

**Reading.** A handle holds only the ID. Each time the code reads a member, it reads
the entity's state at that moment, except for the entity that started the run: that
one reads the state the trigger gave, for the whole run. An entity that Home
Assistant does not have fails the read with "Entity 'binary_sensor.hallway' is
unavailable.", so the step that reads it fails and takes its Error path.

**Optional, returned and refused.** `LightEntity? light`, with or without `= null`, is
an optional pin: left empty, the method gets null. A method can return a handle: its
output is of type `entity_id:light` and goes into a Call service target or any input
that takes lights. Refused, each with a message on the parameter or result:

- a handle that may be null as a result, a tuple element or a flow's value: "A node
  returns an entity, or nothing at all: remove the ? from this entity type";
- a handle inside a list, an array, a generic type or a tuple-typed parameter (such
  as `List<LightEntity>`): "An entity cannot be inside a list or another type on a
  pin; take or return one entity".

A `string` parameter still takes any entity, and `Entity` any domain. A graph saved
with a `string` entity pin keeps working.

**In the graph.** Select property on an entity wire lists the same members (see
[writing visual automations](writing-visual-automations.md#select-property)). In the
graph an entity enum is Home Assistant's text: Log writes `on`, the trace shows `on`,
a Call service field or a Create object gets `"on"`, and `Other` is written `other`.
A Compare against an enum stores its picked value as that text.

An entity enum passed to a typed service call (`context.Services`), on its own or
inside an object, is sent as Home Assistant's text too: `WeatherState.ClearNight` as
`clear-night`, not by its member's name, and `Other` as `other`. In code, `RawState`
or `EntityText.Of(value)` gives that text (`EntityText.Of` is null for `Other`).

The classes follow Home Assistant as the typed service calls do (see below). They are
generated from the live lists together with those they were last generated from,
which are kept in `.storage/hass_sharp/entity_schema.json`, so a restart has them
before Home Assistant has sent its lists. On the first start, code that names a class
waits for them: its automation loads by itself once they arrive. A domain whose
generated class does not compile is left out, and the log says which.

## States and services

An entity reaches node code through a pin: a handle (see [Entities](#entities)), a
`string` entity ID, or an `EntityStateSnapshot` wired from Read entity or a trigger.
The code reads and acts through `NodeContext`:

- `context.State("sensor.outdoor")` reads an entity's current state, as Read entity
  does.
- `context.Services.<Domain>.<Service>(...)` calls a service with typed fields:

  ```csharp
  [Node("Dim the light")]
  public static async Task Dim(NodeContext context, string light)
  {
      await context.Services.Light.TurnOn(light, brightnessPct: 30, transition: 2);
      var forecast = await context.Services.Weather.GetForecasts("weather.home", type: "daily");
      await context.Services.Script.GoodNight(delayMinutes: 5);
  }
  ```

- `context.CallService("light.turn_on", target, data)` calls a service by its id.
  Use it for an id that is known only at run time.

The typed methods are generated from this Home Assistant's own service
descriptions, so they follow its integrations and scripts. Completion and hover show
their names, descriptions, units, ranges and allowed values. The rules are:

- Domains and services are in PascalCase, and fields in camelCase: the
  `brightness_pct` field of `light.turn_on` is `brightnessPct`. A name that is not
  valid C# is made valid, and names that clash get a number (`TurnOn2`). The call
  always sends the original ids.
- A service with a target takes it first. The target is an entity ID, an array or
  list of them, a collection expression (`["light.a", "light.b"]`), or a
  `ServiceTarget`: `ServiceTarget.Areas("kitchen")`, `Devices`, `Floors`, `Labels`,
  or `new ServiceTarget { AreaIds = [...], LabelIds = [...] }`. `null` means no
  target.
- Required fields come next, then the optional ones, best given by name. Only the
  fields you give are sent.
- A value is sent as JSON, the way every service call sends it. A field whose type
  is `object` takes any value: an anonymous object's member names are written in
  snake_case (`new { Minutes = 5 }` is sent as `{"minutes": 5}`), and an enum as its
  name in snake_case (`DayOfWeek.Monday` is `"monday"`).
- A service that returns a response gives it as a `JsonElement`. When the response
  is optional, a second method gives it, such as `GetItemsWithResponse`.
- A call waits until Home Assistant has run the service. When the run stops, the run
  stops waiting for it; a call already sent to Home Assistant is not cancelled.

Services appear one by one while Home Assistant starts. The methods are therefore
generated from the live services together with those they were last generated from,
which are kept in `.storage/hass_sharp/service_schema.json`. Once that file exists,
a restart does not lose a method because its service is not registered yet. The
first start (or one after the file is lost, for example with a recreated container)
has only the services registered so far: an automation whose code calls a later one
does not load then, and loads by itself once the service is registered.

When a service goes away, its method goes too, but only once Home Assistant has
finished starting. Code that calls the method then fails its next compile, with the
ordinary compiler error on that node or library:

- An automation that is already running keeps running: it calls the service by its
  id. If it is not running (after a restart, say), it does not load until the
  service comes back; it then loads by itself.
- A node library whose code calls the method does not compile. Because libraries are
  compiled together, no library can be saved until that code changes or the service
  comes back. An automation that calls it does not block a library save: the save
  lists it as invalid.

Service descriptions are text from Home Assistant and its integrations. They only
ever reach the generated code as names, string literals and documentation. If the
generated methods still fail to compile, the methods of the services concerned are
left out, or the previous methods are kept, and the log says which.

## Custom C# nodes

A Custom C# node is a step whose code is one method. An entity definition also has
two nodes whose code is one class: a **C# entity**, one entity whose class derives
from an entity class such as `SharpSwitch`, and a **C# device**, one device whose
class derives from `SharpDevice` and adds its entities. Their code is compiled as a
Custom C# node's is, in a namespace of the node's own, and their `[Setting]`
properties are their inputs; see [C# nodes](custom-entities.md#c-nodes). The rest of
this section is about the Custom C# step.

A new Custom C# node starts from a template method. Its stored configuration is:

- `source`: the code;
- `arguments`: one value per input;
- `signature`: the pins, worked out from the code.

The signature holds the title, description, `pure`, the inputs and outputs, and
`types`: the compiler's entry for every type the pins use that the static catalog
does not have, such as `int`, `List<string>`, a library record or a record defined
beside the method. The code is compiled in its own namespace,
`NodeLib.Custom_<escaped node id>`, with the libraries in reach. The escaped id keeps
ASCII letters and digits and writes every other character as `_` followed by its
UTF-16 code in four uppercase hex digits: node `kitchen-light` compiles in
`NodeLib.Custom_kitchen_002Dlight`. `using` lines at the top of the code are placed
inside that namespace.

A wire into or out of a Custom C# node is accepted where C# converts the value
implicitly: a library `int` into a `long` parameter, a `List<string>` into an
`IEnumerable<string>`, a record the code declares into a library interface it
implements or into `object`. Any number goes into any numeric pin, except a nullable
one into a pin that cannot hold null; a value the pin cannot hold (2.5 for an `int`)
fails the step when it runs, naming the step and pin, and such a value typed into
the pin is refused at once. The same check applies to a loop's carried and exported
values, to each item a For each takes (naming the item), and to Convert. Compare
works in a type that holds both sides exactly, never rounding one side to the
other's type, and its trace shows the values as given. Less than, at most, greater
than and at least take any number type on either side; with a nullable side that is
null they are false. A type that one Custom C# node declares belongs to that node and
is not for another node's code (declare the type in a node library to share it):
another node whose pin has that type is refused, though a wire can carry it into
`object` or an interface it implements. The same holds for a type a C# entity or C#
device declares (another node's pin or setting of that type is refused), and a C# entity's
or C# device's class may not derive from a type another node declares; the problem
names the kind of node that has it: "NodeLib.Custom_thermometer.Level belongs to the C# entity node
thermometer; declare the type in this code or in a node library to use it here".

Between two Custom C# nodes, a type that only Custom C# code uses (not the catalog
or a node library) is matched by what each node's compile records about it. Each
node records, for every type its pins use, the types it converts to among the
catalog, the libraries, that node's own pin types and the type's base types and
interfaces, and the types among the catalog, the libraries and its own pin types
that convert to it. When several nodes use one type, the editor and the backend take
the union of their records, so the order of the nodes does not matter. So a wire
between two Custom C# nodes is accepted when:

- both pins have the same type;
- the target is a base type or an interface of the source type (`List<string>` into
  `IEnumerable<string>`, a declared record into `IDisposable` or `object`);
- the target is a catalog or library type the source converts to, or the source is
  one that converts to the target;
- both are numbers (the rule above);
- or some Custom C# node in the graph uses both types in its pins, so its compile
  recorded the conversion.

A conversion that C# makes only through generic variance between two types that
only Custom C# code uses is refused otherwise: a `List<string>` output into another
node's `IEnumerable<object>` pin is refused, though C# accepts it, unless a node
uses both `List<string>` and `IEnumerable<object>`. Wire it through such a node or
declare the parameter as `IEnumerable<string>` instead. A user-defined `implicit
operator` is never used for a wire.

The **Edit code** dialog edits the code with completion, hover, signature help and
diagnostics, and applies its pins to the node. MCP authoring sends `source` and
`arguments`: preview and save work the signature out from the code
(`deriveSignatures`), and say in `diagnostics` which values and wires of pins that
are gone they removed. Both validators refuse a stored signature that is not one the
compiler writes, with code `custom.signature`. The backend refuses one whose pins no
longer match the code (their ids, types, required, nullable, defaults and `pure`):
"The pins are out of date with the code: open Edit code and apply it". Labels,
descriptions and type details that differ are not out of date: they are refreshed
from the code. The node library API's `custom_preview` compiles one node's code
(`nodeId` is required) and returns its signature and diagnostics; while a node
library has errors it says so first. Completion, hover, signature help, formatting
and go-to-definition work on a node's unsaved code beside the libraries (`custom: true`).

Older Custom C# code (statements reading `values`, `lastError` and `TriggerData`) is
refused: "Custom C# code is now one static method marked [Node]; rewrite this node's
code in that form". The failure details of a path are now the **Error details** value
node (its Message and Step outputs) or `NodeContext.Error`.

## Node libraries

Create a file in the **Node libraries** view, or use the administrator MCP
`library_preview` / `library_save` tools. The source is wrapped inside namespace
`NodeLib`; provide namespace contents, not an outer file-scoped namespace. Libraries
compile together with the generated service calls and the framework references used
by automations. Their source is stored in Home Assistant's
`.storage/hass_sharp/libraries` directory through the revision-checked authoring
store. Editing files directly bypasses the authoring conflict checks.

This complete harmless library accepts an entity's state and returns a record. A
second node accepts the record's interface, and a third is a value node:

```csharp
public interface IReading { string State { get; } }
public record Reading(string State) : IReading;
public static class Samples
{
    /// <summary>Reads what a Home Assistant entity's state says.</summary>
    /// <param name="entity">The entity's current state.</param>
    /// <returns>Its reading.</returns>
    [Node("Describe", Category = "Examples")]
    [return: Port("Reading")]
    public static Reading Describe([Port("Entity state")] EntityStateSnapshot entity) => new(entity.State);

    /// <summary>Writes a reading as a sentence, without changing devices.</summary>
    [Node("Format reading")]
    public static string Format(IReading reading) => $"State: {reading.State}";

    /// <summary>The text in capitals. A value node: it is worked out when a step uses it.</summary>
    [Node("Shout", Category = "Examples", Pure = true)]
    public static string Shout(string text) => text.ToUpperInvariant();
}
```

Save it with ID `example` and name `Example.cs`. Add the execution chain **Manual
trigger → Read entity → Describe → Format reading → Log**, and choose a real entity ID
in Read entity. Then wire:

- Read entity's `value` output to Describe's `entity` input;
- Describe's `result` to Format reading's `reading`;
- Format reading's `result` to Shout's `text`;
- Shout's `result` to Log's `value`.

Shout takes no step order wire. Roslyn checks the interface assignment. The complete
graph JSON, the library source and the other authoring examples ship in
[`authoring_examples.json`](../custom_components/hass_sharp/authoring_examples.json)
and are returned by `get_capabilities`. Preview the graph, save it, then run its
`start` trigger by hand to write a log entry. Reading state does not control a device.

A library node's kind is `library:<source id>:<type>.<method>`. Each segment is
URI-escaped, as in `library:example:global%3A%3ANodeLib.Samples.Describe`. Changing a
method's parameters keeps its kind; renaming the method or its type changes it. A
class offered as a node has the kind `library:<source id>:<type>`, without a method
(see [A class as a node](#a-class-as-a-node)).
Renaming a file keeps its stable ID and kinds. Preview inspects symbols without
invoking attributes, static initializers or methods.

The library editor supports full Roslyn completion, diagnostics, hover, signature
help with named parameters, formatting, and cross-file go-to-definition. Drafts use
immutable multi-file workspaces. The frontend discards obsolete responses using
model/workspace identities; the saved catalog generation differs from the draft
source generation. A rejected save keeps the prior source and the active graphs. A
successful save, rename or delete recompiles all graphs and entity definitions and
advances their revisions, and is rejected when it would break one that works.
A graph that has errors with the new code is not rebuilt: the response lists it under
`invalid` (a definition under `invalidDefinitions`), with its diagnostics, `running`,
and a `message`. If a copy of it is
running, that copy is still running the previous version, with the previous library
code, until the graph is fixed and saved; otherwise it is not running.
Deleting a type or method that is still in use is rejected with dependency
diagnostics.

## Import from GitHub

**Add external** imports node libraries and entity definitions from a public GitHub
repository that lists them in a `hass_sharp.toml` at its root. It is a button in the
**Node library** view, beside **Add file**, and on the **Entities** page, beside
**New entity definition**; both open the same dialog, which imports both kinds. It is
for Home Assistant administrators, as the node library is: the **Entities** page
shows it only to them. The dialog says, and it holds for all that it imports:

> Node libraries and entity definitions run C# inside Home Assistant with full access
> to it: its devices, its data, its files and your network. Import only from
> repositories you trust.

Paste the repository's address and choose **Inspect**. The address is the one a
browser shows: `https://github.com/owner/repo` for the default branch, or
`https://github.com/owner/repo/tree/<ref>` for a branch, tag or commit, whose name
may hold a `/`, as in `/tree/feature/x`. A trailing `/`, a `.git`, a query such as
`?tab=readme-ov-file`, a `#readme` and `www.github.com` are accepted. Any other
address is refused: "Paste a GitHub repository's address, such as
https://github.com/owner/repo or https://github.com/owner/repo/tree/main".

Inspect asks GitHub's API once which commit the address names, then reads
`hass_sharp.toml` and every file it lists at that commit. All that the dialog shows
and imports is from that one commit, which it names beside the repository. Without
signing in, GitHub's API answers 60 requests an hour from one network address, so
Inspect can be refused for a while: "GitHub refused the request: it allows 60
requests an hour without signing in; try again later". The files come from GitHub's
raw file host, which has no such limit but may refuse a file for a while too: "GitHub
refused to send {path}: try again later". Import makes no API request: it reads
`hass_sharp.toml` and the picked files again at the same commit, and when one cannot
be read it writes nothing.

The list has a group of **Node libraries** and one of **Entity definitions**. Each
item has its description and a **View on GitHub** link to its file at that commit.
An item is already stored when a library file or a definition has exactly its
name. Such an item starts unchecked and the others start checked, so inspecting a
repository you imported from before picks only what is new in it. A file that is not
in the repository at that commit, is larger than 2 MiB or is not UTF-8 text, or a
definition that is not valid JSON or has no logic of kind graph, says why and cannot
be picked. What an item offers:

- **A node library** whose file name is not stored is imported. One whose name is
  stored can only be overwritten, **Overwrite {name} ({id})** (one choice for each
  stored file of that name), or skipped by leaving it unchecked. It is never imported
  as a second copy: every library file is compiled into the one `NodeLib` namespace,
  where two copies would declare each of their types twice.
- **An entity definition** whose name is not stored is imported. One whose name is
  stored offers **Overwrite {name} ({id})**, **Add as new**, or Skip by leaving it
  unchecked.
- A definition imported as new comes in turned off, with an id made from its name as
  the panel makes one: **Front door people** gets `front_door_people`, or
  `front_door_people_2` when that is taken. An overwritten definition stays on or off
  as it was.

**Import** saves the picked libraries first, all of them in one change, and then
each picked definition. The libraries are compiled together with the stored ones,
and every automation and definition is rebuilt against them, as a library save does
(see [Node libraries](#node-libraries)). So a library that uses another is imported
with it, or after it is stored: hass-sharp-vision's `PeopleDetector.cs` uses
`Vision.cs`, and picked alone while no `Vision.cs` is stored it is refused with the
compiler's message (`PeopleDetector.cs {line}:{column}: {message}`). A refused
library import writes no library and no definition: each definition's line says
"Not imported, because the node libraries were not". It is refused too when it would
break an automation or a definition that works ("it would break {name}"), and when a
file it overwrites was saved again since Inspect ("It changed after you inspected
the repository; inspect it again").

Each definition is then saved by itself, as a save in the editor is. The rebuild that
the libraries' import made is not a change since Inspect: a definition overwritten
with the libraries it uses is saved over its rebuilt revision. Only a definition
saved again by something else since Inspect is refused with "It changed after you
inspected the repository; inspect it again". One that is refused says why on its line and leaves the imported libraries, and the other
definitions, in place: fix it in the editor, or import it again. The result has one
line for each item, such as `Vision.cs: imported`, `PeopleDetector.cs: overwritten`,
`Front door people: imported, turned off` or `{name}: not imported — {why}`.
**Back**, on the list or the result, goes to the address again, as you typed it: to
inspect the repository again after "It changed after you inspected the repository",
or to import from another one. After
an import the **Node library** view reads its files again; a draft of a file the
import overwrote is kept and marked as a revision conflict, as after a save made
elsewhere.

Overwrite keeps what you filled in:

- Each setting of the definition whose key and type the stored definition has keeps
  its stored value, a secret too. A new setting, or one whose type changed, takes
  the file's.
- A value typed into a C# node's setting, such as the Camera of hass-sharp-vision's
  **Front door** device or a Face score you tuned, is kept when the stored definition
  has a node of the same id and kind, the setting has the same type (and domain) in
  the file's node as in the stored one, and the file does not wire that setting to
  another step. It is kept whether or not the file's node gives that setting a value.
  A file's node that lists no settings (no `signature`) is taken to have the stored
  node's; the import works the pins out from the code again, and a value that no
  longer fits them is refused on the definition's line rather than dropped. A setting
  whose type or domain changed, or that the file wires, takes the file's.
- The same holds when the file's node of that id is of another kind, one a C# node
  and the other the node of a library's class (see
  [A class as a node](#a-class-as-a-node)), in either direction: a value typed into a
  setting that both have, of the same type, is kept, unless the setting is secret or
  the file wires it. So a definition whose C# device moves to the library's node under
  the same node id keeps what you typed into its settings.

An import is a one-time copy. Nothing keeps track of the repository or updates from
it: to take a newer version, run **Add external** again and overwrite.

### Writing a hass_sharp.toml

A repository offers its files in a `hass_sharp.toml` at its root; one anywhere else
is not read. It lists each node library in a `[[library]]` table and each entity
definition in an `[[entity]]` table:

- `[[library]]`: `id`, optional, the id the library is stored under (see Library
  ids below): up to 80 lower-case letters, digits, `_` or `-`, starting with a
  letter or digit; `name`, its file name in the panel, up to 120 characters, without
  `/`, `\` or a line break; `path`, its `.cs` file; `description`, optional.
- `[[entity]]`: `name`, the definition's name, not blank and up to 120 characters;
  `path`, its `.json` file; `description`, optional.

The libraries come first in the dialog and are imported first, and each kind keeps
the manifest's order. Keys it does not know are ignored. No two items may have one
path, and no two libraries one name or one id. A path is relative to the
repository's root and `/`-separated: no leading `/`, no `\`, no empty, `.` or `..`
part. A library's ends in `.cs` and a definition's in `.json`, in lower case. Each
file, `hass_sharp.toml` too, may be at most 2 MiB, and a manifest may list at most
50 items. A manifest that breaks a rule is refused whole, with every problem:
"hass_sharp.toml: library 1: path must be a relative path inside the repository to a
.cs file".

An entity file is a definition body, `{name, settings, logic}`, with `logic` of kind
`graph`. The manifest's `name` is the definition's, whatever the file's says, and it
is the name an existing definition is matched by. The file may be a definition as the
entity definitions API's `read` answers it (see
[the document](backend/entity-definitions.md#the-document)): only its `settings` and
`logic` are taken, and its other members are dropped. Importing works out the pins of
each C# node again from its code, against the libraries just imported, so a file
whose C# nodes lag a library it comes with still imports. A value a user typed into
a C# node survives an Overwrite whether or not the file's node gives that setting a
value; give one to each setting a new import needs, as **Front door people** gives
its Camera `camera.front_door`.

**Library ids.** A library node's kind carries its library's id,
`library:<id>:<type>.<method>`, and a definition whose graph uses a library node,
such as Detect faces in a graph camera, stores that kind. The Home Assistant that
imports it may store the library under another id, so set each library's `id` to the
id the definitions' kinds use (`library:<id>:…`). A new library is stored under its
`id` when it has one that no stored library has, and otherwise under a new id
`library-<uuid>`, as a file added in the panel is; an overwritten library keeps the
stored file's id.
The import rewrites the `library:<id>:` kinds of the definitions it imports, a class
node's such as `library:relays:global%3A%3ANodeLib.RelayHub` too, to the id
each library has: the one it is stored under, or, for a library left unchecked, the
id of the one stored file of its name (with none or several, the kind stays as it
is). A class a C# node's code derives from, as in
`public class FrontDoorPeople : PeopleDetector { }`, is found by its name, whatever
its library's id, so it needs no `id`.

hass-sharp-vision's manifest, for example:

```toml
[[library]]
id = "vision"                                     # stored under this id when free
name = "Vision.cs"                                # library file name in the panel
path = "nodes/Vision.cs"
description = "Face and person detection on a native library (YuNet and YOLOX-nano on tract), and the Detect faces node"

[[library]]
id = "people_detector"
name = "PeopleDetector.cs"
path = "nodes/PeopleDetector.cs"
description = "People detector: a device that watches a camera and reports faces and people seen"

[[entity]]
name = "Front door people"
path = "nodes/front_door_people.json"             # a DefinitionBody: name, settings, logic
description = "A People detector on camera.front_door"
```

## Setting up a node class

A class of a node library may have one method marked `[NodeSetup]`, of the shape
`public static Task Setup(NodeSetupContext context)`. It is the place for one-time
work that the class's nodes share, such as downloading a model and opening it. It
runs once when an entity definition whose graph uses any `[Node]` of the class
starts, before its When started flow, and again each time the definition starts:
after a restart, when the definition is saved or turned on again, and after a
library save, which recompiles every definition. Each definition compiles the
libraries into an assembly of its own, so it has its own copy of the class's
statics, and a setup sets up the copy of its definition.

The setups share the 60 seconds that the definition's C# nodes have for their
constructors and `Setup()`s, and run before them; the time a download receives is
not counted. A setup that throws keeps the definition from starting, and the
definition says why, naming the class: "`{definition}: {class}: {message}`". A setup
whose context cannot be made, such as on a host with no directory for downloads,
does not run, and the definition fails in the same way. One that takes longer fails
with "`{definition}: {class}: its [NodeSetup] did not finish within 60 s`". What it
owned until then is disposed.

A node of such a class works only in an entity definition's graph: the editor leaves
it out of an automation's list of steps, an automation that has one is refused
with code `node.document` ("`{step} only works in the graph of an entity
definition; delete it from this automation`"), and so is a library save that would
add a setup to a class an automation uses. The compiler refuses a setup of any other
shape, a second setup in one class, and a setup in a C# node's code ("`[NodeSetup]
works in a node library only; move this class into one`"); a setup in a class
without `[Node]` methods is a warning, since it never runs.

`NodeSetupContext` gives the setup what it reaches of its definition:

- **`Files`**: the definition's files. `Files.Download(url, sha256, name,
  cancellationToken)` downloads a file once and keeps it by its checksum, as it does
  for a C# node (see [Http and Files](custom-entities.md#http-and-files)).
- **`Log(message, level)`**: writes a line to the definition's log, under its name;
  `PyLogLevel.Info` when no level is given.
- **`RuntimeIdentifier`**: the process's runtime identifier, such as
  `linux-musl-arm64`: which native library to load.
- **`Stopping`**: cancelled when the definition stops, also while the setup still
  runs. Pass it to each download and request.
- **`Own(disposable)`**: keeps an object until the definition stops, and returns it.
  When the definition has stopped, what the setups own is disposed, the last owned
  first; one whose `Dispose()` throws is logged as a warning and the rest are still
  disposed. Stopping waits at most 10 seconds in all for the start, the nodes and
  frames still running, and the `Stop()`s, and disposes what is owned also when they
  have not finished by then: `Dispose()` can run while a node, a frame or a `Stop()`
  still uses the object. Make `Dispose()` wait for a call in progress and a later call fail, as
  hass-sharp-vision's detector does: each call and `Dispose()` take one lock, and a
  call after `Dispose()` throws `ObjectDisposedException`. Once the definition has
  stopped, `Own` disposes the object at once and throws `ObjectDisposedException`.

This class downloads a table once, maps it into memory for as long as the
definition runs, and reads it in a value node:

<!-- compiled: Table -->
```csharp
using System.IO;
using System.IO.MemoryMappedFiles;

public static class Table
{
    // The file, and its SHA-256 as sha256sum gives it: put the file's own in place of the zeros.
    const string Url = "https://example.com/table.bin";
    const string Sha256 = "0000000000000000000000000000000000000000000000000000000000000000";

    // This definition's view of the table: each definition has its own copy of the class.
    static MemoryMappedViewAccessor? view;

    /// <summary>Downloads the table once and maps it until the definition stops.</summary>
    [NodeSetup]
    public static async Task Setup(NodeSetupContext context)
    {
        var path = await context.Files.Download(Url, Sha256, cancellationToken: context.Stopping);
        var file = context.Own(MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read));
        view = context.Own(file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read));
        context.Log($"Table ready on {context.RuntimeIdentifier}");
    }

    /// <summary>The byte of the table at an offset.</summary>
    [Node("Table byte", Category = "Examples", Pure = true)]
    public static int Byte(long offset) =>
        (view ?? throw new InvalidOperationException("Table byte works in an entity definition, once it has started")).ReadByte(offset);
}
```

The view is owned last, so it is disposed before the file it reads. A complete
example is `nodes/Vision.cs` in
[hass-sharp-vision](https://github.com/dompagoj/hass-sharp-vision): its setup
downloads a native library for the process's runtime identifier and owns the
detector it opens.

## Entity classes

A node library can hold classes for [entity definitions](custom-entities.md),
deriving from `SharpDevice` or from an entity class such as `SharpSwitch` or
`SharpCamera`. Such a class is used in either of two ways:

- **As a node of its own.** `[Node("Title")]` on the class offers it in the step
  list of entity definitions. You drop it on the canvas, fill in its settings and
  write no code.
- **As a base class.** The class of a C# entity or a C# device node derives from it
  and takes its settings and its code, so several nodes, in one definition or
  several, share it.

A library that holds such a class needs no node method, and may hold classes and
node methods side by side.

### A class as a node

This device class is a hub of relays that answers HTTP at its address, with a switch
for each of its four relays:

<!-- compiled: RelayHubNode -->
```csharp
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

/// <summary>A hub of four relays that answers HTTP at its address: a switch for each relay.</summary>
[Node("Relay hub", Category = "Relays")]
public class RelayHub : SharpDevice
{
    /// <summary>Where the hub answers, such as http://192.168.1.40/.</summary>
    [Setting("Address")]
    public string Address { get; set; }

    /// <summary>The token the hub asks for; none when it asks for none.</summary>
    [Setting("Token", Secret = true)]
    public string? Token { get; set; }

    /// <summary>How many seconds pass between two readings.</summary>
    [Setting("Poll every", Minimum = 1, Maximum = 60)]
    public double PollEvery { get; set; } = 5;

    readonly SharpSwitch[] relays = new SharpSwitch[4];

    /// <summary>Adds a switch for each relay, which turns that relay on and off.</summary>
    public RelayHub()
    {
        for (var i = 0; i < relays.Length; i++)
        {
            var relay = i;
            relays[i] = Add(new SharpSwitch($"relay_{i + 1}", $"Relay {i + 1}") { OnTurnOn = () => Turn(relay, true), OnTurnOff = () => Turn(relay, false) });
        }
    }

    /// <summary>Reads the relays every Poll every seconds while the definition runs.</summary>
    protected override Task Start()
    {
        Every(TimeSpan.FromSeconds(PollEvery), Poll);
        return Task.CompletedTask;
    }

    // The hub answers GET relays with one true or false for each relay.
    async Task Poll()
    {
        var states = JsonNode.Parse(await Send(HttpMethod.Get, "relays"))!.AsArray();
        for (var i = 0; i < relays.Length; i++) relays[i].IsOn = states[i]?.GetValue<bool>();
    }

    async Task Turn(int relay, bool on)
    {
        await Send(HttpMethod.Post, $"relays/{relay}/{(on ? "on" : "off")}");
        relays[relay].IsOn = on;
    }

    async Task<string> Send(HttpMethod method, string path)
    {
        using var request = new HttpRequestMessage(method, new Uri(new Uri(Address), path));
        if (Token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var response = await Http.SendAsync(request, Stopping);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(Stopping);
    }
}
```

Once the library is saved, an entity definition's step list has **Relay hub** under
**Relays**. An automation's has not: a class is a node of entity definitions only.

- **Which classes.** A class marked `[Node]` that derives from `SharpDevice` or from
  an entity class is offered when a definition can create it: it is public, not
  nested in another type, not abstract and not generic, and has a public constructor
  without parameters. One that is not is left out of the step list, and the library
  compiles with a warning at the class's name, such as "`RelayHub has [Node] but is
  not offered as a step: a class a definition creates is public, not abstract or
  generic, and has a public constructor without parameters`". A class meant only as
  a base, such as an abstract one, has no `[Node]`. A class without `[Node]` is never
  a step, so a helper class that a device adds does not show up.
- **Its title and place.** The text of `[Node("…")]` is the step's title, and
  `Category` the group it is listed under: **Custom** when the class gives none. Its
  `<summary>` is its description.
- **The node.** It is drawn and edited as a C# device or a C# entity node is (see
  [C# nodes](custom-entities.md#c-nodes)), without code: there is no **Edit code**,
  and the right panel names the library it comes from, a link that opens it
  ("From the node library Relays.cs." for a library saved as `Relays.cs`). A
  device class's node has a **Key**, a **Device name** and **Part of**; an entity
  class's has a **Key** and a **Device** pin. Its inputs are the class's
  `[Setting]` properties, a base class's first, and each takes a value typed into
  the node, a Setting step or nothing, as a C# node's settings do (see
  [Settings as inputs](custom-entities.md#settings-as-inputs)). Here **Address** is
  required, **Poll every** is 5 unless set, and **Token**, a secret, takes only a
  Setting step.
- **Its key.** A dropped node is keyed by its title, `relay_hub`, then `relay_hub_2`
  when that is taken, and a device class's node is named by its title. As for any
  device or entity, the key cannot change once saved.
- **At run time** it is a C# node of its class: its constructor, `Setup()`,
  `Start()` and `Stop()` run, its entities, its [page](custom-entities.md#a-page-for-your-device)
  and its [data](custom-entities.md#keeping-data) are kept under its key. In Home
  Assistant an entity class's node is the entity `<definition id>:<key>`, as a C#
  entity is; a device class's node is the device `<definition id>:<key>`, and each
  entity it adds is `<definition id>:<key>:<entity key>`.

As a document, the node's kind is `library:<library id>:<type>`, here
`library:relays:global%3A%3ANodeLib.RelayHub` for a library stored under the id
`relays`, and its `config` is a C# node's without `source` and `signature`:
`{key, name, via?, arguments?}` for a device class, `{key, arguments?}` for an entity
class.

### A class as a base

```csharp
/// <summary>A relay that answers HTTP at its address.</summary>
public abstract class HttpRelay : SharpSwitch
{
    [Setting("Address")]
    public string Address { get; set; } = "";
}
```

A C# entity's code is then `public class KitchenRelay : HttpRelay { … }`, and the
node has the Address input with the settings of its own class after it. A class
offered as a node can be a base class too: a C# node that derives from `RelayHub`
works as before, and adds what its own code does.

[C# nodes](custom-entities.md#c-nodes) has complete classes, the base classes,
settings, the life cycle (`Setup`, `Start`, `Stop`), timers, entity tracks, HTTP,
downloads and cameras.

### Changing a class that definitions use

A library save rebuilds every entity definition, and is refused when it would break
one, naming it (see [Node libraries](#node-libraries)). For a class that definitions
use as a node, that means:

- Removing the class, or its `[Node]`, is refused while a definition has a node of
  it: that node would be "This step is no longer in its node library; delete it and
  add it again from the node library".
- Removing a setting that a node has a value for is refused, as removing a parameter
  of a node method is: "Unknown input PollEvery".
- A new required setting (a `string` or an entity with no initializer and no `?`) is
  refused while a definition has a node of the class without a value for it: "Room
  is required". Give a new setting a default (`= "hall"`, or a `?`), or ship the
  library together with a definition that sets it. A new setting with a default is
  accepted: each definition is rebuilt and runs the new code, the setting at its
  default.

### From a C# node to the class's node

A C# node whose code only derives from a library class, such as
`public class FrontDoorPeople : PeopleDetector { }`, can be replaced by the node of
that class once the library marks it `[Node]`. Delete the C# node, drop the class's
node, and give it the old node's key under **Key** before you save. Deleting the C#
node asks about its stored data, if it has some ("{name} has stored data. It is
deleted when you save the entity definition without a node keyed {key}."): the new
node has that key, so the data is kept.
Its device, its entities with their entity ids and history, its page and its data are
all kept by the definition's id and the node's key, so the new node takes them over.
Type its settings again, or wire them to the Setting steps the old node had.

## Scope snapshots

Loops and parallel branches isolate values with detached deep copies, taken the
first time a step inside the loop or branch reads a value from outside it; a value
it never reads is not copied. An entity snapshot is immutable. JSON nodes, arrays,
lists, dictionaries and ordinary user classes and records are copied. Aliases within
one scope remain aliases within that scope. Nullable values keep their null state.
A value being assignable at compile time does not guarantee that it can be copied:
a value that cannot be copied fails the step inside the loop or branch that reads
it, not the loop or branch itself. Its message names that step, its pin and where
the value came from, for example "Log · Message reads Build list · Result, which
cannot be copied into a loop or parallel branch; return a list or an array
instead". The type that could not be copied is the failure's inner exception.

Live resources cannot be copied. These include streams, tasks, delegates, safe
handles, LINQ sequences and other framework objects that are not plain data (sets
and read-only wrappers among them). For your own
resource containers, implement `IVisualSnapshot.CreateVisualSnapshot()`. It must
return a detached instance of the same concrete type, deeply copying every reachable
mutable object; returning the original object or a shallow copy violates the
contract. Keep IO and resources inside the method body, and return data the next
scope can use.
