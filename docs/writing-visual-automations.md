# Writing visual automations

HassSharp supports **graph version4 only**. Older graph formats must be recreated;
there is no migration or backward-execution mode. Document and API envelope
version1 is separate from the graph version.

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

Inputs can be literals, C# expressions, explicit unset values, or connected data
ports. Types include booleans, numbers, strings, entity IDs, JSON values and the
full CLR types exposed by custom nodes. Null and absence are distinct. Use the
server catalog for assignability, nullability and literal choices; explicit
conversion nodes perform checked conversions where supported.

Read entity returns a detached `HassSharp.EntityStateSnapshot`. Use Entity field
to extract a scalar state/attribute, or pass the snapshot directly to a typed
custom method. Service nodes accept target/data objects and can expose responses.
Select real services and entity IDs using Home Assistant discovery rather than
guessing fields.

If chooses True/False paths; actions also offer Error. Try/Catch/Finally, Sequence
and bounded loops have structured scopes. Branch/body outputs do not automatically
escape. Loop carry values retain state across sequential iterations; explicit
exports publish selected values when the loop completes. Each declaration has a
type and required initial operand. Foreach validates a declared item type, snapshots
its input and exposes item/index only inside its body. A loop may lower its own
iteration cap; the shared catalog defines global node/edge/nesting/step limits.
Scope snapshots and unsupported live resources are described in
[custom node libraries](node-libraries.md).

## Harmless complete example

This complete graph writes a log only when manually run. Save it with a stable
ID and expected revision0 to create it; use the current revision for later edits.
Positions and sizes may be omitted on new graphs; the server fills only missing
layout and preserves supplied coordinates.

```json
{
  "version": 4,
  "name": "Manual log",
  "nodes": [
    {"id":"start","kind":"trigger","config":{"type":"manual","manual":true}},
    {"id":"log","kind":"log","config":{"value":{"kind":"literal","type":"string","value":"Hello from HassSharp"}}}
  ],
  "edges": [{"id":"flow","source":"start","target":"log","port":"next"}]
}
```

Complete native-event, bounded-loop, custom-C# and typed-library examples are in
[`authoring_examples.json`](../custom_components/hass_sharp/authoring_examples.json).
They are also returned by the MCP `get_capabilities` tool. The custom library
example connects Read entity to a record-returning method, an interface consumer
and a Log node. Substitute an installed entity before running it.

## Triggers and concurrency

A Trigger supports manual, startup, entity changes, daily, once and native HA
configuration. Native HA declarations use the usual `trigger`/`platform` and
platform-specific fields. Discover installed trigger descriptions and preview the
complete graph; validation never attaches a listener. Saving a valid declaration
attaches it through Home Assistant. Disabled native declarations remain disabled.
Manual eligibility is independently controlled by `manual`.

Daily schedules require a local time and an explicit time zone. Once schedules
require an ISO timestamp with an explicit offset or Z. Missed daily starts and
DST gaps are skipped; repeated local times fire once. Startup is opt-in by trigger
type or its override. Every trigger selects Single, Restart, Queued or Parallel
for the whole invocation. Cancellation stops cooperative work; it cannot undo an
already-dispatched service call.

Trigger IDs remain stable through renames and layout changes. Generated methods
are `Run_` plus uppercase UTF-8 hexadecimal of the node ID. MCP accepts the stable
triggerId and resolves the method inside the managed revision gate.

## C# escape hatch

Custom C# nodes run statements with `values`, `responses`, `lastError` and normal
Automation members. For example, `Logger.Info("Hello from a C# node");` is a
complete harmless node body. Graph `imports` and `members` provide shared imports
and class members. Compiler errors map back to node/input/source positions.
Custom code is trusted administrator code, and preview compiles it without
executing it. Use [custom libraries](node-libraries.md) for reusable typed methods,
and the [ordinary C# guide](writing-csharp-automations.md) for `.cs` automations.

Editor and [MCP authoring](mcp-authoring.md) use the same revision-checked services.
On a conflict, reload the current document and reconcile edits. Library changes
advance every graph revision, so refresh open graphs after saving a library.
