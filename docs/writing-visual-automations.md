# Writing visual automations

HassSharp supports **graph version 5 only**. Older graph formats must be recreated;
there is no migration or backward-execution mode. Document and API envelope
version 1 is separate from the graph version.

Choose **Automations → New automation**, enter a name, and edit the
canvas. Drag steps from the palette, inspect their settings, and connect control
and typed data ports. The inspector uses the same catalog as compiler validation
and MCP. Custom library nodes appear after a successful library save.

**Save** validates, compiles, persists and activates. Native trigger
configuration is checked by Home Assistant before saving. Warnings remain visible;
errors prevent persistence. An entity ID that Home Assistant has no entity for (in an
entity trigger, or typed into an entity input or a service target) is a warning,
code `entity.unknown`: check the spelling, since a trigger on it never fires. It
does not block the save, because the entity may be added later. Saving startup/scheduled/native triggers can cause
external effects. Unsaved changes are drafts. Run now requires the exact saved
revision to be active and the selected trigger to permit manual execution. If
activation fails, an older runtime may remain active; the editor reports the
failure and disables running the newer saved revision.

## Turning an automation off

Each automation has an on/off switch, on its card on the Automations page and in
the editor. Turning it off changes neither its graph nor its revision:

- none of its triggers starts it any more, and neither does a startup run: its
  Home Assistant triggers are detached, and a daily or once time that passes while
  it is off is skipped;
- its runs in progress are cancelled, with the reason "Automation turned off";
- **Run now** still runs it, for testing.

Turning it on again attaches its triggers at once, without a save. The switch is
kept across saves and restarts. **Duplicate** in a card's menu makes a copy that
starts off, so the copy never runs beside the original until you turn it on. The
**Off** filter on the Automations page lists the automations that are off.

## Control flow and typed data

Control edges schedule execution. A control output has one destination; multiple
paths may enter a non-trigger step. Triggers cannot receive control inputs.
Connections cannot create cycles; use bounded Repeat, While or For each loops.
Data edges supply typed values and **never execute their source steps**. The
producer must have executed on the chosen path before the consumer reads it.
Missing producer output is a mapped runtime error, not an implicit default.

Value nodes (Constant, Object, List, Create object, Create list, Get field, Entity
field, Convert, And, Or, Exclusive or, Not, Compare and Error details; `"pure": true`
in the catalog, and any `[Node(Pure = true)]` method) are not part of the run order:
they have no control pins and no Error path. A value node is worked out each time a
step that reads it runs, in the scope of that step, so the step gets the value as it
is at that moment. One exception: Wait until works out a wired Expected value once,
when the wait starts, not again at each poll. When a value
node fails, the step reading it fails (and takes its own Error path). A value node
that no step reads is a warning.

A Constant gives a value you type: its card and panel show a Type select and an
editor for a value of that type. Its value must be a value of its Type: a value of
another type (from a graph saved before the Constant had a Type, say) is shown in
the Type's editor with a problem until you type it again or change the Type.
Changing the Type keeps the value when it is still a value of the new type and
clears it otherwise (one undo step). A Constant takes no connection: it has no
input pin, and a wire into one from an older graph is a problem whose Remove
connection action removes it. To change the type of a connected value, use
Convert.

Inputs can be values you type, explicit unset values, or connected data ports. A
connection replaces the input's own value. A step's value reaches another step only
over a data wire; there are no named variables. Use a Custom C# node, or a
[node library](node-libraries.md), for calculations the built-in nodes do not cover.
Null and absence are distinct. Error details gives the message and step of the most
recent failure on the path, for an Error path to report or log. When several
Parallel branches fail, the step is the one in the first branch that failed; when a
recovered failure is thrown again, the step is the one that threw it last.

Read entity returns a detached `HassSharp.EntityStateSnapshot`. Use Entity field
to extract a scalar state/attribute, or pass the snapshot directly to a typed
custom method.

### Select property

