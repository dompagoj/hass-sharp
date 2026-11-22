# Entity definitions API: envelope 1

An entity definition creates Home Assistant entities, optionally grouped into
devices, whose behaviour comes from a graph (version 5), in which an entity or a
device can also be one C# class: a C# entity node or a C# device node. The guide is
[custom entities](../custom-entities.md); the MCP tools that
call this API are in [MCP authoring](../mcp-authoring.md). The graph contract,
the library API and the authoring gate are those of
[visual automations](visual-automations.md).

## WebSocket command

Administrator WebSocket requests use:

```json
{"id":12,"type":"hass_sharp/entities","request":{"operation":"read","id":"motion_in_either_room"}}
```

The envelope is the visual API's: `{version:1,ok:true,result:...}` or
`{version:1,ok:false,error:{code,detail}}`. A non-administrator gets HA's
`unauthorized`, and while no entry is set up every request answers `not_ready`.
An unexpected failure is logged and answered as `entity_request_failed`, "Entity
request failed; check integration logs".

| Operation | Request fields | Result |
|---|---|---|
| list | view?: `"home"`, todayUtc? | Document array without `source` and `sourceMap`, with `devices` a count (its Device nodes and C# device nodes); `home` adds `recentRuns` (at most 10, newest last), `runsToday`, `failedLastDay` and `lastRunUtc` to each |
| read | id | Complete document |
| preview | definition, deriveSignatures? | `{valid,diagnostics,source,sourceMap}`, and `graph` when `deriveSignatures` is true |
| save | id, expectedRevision, definition, enabled?, deriveSignatures? | `{id,saved:true,document,active,activationError,diagnostics?}` |
| set_enabled | id, enabled | `{id,enabled,active,activationError}` |
| delete | id, expectedRevision | `{deleted:true}` |
| run | id, triggerId | `{version:1,runId}` |

Any other operation answers `invalid_request`, "Unknown entity operation", and a
`view` other than `home`, "Unknown list view; the entity definitions list has the
view home".

Error codes: `validation` (its `detail` is the preview `{valid,diagnostics,source,sourceMap}`),
`conflict` (the expected revision is not the stored one), `not_found` ("Entity
definition not found"), `invalid_request` (a malformed request, with the reason, and
a refused `run`; a stored file that is not a definition answers "Invalid entity
definition envelope"), `storage` (the file could not be read or written) and
`internal`.

## The definition

`definition` is the body a client sends:

```text
{name, settings?, logic: {kind:"graph", graph}}
```

- `name`: 1 to 120 characters.
- `logic`: `{kind:"graph", graph}` with a version 5 graph. A `logic` of any other
  kind, or none, is refused with that one problem beside the name's,
  `definition.logic`: `A definition is built from nodes: logic is {kind: "graph",
  graph}. Write C# in a C# entity or C# device node`.
- A graph's entities are its Entity nodes (kind `entity`, any number), each with
  `config` `{key, domain, name?, icon?, deviceClass?, entityCategory?,
  enabledByDefault?, options?, history?, arguments?}`. `key` matches
  `^[a-z][a-z0-9_]{0,39}$` and is unique; `domain` is one of the ten in the node
  catalog's `entityDomains`, which also gives each domain's state values (the
  node's inputs), commands (its execution outputs `command:<command>`, with their
  values as data outputs `command:<command>:<value>`), pictures (`picture`: a
  camera's `image` input, the picture it shows, which a value wire from Camera
  frame or a Draw step gives and which reads no step, `scope.frameSource`; one with
  no wire is the warning `camera.no_frame`) and `options`; `entityCategory` is `config` or `diagnostic`. An entity on no device
  needs a `name`; a name that is blank once trimmed is no name (the entity has
  none). A graph with no Entity node, C# entity node or C# device node (whose code
  makes its entities) is refused with `definition.entitiesRequired`, "Add an Entity
  node: an entity definition creates the entities on its canvas, and has none". The answered document lists them under
  `entities`, for reading only, then its C# entities (see below).
- A graph's devices are its Device nodes (kind `device`, any number), each with
  `config`
  <!-- config: device -->
  `{key, name, manufacturer?, model?, swVersion?, hwVersion?,
  configurationUrl?, suggestedArea?, via?}`, and its C# device nodes. `key` follows the entity key rule and is
  unique among the devices, C# devices included (device and entity keys are apart); `name` is required;
  `configurationUrl` is `http://`, `https://` or `homeassistant://` (in any case)
  followed by an ASCII host name or an address in brackets, an optional port of at
  most 65535, and an optional path, query or fragment without white space, control
  characters, backslashes or characters beyond U+FFFF such as an emoji (narrower
  than Home Assistant's own check); `via`
  is `{deviceId:"<Home Assistant device id>"}` or `{device:"<key of another Device
  node or C# device node>"}`, and a key no device of the graph has or a circle is
  refused. An entity is on a device when a data edge goes from the Device node's
  `entities` output to the `device` input of the Entity node or C# entity node
  (`targetPort: "device"`); without one it stands alone. A Device node with no
  entity is a warning, `device.empty`. The answered document lists the devices under
  `devices`, and each of its `entities` has `device`, the key of its device or null,
  for reading only: a `devices` sent in a request is ignored.
- A graph's C# entity nodes (kind `csharp_entity`) have `config`
  <!-- config: csharp_entity -->
  `{key, source, signature?, arguments?}` and its C# device nodes (kind
  `csharp_device`)
  <!-- config: csharp_device -->
  `{key, name, via?, source, signature?, arguments?}`. `source` is one public class
  deriving from an entity class (whose domain is the entity's) or from `SharpDevice`;
  `signature` is `{title, description, domain, settings:[{key, label, description,
  type, secret, options?, minimum?, maximum?, domain?, required?}]}`, required
  unless the request has `deriveSignatures: true`, which writes it from the code. A
  setting's `type` is
  `string`, `integer`, `number`, `boolean`, `enum` or `entity` (a property of type
  `Entity`, or of a domain's class such as `LightEntity`, whose `domain` is then
  written); `required: true` is written only for a string or entity property that is
  not nullable and has no initializer. `arguments` holds a typed
  value per setting pin, `{kind:"literal", type, value}` (an entity setting's type
  `entity_id` or `entity_id:<domain>`, its value an entity id), keyed by the property's
  name, and a setting pin may instead take a data edge from a Setting step
  (`sourcePort: "value"`), the only way to give a secret one; an entity setting's pin
  takes none. A required setting's pin with neither is "`{Label} is required`". A C#
  entity's key is among the entity keys, a C# device's among the device keys. A C# entity node has a
  `device` input as an Entity node has; a C# device node has no `device` input (its
  only inputs are its setting pins, and no entity is wired to it), and `via` as a
  Device node's. Neither has outputs or execution ports.
- The derived lists. `entities` holds each Entity node's entity, then each C# entity
  node's, `{key, domain, name: null, device, csharp: true}` (its class names it, so
  no name is stored); `devices` holds each Device node's device, then each C# device
  node's, `{key, name, via?, csharp: true}`. The entities a C# device's code adds are
  in neither: they are known only once its class has run, in `liveEntities` and
  `registeredEntities`.
