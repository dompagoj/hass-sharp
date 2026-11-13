# Writing visual automations

HassSharp supports **graph version 5 only**. Older graph formats must be recreated;
there is no migration or backward-execution mode. Document and API envelope
version 1 is separate from the graph version.

Choose **Automations → New automation → Visual**, enter a name, and edit the
canvas. Drag steps from the palette, inspect their settings, and connect control
and typed data ports. The inspector uses the same catalog as compiler validation
and MCP. Custom library nodes appear after a successful library save.

**Save graph** validates, compiles, persists and activates. Native trigger
configuration is checked by Home Assistant before saving. Warnings remain visible;
errors prevent persistence. Saving startup/scheduled/native triggers can cause
external effects. Unsaved changes are drafts. Run now requires the exact saved
revision to be active and the selected trigger to permit manual execution. If
activation fails, an older runtime may remain active; the editor reports the
failure and disables running the newer saved revision.

## Control flow and typed data

Control edges schedule execution. A control output has one destination; multiple
paths may enter a non-trigger step. Triggers cannot receive control inputs.
Connections cannot create cycles; use bounded Repeat, While or Foreach loops.
Data edges supply typed values and **never execute their source nodes**. The
producer must have executed on the chosen path before the consumer reads it.
Missing producer output is a mapped runtime error, not an implicit default.

Inputs can be literals, named variable references, explicit unset values, or
connected data ports. A connection replaces the input's own value. Use Custom C#
nodes for calculations; graph imports and members and trigger override
expressions provide the existing C# escape hatches. Null and absence are distinct.

Read entity returns a detached `HassSharp.EntityStateSnapshot`. Use Entity field
to extract a scalar state/attribute, or pass the snapshot directly to a typed
custom method.

If and State condition choose True/False paths; most actions also offer an Error path,
which receives the failure instead of ending the run. Parallel branches and bounded
loops have structured scopes. Branch/body outputs do not automatically escape. Loop carry values retain state across sequential iterations; explicit
exports publish selected values when the loop completes. Each declaration has a
type and required initial operand. Foreach validates a declared item type, snapshots
its input and exposes item/index only inside its body. A loop may lower its own
iteration cap; the shared catalog defines global node/edge/nesting/step limits.
Scope snapshots and unsupported live resources are described in
[custom node libraries](node-libraries.md).

## Types and refinement

Types include booleans, numbers, strings, objects, arrays, entity IDs, Home
Assistant targets and the CLR types exposed by custom nodes. Some types
**refine** another: `entity_id` refines `string`, and `target` (an object of
`entity_id`, `device_id`, `area_id`, `floor_id` and `label_id`) refines `object`.
Entity IDs can be narrowed further to one domain with the parametric type
`entity_id:<domain>`, such as `entity_id:light`, whose values must look like
`light.<name>`.

A value fits an input when its type is the input's type, or when the input's type
refines nothing and the value's root type is assignable to it (CLR conversions
are checked by Roslyn). When the input's type refines another type, only that
type or a narrower one fits. So `entity_id:light` fits an `entity_id` or `string`
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
the producer and the service. Convert checks the value when it runs and follows
its Error path when the value is not a light. The example "Light named in an
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
| Manual run or startup | `{platform: "manual"}` or `{platform: "startup"}` |

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
when connected. A manual run of a state-triggered graph, for example, has no
Entity: set `manual` to false on triggers whose graph depends on those outputs.

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

- **Target**: an entity ID or a target object. A string is sent as `entity_id`;
  the properties of an object are sent as they are.
- **Fields**: only the fields you add to the node. Each field becomes its own
  input port, so it can be a literal or a connection like any other input.
- **Response**: turn on **Return response** to request the service response and
  get a **Response** object output. The output exists only while it is on.

The editor, preview and save check the node against the services Home Assistant
has registered: field types, required fields, allowed values and ranges, the
target's entity domains, whether the service takes a target at all, and whether
Return response matches the service (some services never return a response,
some only return one). A service Home Assistant does not know is a warning, not an
error, and its fields then accept any type. Loading saved automations at startup
never depends on which services are registered, so a service that disappears
later fails only when the node runs.

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
loop, custom C#, lights at sunset with typed fields, a heater switched off after
30 minutes with a notification, a light named in an event, a service response, and
a typed custom library. Every example validates and compiles with and without the
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

## C# escape hatch

Custom C# nodes run statements with `values`, `lastError`, `TriggerData` (the
normalised trigger payload) and normal Automation members. For example,
`Logger.Info("Hello from a C# node");` is a complete harmless node body. Graph
`imports` and `members` provide shared imports and class members. Compiler errors
map back to node/input/source positions. Custom code is trusted administrator
code, and preview compiles it without executing it. Use
[custom libraries](node-libraries.md) for reusable typed methods, and the
[ordinary C# guide](writing-csharp-automations.md) for `.cs` automations.

Editor and [MCP authoring](mcp-authoring.md) use the same revision-checked services.
On a conflict, reload the current document and reconcile edits. Library changes
advance every graph revision, so refresh open graphs after saving a library.