Select property (a value node, in the Data category) reads one property of a typed
C# value. Connect a value to its Value input: a record, class or struct that a node
library step or a Custom C# node gives, or an entity state. Its Property select then
lists that type's properties in the order the type declares them, each with its type
in words and in C#, such as "UserName · Text (string)"; with more than 12 it is a
list you can search. Choosing one gives the node's Value output that property's type,
so a step reading it gets a value of exactly that type, and the card's summary names
the property. For example, a library step gives a `User` record:

```csharp
public record User(string UserName, string? Email, int Age);
```

User → Select property (UserName) → Log: the output is Text, and the Log writes the
actual user name. Choosing Age instead makes the output a Number (`int`).

The properties of a type are its public instance properties you can read and its
public instance fields, inherited ones included; static members, indexers and members
the compiler writes for itself are not listed. Object, List, Text, numbers, entity IDs
and enums have none: connecting one is refused with what to use instead (Get field
for an object or a JSON list, For each for a C# list or array it can go through, Read
entity for an entity).

- Until a value is connected the select is off and says "Connect a value to choose a
  property". Until a property is chosen the output has no type, and a wire from it is
  refused: "Choose a property in Select property to use its value".
- Choosing a property is one undo step. A wire from the output that no longer fits
  the new type is removed in the same step, and a notice says which.
- Connecting a value of another type keeps the chosen property when that type has
  one of the same name; otherwise the select shows it as missing and the node has an
  error, "User has no property Level; choose another".
- Dropping a wire from an output whose type has properties on empty canvas offers
  Select property in the quick-add list.
- Nothing can be typed into Value: it takes a wire only. A value stored there (by
  hand or by an older editor) is refused: "Connect a value to Select property; nothing
  can be typed in here".
- When the value is empty (null) at run time, the step that reads the node fails:
  "Select property (used by Log): UserName cannot be read because the value is empty".
  A property whose getter throws fails the step with the getter's own message.

If and State condition choose True/False paths; most actions also offer an Error path,
which receives the failure instead of ending the run. Parallel branches and bounded
loops have structured scopes. Branch/body outputs do not automatically escape. Loop carry values retain state across sequential iterations; explicit
exports publish selected values when the loop completes. Each declaration has a
type and required initial operand. For each validates a declared item type, snapshots
its input and exposes item/index only inside its body. A loop may lower its own
iteration cap; the shared catalog defines global node/edge/nesting/step limits.
Scope snapshots and unsupported live resources are described in
[custom node libraries](node-libraries.md).

## Types and refinement

Types include True/False (`boolean`, whose values read True and False), Number
(`number`), Text (`string`), Object (`object`), List (`array`), entity IDs, Home
Assistant targets and the CLR types exposed by custom nodes. In the editor's right
panel each input and output row shows both the friendly type name and the C# type,
for example "True/False · `bool`". Some types
**refine** another: `entity_id` refines `string`, and `target` (an object of
`entity_id`, `device_id`, `area_id`, `floor_id` and `label_id`) refines `object`.
Entity IDs can be narrowed further to one domain with the parametric type
`entity_id:<domain>`, such as `entity_id:light`, whose values must look like
`light.<name>`.

Two rules come first. Any number type fits any other: `number` and every CLR
number type (`int`, `double`, `decimal` and the rest) go into one another, and a
value that does not fit the narrower type (2.5 for a whole number, 300 for a byte)
fails the step at run time, naming the step and the input. And a nullable value
(such as `int?`) never fits an input that cannot hold null, even where C# would
convert it explicitly.

