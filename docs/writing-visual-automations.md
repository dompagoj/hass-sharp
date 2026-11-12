# Writing visual automations

Choose **Automations → New automation → Visual**, enter a name, and create the
draft. The starter graph contains an entity trigger, a state condition and a
service call. Adapt their IDs and settings to your Home Assistant instance.

**Save graph** validates, compiles and activates the automation. A startup trigger
can execute on save. Unsaved edits remain a draft. Use **Run now…** and run history
after saving to test manual actions and inspect results.

## Add, inspect and connect steps

- Click a palette item to read its description, configuration and connections in
  **Step details**. This does not create a node.
- Drag it onto the canvas to create a node at the drop position.
- Click empty canvas to search for a step there. **Add step** opens the same
  chooser at the viewport center. Type to filter, use arrows and Enter to select,
  or Escape to cancel.
- Drag an output to empty canvas to create and connect a compatible step. Undo
  removes the new node and connection together.
- To connect existing steps, drag an output handle to a target input, or select
  a node and use **Output**, **Connect to**, and **Connect** in its inspector.

Each output has one connection. Several paths can enter the same non-trigger
step. Triggers cannot receive connections. Graphs cannot contain loops or parallel
forks. Conditions offer **If true**, **If false**, and **On error**; other action
steps offer **Next** and **On error**. Triggers offer **Next** only.

## 1. Turn on a hallway light when motion starts

Configure the three starter nodes as follows, substituting IDs from your home:

| Node | Fields | Connection |
| --- | --- | --- |
| Entity changes | Trigger type: **Entity changes**; Entity ID: `binary_sensor.hall_motion`; Concurrency mode: **Single** | **Next → State condition** |
| State condition | Entity ID: `binary_sensor.hall_motion`; Required state: `on` | **If true → Call service** |
| Call service | Service: `light.turn_on`; Entity ID: `light.hall` | Leave outputs empty |

The trigger starts a run when the entity changes. The condition matters: a motion
sensor also changes when motion stops. Only the `on` branch turns the light on.
The unconnected **If false** output ends that run without doing anything.

## 2. Turn the light off after motion has been clear for one minute

Create a separate visual automation with these steps:

| Node | Fields | Connection |
| --- | --- | --- |
| Entity changes | Entity ID: `binary_sensor.hall_motion`; Concurrency mode: **Restart** | **Next → State condition** |
| State condition | Entity ID: `binary_sensor.hall_motion`; Required state: `off` | **If true → Wait** |
| Wait | Seconds: `60` | **Next → Call service** |
| Call service | Service: `light.turn_off`; Entity ID: `light.hall` | Leave outputs empty |

Set **Allow manual runs** to **Disabled** if you only want motion changes to start
this flow. `Restart` asks the previous invocation to cancel when the sensor changes
again. The Wait step respects cancellation, so new motion interrupts the pending
off action. Cancellation cannot undo a service already dispatched.

## 3. Turn on a light every morning

Use a trigger and a service node. Remove the starter condition and reconnect the
trigger directly to the service.

| Node | Fields | Connection |
| --- | --- | --- |
| Entity changes (rename it “Morning”) | Trigger type: **Daily**; Daily time: `07:30`; Time zone: `Europe/Zagreb`; Concurrency mode: **Single** | **Next → Call service** |
| Call service | Service: `light.turn_on`; Entity ID: `light.hall` | Leave outputs empty |

The palette's “Entity changes” building block can become any supported trigger
through **Trigger type**. Daily schedules require an explicit time zone. Missed
starts are skipped; daylight-saving gaps are skipped and repeated times fire once.
For a single future run, choose **Once** and enter a timestamp with an offset, for
example `2030-12-01T07:30:00+01:00`.

## 4. Branch and handle an error

To mirror a switch's state to a light, use these connections:

| Node | Fields | Connection |
| --- | --- | --- |
| Entity changes | Entity ID: `input_boolean.hall_light_enabled` | **Next → State condition** |
| State condition | Same Entity ID; Required state: `on` | **If true → Turn light on**; **If false → Turn light off** |
| Call service (label “Turn light on”) | Service: `light.turn_on`; Entity ID: `light.hall` | Optional **On error → Recovery step** |
| Call service (label “Turn light off”) | Service: `light.turn_off`; Entity ID: `light.hall` | Leave outputs empty |
| Call service (label “Recovery step”) | Service: `switch.turn_on`; Entity ID: `switch.backup_lamp` | Leave outputs empty |

Use a real recovery action and entity that make sense in your home. An error
output runs when that step fails; **If false** is a normal condition result, not
an error. Picking or dragging a particular output preserves that branch. Used
outputs cannot acquire another connection until you remove the existing one.

## Entity suggestions

Entity ID fields search by ID and friendly name, ignoring case. Selecting a
suggestion writes the exact ID. You can also type an ID manually. Suggestions
refresh when the picker opens and include registered entities without a current
state. Those entities may still be unavailable when the automation executes.

Loading or connection errors do not erase your text. Use **Retry** to fetch again.
Administrators can also use **Refresh entity IDs** on the Automations page to
refresh generated C# symbols and invalidate the picker cache. Refresh does not
restart or run automations.

## Editing and troubleshooting

- Drag a node to move it; resize it with its handles or the Width/Height fields.
- Undo and Redo apply to graph edits. Palette descriptions and canceled choosers
  do not add history entries.
- Use Graph JSON or Export graph to inspect or back up your draft.
- Select a diagnostic to find the related node and generated source.
- Graph v1 allows one trigger. **Upgrade to graph v2** enables more triggers and
  C# imports/members. Palette descriptions remain available before upgrading.
- In graph v2, trigger nodes can share downstream steps. Concurrency policies
  apply per trigger action, not across every trigger in the graph.

For logic that needs loops, concurrency or richer service data, use a Custom C#
step or [write a C# automation](writing-csharp-automations.md).
