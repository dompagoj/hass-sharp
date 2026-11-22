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
method's parameters keeps its kind; renaming the method or its type changes it.
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
`SharpCamera`. One is used by deriving from it in a node's code: the class of a C#
entity or a C# device node derives from the library's class and takes its settings
and its code, so several nodes, in one definition or several, share it. It needs no
`[Node]` method, and a library may hold classes and node methods side by side.

```csharp
/// <summary>A relay that answers HTTP at its address.</summary>
public abstract class HttpRelay : SharpSwitch
{
    [Setting("Address")]
    public string Address { get; set; } = "";
}
```

A C# entity's code is then `public class KitchenRelay : HttpRelay { … }`, and the
node has the Address input with the settings of its own class after it.

[C# nodes](custom-entities.md#c-nodes) has complete classes, the base classes,
settings, the life cycle (`Setup`, `Start`, `Stop`), timers, entity tracks, HTTP,
downloads and cameras.

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
