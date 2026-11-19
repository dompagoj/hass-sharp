# Entity definitions on the canvas: design

Revises `2026-11-17-custom-entities-design.md`. Where the two disagree, this one holds.

## Context

Custom entities were added after `0.0.46-beta` and have not shipped. Using them showed four
problems:

1. A definition can be "a C# class from the node library" instead of a graph. That second editor
   does not belong: C# should be written in a node, as the Custom C# step is.
2. Entities and the device are declared in a form in the right panel, and nodes point at them by a
   dropdown. The graph alone does not say what the definition is.
3. In the entity editor the Entity group is last in the palette.
4. Clicking a palette item drops a node on the canvas.

The intended outcome: **a definition is only a logical grouping, like an automation.** Everything
Home Assistant sees (entities, devices, their behaviour, C# entities and devices) is a node on the
canvas.

## Decisions (yours)

1. **The class-from-library kind is deleted**, with its option in the New dialog. The node editor
   is the only editor.
2. **Each entity is a hub node.** One palette item, "Entity", starting as a binary sensor. Its
   state values are inputs on the left (this replaces the State node). Each command is an
   execution output on the right with that command's values (this replaces When commanded). A
   camera's "frame needed" is an output of the camera's entity node. Name, key, type, device class,
   icon, category and options are edited in the right panel while the node is selected.
3. **A definition affects nothing itself.** It is not a device. It groups nodes, as an automation
   does.
4. **Each device is a node.** A plain Device node has an Entities pin; each entity node has a Device
   pin in its header; a wire between them is the membership. An entity node without that wire is a
   standalone entity. A definition may hold several devices.
5. **C# entity node.** Its code is one class deriving from a typed entity (`SharpSwitch`,
   `SharpCamera` and the rest), written in the editor the Custom C# step uses. One node is one
   entity.
6. **C# device node.** Its code is one `SharpDevice` class that adds several entities, as the
   SmartPlug example does today. Each such node is its own device.
7. **Palette.** In the entity editor the Entity group is first. Clicking an item shows its
   description in the right panel. Only dragging it onto the canvas creates a node.
8. **Subagent-driven development with Opus 5.5 subagents** for all implementation.

## Decisions (mine, for you to correct)

- Set state, Set available and Show frame stay as steps and name their entity by a picker (the
  card reads "Set state ▸ Lamp"), not by a wire. A wire to every such step would cross the canvas.
  When started stays as it is.
- The Trigger node stays in the entity editor, after the Entity group. It is how a definition
  polls or reacts to the outside; the "Plug over HTTP" example depends on it.
- A definition's Settings stay in the right panel when nothing is selected, and the Setting node
  still reads them. The Entities and Device sections there are removed.
- `[Setting]` properties of a C# node become input pins. A pin takes a typed value or a direct wire
  from a Setting node: settings are applied before `Setup()`, before any flow can run. A secret
  setting refuses a typed value.
- A standalone entity must have a name. The fallback to the definition's name is removed. An
  entity on a device with no name takes the device's name, as Home Assistant does.
- Deleting an entity node deletes the entity, with a confirmation when steps use it. Deleting a
  device node leaves its entities standalone.
- Moving an entity between devices keeps its registry entry and its history: its unique id stays
  `<definition id>:<key>`. The entities a C# device adds are
  `<definition id>:<device key>:<entity key>`, so two plugs of the same class do not clash.
- The palette's description panel says "Drag onto the canvas, or press A": dragging does not work
  from the keyboard or by touch, and quick-add does.
- The palette change applies to the automation editor too. It is one shared component.
- Definitions saved by earlier development builds fail to load with one message: "saved by an
  earlier development build; delete it and build it again". Nothing shipped, so nothing is
  migrated. The document version is raised in each phase that changes the format.

## Goals, non-goals, anti-goals

Goals
- Reading the canvas tells you every entity and device a definition makes, what each entity's
  state comes from and what each command does.
- Everything the class kind could do is still possible, in a node.
- Each phase below works on its own and leaves every suite green.

Non-goals
- Entities of a C# device as nodes of their own. They are known only once the class has run.
- Wiring values other than a Setting into a C# node's setting pins.
- Migrating definitions saved by development builds.

Anti-goals
- No automation changes or needs migrating. The graph version (5) is shared with automations and
  is not raised.
- Unloading or disabling never purges the registry; that rule of the first design stands.
- No user class is instantiated when a definition is previewed.

## Approaches

**Where entities live.** (a) The graph's nodes are the only source, and the server derives the
`entities` and `devices` lists the host, the bridge and the summaries read. (b) The stored list
stays and nodes point at it. Chosen (a): one source and nothing to keep in step. A request that
still sends `entities` is refused.