Otherwise a value fits an input when its type is the input's type, or when the
input's type refines nothing and the value's root type is assignable to it (a CLR
conversion that C# makes implicitly). When the input's type refines another type,
only that type or a narrower one fits. So `entity_id:light` fits an `entity_id` or `string`
input, but a plain `entity_id` does not fit an `entity_id:light` input: nothing
proves it names a light. A literal must satisfy the pattern of its own type and of
every type it refines.

The Call service node gives its target and fields these narrow types when the
service restricts them. A plain entity ID literal, or a wired plain entity ID, in
the target of `light.turn_on` is therefore an error. Write the literal with the
exact type instead:

```json
{"kind": "literal", "type": "entity_id:light", "value": "light.living_room"}
```

For a wired value, add a **Convert** node whose type is `entity_id:light` between
the producer and the service. Convert checks the value each time the service
reads it; when the value is not a light the service fails and follows its own
Error path. The example "Light named in an
event" in [`authoring_examples.json`](../custom_components/hass_sharp/authoring_examples.json)
reads an entity ID from event data with Get field, converts it and turns that
light on. A trigger's own entity output is usually narrow already (see below), so
it needs no Convert.

## Triggers and their outputs

A Trigger supports manual, startup, entity changes, daily, once and native HA
configuration. Native HA declarations use the usual `trigger`/`platform` and
platform-specific fields. Discover installed trigger descriptions and preview the
complete graph; validation never attaches a listener. Saving a valid declaration
attaches it through Home Assistant. Disabled native declarations remain disabled.
Manual eligibility is independently controlled by `manual`.

Every trigger has a **Trigger data** output with the whole normalised payload of
the run, whatever started it:

| Cause | Payload |
|---|---|
| Native HA trigger | Home Assistant's `trigger` variables plus `context` |
| Entity change | `{platform: "entity", entity_id, from_state, to_state}`, states as `{entity_id, state, attributes, last_changed, last_updated, last_reported}`, with `last_updated` always `null` |
| Daily or once | `{platform: "schedule", schedule_id, planned}` with `planned` in ISO-8601 UTC |
| Manual run of an entity trigger, or of a native state or numeric state trigger | What a real firing would carry, with the entity's current state as both `from_state` and `to_state`, plus `manual: true` (see below) |
| Any other manual run, or startup | `{platform: "manual"}` or `{platform: "startup"}` |

Triggers also have typed outputs for their usual values. Entity triggers and the
native state, numeric state and template triggers offer **Entity**, **New state**
and **Previous state**; the states are `HassSharp.EntityStateSnapshot` values
ready for Entity field or custom nodes. Zone, event, time, sun, Home Assistant,
webhook and MQTT triggers offer their own values (zone and event, event type and
data, time, JSON body and query, topic and payload), and daily and once triggers
offer the planned time. The catalog's `triggerOutputs` table lists them.

When the trigger names entities of a single domain, its Entity output is typed
`entity_id:<domain>`, so it connects straight to a domain-filtered service
target. The example "Heater off after 30 minutes" wires a state trigger's Entity
into `climate.set_hvac_mode` and its New state into an Entity field that reads the
heater's name for the notification.

An output whose value is missing from the payload is not produced. A step that
reads it fails with an error naming that trigger output and follows its Error path
when connected.

**Run now** tests a graph with the values its trigger would really have. A manual
run of an entity trigger gets `{platform: "entity", entity_id, from_state,
to_state, manual: true}`; a native state or numeric state trigger gets
`{platform, entity_id, from_state, to_state, manual: true}` for its first entity,
plus its configured `id` and `attribute`, and a numeric state trigger adds its
configured `below` and `above`. `for`, `description` and `context` are left out:
only a real firing knows them. Both states are the entity's current state, read as
Read entity reads it, so Entity, New state and Previous state resolve and the run
trace shows them. The run is still recorded as a manual run and its summary names
the entity, never a change. A manual run of any other trigger, or of an entity that
does not exist or whose state cannot be read (a warning is logged), carries
`{platform: "manual"}`, and a step reading one of those outputs fails as above.

Daily schedules require a local time and an explicit time zone. Once schedules
require an ISO timestamp with an explicit offset or Z. Missed daily starts and
DST gaps are skipped; repeated local times fire once. Startup is opt-in by trigger
type or its override. Every trigger selects Single, Restart, Queued or Parallel
for the whole invocation. Cancellation stops cooperative work; it cannot undo an
already-dispatched service call.

Trigger IDs remain stable through renames and layout changes. Generated methods
are `Run_` plus uppercase UTF-8 hexadecimal of the node ID. MCP accepts the stable
triggerId and resolves the method inside the managed revision gate.

## Calling services

The **Call service** node calls one Home Assistant service, chosen in its
`service` setting as `domain.service`. Its inputs are:

- **Target**: an entity ID or a target object. A string is sent as `entity_id`.
  An object may hold only `entity_id`, `device_id`, `area_id`, `floor_id` and
  `label_id`; any other key is an error.
- **Fields**: only the fields you add to the node. Each field becomes its own
  input port, so it can be a literal or a connection like any other input.
- **Response**: turn on **Return response** to request the service response and
  get a **Response** object output. The output exists only while it is on.

The editor, preview and save check the node against the services Home Assistant
has registered: field types, required fields, allowed values and ranges, the
target's entity domains, whether the service takes a target at all, and whether
Return response matches the service (some services never return a response,
some only return one). A service Home Assistant does not know is a warning, not an
error, and its fields then accept any type. A Call service node never stops an
automation from loading at startup, whichever services are registered then: a
service that disappears later fails only when the node runs. Custom C# and node
library code that calls `context.Services` is different: it needs the service to
compile; see [states and services](node-libraries.md#states-and-services).

Pick the service and entities from the editor's searchable lists rather than
typing IDs: they list what this Home Assistant has registered.

## Harmless complete example

This complete graph writes a log only when manually run. Save it with a stable
ID and expected revision 0 to create it; use the current revision for later edits.
Positions may be omitted on new graphs; the server fills only missing positions
and preserves supplied coordinates. Nodes have no stored size: the editor sizes
them to their ports.

```json
{
  "version": 5,
  "name": "Manual log",
  "nodes": [
    {"id":"start","kind":"trigger","config":{"type":"manual","manual":true}},
    {"id":"log","kind":"log","config":{"value":{"kind":"literal","type":"string","value":"Hello from HassSharp"}}}
  ],
  "edges": [{"id":"flow","source":"start","target":"log","port":"next"}]
}
```

More complete examples are in
[`authoring_examples.json`](../custom_components/hass_sharp/authoring_examples.json)
and are also returned by the MCP `get_capabilities` tool: a native event, a bounded
loop, a Custom C# node, lights at sunset with typed fields, a heater switched off after
30 minutes with a notification, a light named in an event, a service response, and
a library with a value node. Every example validates and compiles with and without the
service schemas. Substitute your own entities and services before saving one.

## Run history and traces

Each visual automation lists its runs, newest first: what started each run, when,
how long it took and how it ended. A run is Pending, Running, Succeeded, Failed,
Cancelled, Skipped, Interrupted (still unfinished when Home Assistant restarted)
or Stopped (a Stop step ended it). A cancelled run records why: a newer trigger restarted it,
the automation was reloaded, or Home Assistant was stopping.

Every visual run also records a trace: each step that ran, in order, with the path
it took, its timing, its input and output values and any error, together with the
exact graph revision that ran. Values are shortened beyond fixed size limits, and
a very long run keeps the path of its oldest steps without their values. Traces
are kept for the newest 1,000 traced runs. MCP clients read them with the `trace`
tool; see [MCP authoring](mcp-authoring.md).

## C# nodes

A Custom C# node is one public static method marked `[Node]`: its parameters are the
node's inputs and its return value is its output. A `NodeContext` parameter gives it
the run's cancellation, run id, trigger data, error details, entity states, typed
service calls (`context.Services.Light.TurnOn(...)`) and the log; see
[states and services](node-libraries.md#states-and-services). The same methods in a [node library](node-libraries.md) become
node kinds every automation can use. Compiler errors map back to the node and to the
line in its code. Custom code is trusted administrator code, and preview compiles it
without executing it. **Edit code**, on the node's card and in its panel, opens the
code in a full-screen editor with completion, hover, signature help and live
diagnostics; **Apply** writes the code and its pins to the node. See
[Custom C# nodes](node-libraries.md#custom-c-nodes).

Editor and [MCP authoring](mcp-authoring.md) use the same revision-checked services.
On a conflict, reload the current document and reconcile edits. Library changes
advance every graph revision, so refresh open graphs after saving a library.