- `entities` is not part of a request. A definition that has the key at all,
  even an empty list or null, is refused with that one problem,
  `definition.entitiesNotAllowed` on input `entities`: "Entities are Entity nodes in
  the graph now: leave entities out and add one Entity node for each". Its name is
  checked first, so a name's problem is answered beside it; its graph, settings,
  Entity nodes and Home Assistant triggers are not checked.
- `device` is not part of a request either: a definition is not a device, and its
  devices are its Device nodes. A request whose `device` is not null is
  refused with that one problem, `definition.deviceNotAllowed` on input `device`:
  "Devices are Device nodes in the graph now: leave device out, add a Device node
  and connect its Entities pin to each entity's Device pin", after the name check
  and with nothing else checked (one with `entities` too is refused for `entities`
  alone). `device: null` is no device. A document
  stored with a device is read without it.
- `settings`: `{key, label?, type, value, secret?}` with `type` `string`, `boolean`
  or `number`.

The server never trusts a `definition` key a client puts inside `graph` (the graph
is stored without one), fills in missing node positions, and answers the document
with `version`, `id`, `revision`, `source`, `sourceMap`,
`catalogGeneration`, `enabled` and `savedUtc` added.

A save writes the document as `version: 3`, with the devices its Device nodes and
C# device nodes declare under `devices` and the entities its Entity nodes and C#
entity nodes declare under `entities`, as the derived lists above give them. A
version 1 or 2 graph document, from an earlier development build, does not load:
`list` leaves it out of `result` and logs `Skipping entity definition <path>: saved
by an earlier development build; delete it and build it again` once, `read` of its
id answers `invalid_request` with that sentence, and `delete` with
`expectedRevision` 0 removes the file and its entities.