**Command values on the hub.** (a) Reuse the Custom node's scope ports. (b) Keep today's per-flow
outputs and add one static rule. Chosen (b): scope ports would stop two command flows sharing a
later step, which works today, and would use one of the eight nesting levels.

**The device wire.** (a) A third edge kind. (b) A data edge of a new `device` type that is never
evaluated. Chosen (b): the canvas, the connection checks and the limits already handle data edges.
The type is marked structural, so `$any` pins refuse it and the runtime skips it.

**The C# nodes.** (a) A new compile path. (b) The Custom node's pipeline (a compile per node, a
stored signature, the preview operation, the same editor, apply-and-refit) together with the class
kind's own describe, settings and life-cycle code. Chosen (b).

## Shapes

New kinds, all with `documents: ["entity"]`, in category Entity and listed in this order:

| Kind | Config | Pins |
|---|---|---|
| `entity` (a root) | `key`, `domain`, `name`, `icon`, `deviceClass`, `entityCategory`, `enabledByDefault`, `options{}`, `history{state, commands, frame}`, `arguments{}` | In: `device`, and one per state value, each optional. Execution out: `command:<cmd>`, and `frame` for a camera. Data out: `command:<cmd>:<value>`, grouped under its command |
| `device` | `key`, `name`, `manufacturer`, `model`, `swVersion`, `hwVersion`, `configurationUrl`, `suggestedArea`, `via`: null, `{deviceId}` or `{device}` | Out: `entities`, of type `device` |
| `csharp_entity` | `key`, `source`, `signature{title, description, domain, settings[]}`, `arguments{}` | In: `device`, and one per `[Setting]` |
| `csharp_device` | `key`, `name`, `via`, `source`, `signature{…, settings[]}`, `arguments{}` | In: one per `[Setting]` |

- These kinds declare things; they are not steps or values. Two catalog flags say so: `declares`
  (`"entity"` or `"device"`) and, for those that are not roots, `declaration: true`, which keeps
  them out of the reachability and unused-value checks. The `device` type and input are
  `structural`.
- A hub's state values are stored under `arguments`, as the Custom node's are: pin ids such as
  `value` and `options` would otherwise collide with config keys.
- A hub flow's method is `Run_<hex(node id)>_<hex(port)>`. Triggers and When started keep theirs.
- Removed kinds: `entity_state`, `entity_command`, `camera_frame`.
- A request's body is `{name, settings, logic: {kind: "graph", graph}}`. The stored document adds
  two lists the server derives and nobody writes: `entities[]` and `devices[]`. A C# device's
  `entities` is null until it has run.
- The editor's graph keeps `graph.definition = {settings}`. It must stay an object, since that is
  what marks an entity document.
- The bridge receives `{definitionId, name, devices: [{key, name, …, via}], entities: [{uniqueId,
  key, domain, …, device}]}`. A device's identifier is `(DOMAIN, "<definition id>:<device key>")`.

## Validation

Every rule is in the C# validator and its TypeScript mirror, with a case in
`tests/fixtures/visual-graph-validation.json` written before either implementation.

- Gone, because one node per entity makes them impossible: `state.duplicate`,
  `command.duplicate`, `camera.duplicate`.
- Moved onto the node: the key, option and duplicate-key errors; `command.unhandled` and
  `camera.no_frame`, now naming the port.
- New for the hub: `entity.partialState` (some but not all required state values given),
  `scope.stateSource` (a state input reads a step's output), `scope.flow` (a command's value read
  by a step another flow can also reach), `entity.name` (a standalone entity without a name).
- New for devices: `definition.duplicateDevice`, `device.viaUnknown`, `device.viaCycle`, and the
  warning `device.empty`.
- New for C# nodes: `csharp.signature`, `scope.settingSource`, `setting.secretLiteral`.

## Phases

1. **Palette.** Entity group first; click describes; drag places. Frontend only.
2. **Entities as hub nodes.** In three steps, each green: derive the entity list from the graph
   with the old kinds still present; add the hub kind to the generator, the runtime and the
   editor; remove the old kinds and the panel's Entities list.
3. **Devices as nodes.** Several per definition; the bridge keys devices by definition and device
   key. A class definition goes through as one implicit device, `main`, until phase 5.
4. **C# entity and C# device nodes.**
5. **Delete the class kind** and what nothing else uses.

Each phase has its own plan under `docs/superpowers/plans/`.

## What is left to the builder

- The card layout of the hub: how the Device pin sits in the header and how a command's values
  are grouped under it.
- The wording of every new message and of the description panel.
- How the New dialog reads once it has only a name.
- Whether `logic` is flattened out of the request body once only graphs remain.