Every `list` answer, with or without `view`, names each definition file it cannot
read beside its `result`: `{version: 1, ok: true, result: [...], unreadable: [{id,
reason, deletable}]}`, sorted by id, empty when every file reads. `reason` is "saved
by an earlier development build; delete it and build it again" for a version 1 or 2
graph document, and otherwise what reading the file met (such as "Invalid entity
definition envelope"). Such a definition can only be deleted, with
`expectedRevision` 0, when it is `deletable`: its file name is a definition id and
the file could be opened, only its content is wrong. One that is not (a file named
`Hall.json`, or one the process may not read) cannot be deleted through the API:
remove it from Home Assistant's `.storage/hass_sharp/entities` folder by hand. Until it is, the entities it made stay in Home Assistant as
unavailable (startup keeps its id, so they are not pruned), and a node library save,
which checks every definition, is refused with `Cannot read entity definition
<path>: <reason>`. The **Entities** page lists it apart, with its id and its
reason, and with **Delete** when it is `deletable`; one that is not has no
**Delete**, and the page says to remove the file by hand instead.

Ids match `^[a-z0-9][a-z0-9_-]{0,79}$`; `new` is refused ("Invalid definition ID:
new is reserved"). An entity's unique id is `<definition id>:<key>`, so changing a
key makes a new entity; an entity a C# device node's class adds is
`<definition id>:<device key>:<entity key>`, so two C# devices of one class are two
devices with entities of their own. A Device node's or a C# device's device is `<definition id>:<device key>`
in Home Assistant (a definition is not a device itself, and the device
`<definition id>` an earlier build made for it is removed at the next sync), so
changing a device key makes a new device, and an entity moved to another device
keeps its registry entry, entity id and history. Creation uses expectedRevision 0; updates and deletes need the
stored revision, and expectedRevision 0 deletes a corrupt file. `enabled` may be
given only when saving a new definition; otherwise the answer is "enabled can only
be given when saving a new definition; use set_enabled to turn a saved one on or
off". Warnings (severity 2) allow a save; errors (severity 3) refuse it.

### Secret settings

An answer never carries a secret setting's value: it is `value:null, secret:true,
hasValue` (whether one is stored). On save, for a setting already stored:

| Sent | Result |
|---|---|
| a `value` | replaces the stored value |
| `keep: true` | keeps it |
| `hasValue: true` with no value (the answer sent back) | keeps it |
| `value: null` without `hasValue` | clears it |
| a `value` and `keep: true` | refused: "Setting X has both a value and keep; give the value, or keep to leave the saved one as it is" |

`keep` and `hasValue` are request markers; neither is stored. A value kept from a
secret setting stays secret: making the setting open needs the value given again. A
preview reads no stored definition, so it checks no value for a kept setting.

## The document

A document answered by `list`, `read` and `save` adds how the definition runs now:

- `automationId`: its run history's key (see below).
- `active`: some revision of it is loaded; `currentRevisionActive`: the saved
  revision is the loaded one.
- `status`: `running`, `setup` (starting), `failed` (its setup or `Start()`
  failed, or it could not be prepared) or `off`.
- `activationError`: why it is not running, else null.
- `liveEntities`: `[{uniqueId,key,domain,name,entityId}]` of the running definition.
- `registeredEntities`: `[{uniqueId,entityId,domain,name,disabled}]`, its entries
  in Home Assistant's entity registry, which has them while it is off or failed too.
- `triggers`: its Trigger steps, `[{triggerId,method,manual,runnable}]`.
  `runnable` is true only while the saved revision runs, its status is
  `running` and the Trigger permits manual runs.

## Operations

**preview** validates and generates the definition without storing or running
anything. A graph's Home Assistant triggers are checked as an automation's are, and
an entity id Home Assistant does not have is a warning, `entity.unknown`.
`deriveSignatures: true` first writes each Custom C# step's pins and each C# node's
signature from its code, and the answer's `graph` is the graph with those and nothing
else of the definition.

A C# entity or C# device node's code is compiled as a class, its problems on the
node's `source`. A stored `signature` that differs from the class's in its `domain`,
the keys of its settings or their order, or a setting's `type`, `secret`, `options`,
`minimum`, `maximum`, `domain` or `required` is refused with `csharp.signature` on
the node's `signature`: "The settings are out of date with the code: open Edit code and apply it"; one that
differs in words only is refreshed. Then, for a node whose settings are current,
each pin's value, typed or that of the Setting wired to it, is checked against its
setting: a value the setting does not take is `setting.value` on the pin,
"`{Label} takes {what it takes}`" (such as "Interval takes a whole number from 1 to
3600"), without the value. With `deriveSignatures: true` each one's signature is
written from its class, and the value and the wire of a setting the code no longer
has are removed, each with a warning: "Removed the value of {Label}: the code no
longer has that setting", "Removed the connection to {Label}: the code no longer has
that setting". The source holds each node's code in `NodeLib.Custom_<escaped node
id>` and `SharpNodes.Create(nodeId)`, which creates a node's class by its id; a graph
without such a node has no `SharpNodes`. A Setting's value is never in the source:
it is read when the definition starts. Previewing never creates a class. The
panel's code editor checks one node's unsaved code with the node library API's
`custom_preview`, whose `kind` is `csharp_entity` or `csharp_device` for these nodes
and whose `signature` is then the class signature (see
[visual automations](visual-automations.md)).

**save** checks as preview does, stores the definition and runs it (unless it is
off), replacing the revision running. It waits up to 5 seconds for the
definition's `Start()`; one still starting is answered with status `setup`. The
answer's `diagnostics` are the preview's warnings.

A graph's C# entity and C# device nodes run beside its Entity nodes: each node's
class is created, its settings applied (a typed value, or the saved value of the
Setting wired to it), its entities' last states restored and its `Setup()` run, all
of them within one set-up time, before Home Assistant is told of any entity. Setting
up creates the graph's own entities, then runs the `[NodeSetup]` of each node library
class whose nodes the graph uses (see
[Setting up a node class](../node-libraries.md#setting-up-a-node-class)), then
creates every C# node with its `Setup()`, then runs the When started flow. There are
two clocks: the node classes' setups, creating every C# node and running its
`Setup()` share the definition's set-up time (`SetupTimeout`, 60 seconds, not
counting the time a download receives), and the When started flow has a set-up time
of its own after them. A node class's setup that throws fails the start with
"`{Definition}: {Class}: {reason}`". Once
the entities are registered, starting runs the `Start()` of the graph's own
entities, then each C# node's in node order, a C# device's before its entities';
stopping goes in the same order of groups, each group's entities before its device. A C# entity's key is its node's, and it is called what its
code names it, else its class's title when it stands alone (none on a device, which
Home Assistant then shows). A C# device is its own device, with its node's key, name
and Part of and what its class's device page shows. Its commands reach its class. A
new revision makes new instances, which restore their states; the old one stops after
the new one is set up and registered and before it starts. Every C# node's
constructor and `Setup()`, with the node classes' setups before them, together have
the set-up time. One that cannot be created
fails the start with "`{Definition}: {Name} could not be created: {reason}`" (a
constructor that does not finish in time: "`its constructor did not finish within
{n} s`"); a `[Setting]` setter or a `Setup()` that throws, or a `Setup()` that does
not finish, with "`{Definition}: {Name}: {reason}`"; nothing of the definition is
then registered. A required setting wired to a Setting whose saved value is null is
accepted by save, and fails the start after its class is created and before its
settings are applied or its `Setup()` runs, with
"`{Definition}: {Name}: the setting {Label} has no value`".

**set_enabled** turns a saved definition on or off without a new revision. Turned
on, it is rebuilt against the libraries as saved now; off, it is unloaded and the
entities it has created stay in Home Assistant as unavailable. A definition saved
off (`enabled: false`) creates its entities the first time it is turned on.

**delete** removes the file, the run history's identity, the entities' stored
states, and the entities and the devices in Home Assistant. Removing the entities has
30 seconds; a failure is logged ("Cannot remove the entities of deleted entity
definition …") and the delete still succeeds.

**run** starts one Trigger step of a definition by hand, as `run` does for an
automation. An unknown definition is `not_found`; otherwise it is refused with
`invalid_request`, in this order: one that is off ("The entity definition is off"), one whose
saved revision is not running ("The saved revision of this entity definition is
not running: …"), an unknown step ("Trigger not found"), a step that is not a
Trigger ("Only a Trigger step can be run by hand; … is not one"), and a Trigger
whose manual runs are off ("Trigger does not permit manual execution").

## Runs and traces

A definition's runs are read with the run history commands of
[run history](run-history.md), by the document's `automationId`:
`{type:"hass_sharp/run_history",automationId,offset?,limit?,status?}`, then
`{type:"hass_sharp/run_trace",runId}`. Besides an automation's causes, a run's
`cause` can be `Command` (an Entity node's command), `Setup` (When started) or
`Frame` (a camera making a frame from its `image` input, named `{Name}: When a frame
is needed`, its trace's `rootPort` `frame`); an Entity node's state run is `Entity`. An Entity
node's `config.history` (`{state, commands, frame}`, each true or false and no
other member; an Entity node with `trackHistory` is refused) says whether its runs are
recorded: its commands' unless `commands` is false, its state's and its frame's only
when `state` or `frame` is true. Another step's runs are recorded by its
`trackHistory`; when it is not set: on for When started, off for a Trigger. A run
started with `run` is always recorded. The code of a C# node records no runs.

A trace's graph is the composed graph: it carries the definition under
`definition`, with no setting values. It is not a body to save; `read` gives that.

## Node library answers

A library `save` or `delete` checks and rebuilds every entity definition as it does
every automation (each embeds the libraries). Its answer
adds `definitions`, one `{id,saved,document,active,activationError,enabled}` per
definition rebuilt, and `invalidDefinitions`, `{id,name,diagnostics,running,message}`
for each that already had errors and was left as it is. A save that would break a
definition that works is refused with `validation`, and its `detail` lists those
definitions under `definitions` as `{id,name,diagnostics}`.

## Persistence

Definitions are stored one per file in `.storage/hass_sharp/entities/<id>.json`,
their entities' last states in `.storage/hass_sharp/entity_states.json` and
downloaded files in `.storage/hass_sharp/files/<sha256>/<name>`. Writes go through
the same authoring gate and atomic replacement as automations.
