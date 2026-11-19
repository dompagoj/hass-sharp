# Entity definitions API: envelope 1

An entity definition creates Home Assistant entities, optionally grouped as a
device, whose behaviour comes from a graph (version 5) or from a class in a node
library. The guide is [custom entities](../custom-entities.md); the MCP tools that
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
| types | none | `{types,generation}`: the classes the saved libraries offer, sorted by id |
| list | view?: `"home"`, todayUtc? | Document array without `source` and `sourceMap`; `home` adds `recentRuns` (at most 10, newest last), `runsToday`, `failedLastDay` and `lastRunUtc` to each |
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
{name, device?, settings?, logic}
```

- `name`: 1 to 120 characters. It names the device, and a class's entity without a
  name of its own on a standalone definition.
- `logic`: `{kind:"graph", graph}` with a version 5 graph, or
  `{kind:"class", type}` with a type id from `types`, such as
  `library:example_plug:NodeLib.SmartPlug`. A class's entities come from its code.
- A graph's entities are its Entity nodes (kind `entity`, at least one), each with
  `config` `{key, domain, name?, icon?, deviceClass?, entityCategory?,
  enabledByDefault?, options?, history?, arguments?}`. `key` matches
  `^[a-z][a-z0-9_]{0,39}$` and is unique; `domain` is one of the ten in the node
  catalog's `entityDomains`, which also gives each domain's state values (the
  node's inputs), commands (its execution outputs `command:<command>`, with their
  values as data outputs `command:<command>:<value>`; a camera has `frame`) and
  `options`; `entityCategory` is `config` or `diagnostic`. An entity of a
  definition without a device needs a `name`. The answered document lists them
  under `entities`, for reading only; a class's entities are in
  `registeredEntities`.
- `device`: null for none, or, for a class, null for the class's own (a
  `SharpDevice` class always makes its device); otherwise `{manufacturer?, model?,
  swVersion?, hwVersion?, configurationUrl?, suggestedArea?, via?}`, where `via` is
  `{definition:"<id>"}` or `{deviceId:"<Home Assistant device id>"}`. `via` is not
  checked on save, and a cycle is not refused either; when the device is set up, a
  parent Home Assistant does not have is left out. For a class, each field given
  overrides the class's own.
- `settings`: for a graph `{key, label?, type, value, secret?}` with `type`
  `string`, `boolean` or `number`; for a class `{key, value}`, one per `[Setting]`
  property the class declares (a value null leaves the property's own).

The server never trusts a `definition` key a client puts inside `graph` (the graph
is stored without one), fills in missing node positions, and answers the document
with `version`, `id`, `revision`, `source`, `sourceMap`,
`catalogGeneration`, `enabled` and `savedUtc` added.

Ids match `^[a-z0-9][a-z0-9_-]{0,79}$`; `new` is refused ("Invalid definition ID:
new is reserved"). An entity's unique id is `<definition id>:<key>`, so changing a
key makes a new entity. Creation uses expectedRevision 0; updates and deletes need the
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
- `triggers`: its Trigger steps, `[{triggerId,method,manual,runnable}]`, none for a
  class. `runnable` is true only while the saved revision runs, its status is
  `running` and the Trigger permits manual runs.

## Operations

**types.** Each type is `{id, sourceId, typeExpression, kind, domain, title,
description, settings}`. `kind` is `device` (a `SharpDevice`) or `entity` (a typed
entity, whose `domain` it gives). `settings` are `{key, label, description, type,
secret, options, minimum?, maximum?}` with `type` `string`, `boolean`, `integer`,
`number` or `enum` (`options` lists an enum's members).

**preview** validates and generates the definition without storing or running
anything. A graph's Home Assistant triggers are checked as an automation's are, and
an entity id Home Assistant does not have is a warning, `entity.unknown`.
`deriveSignatures: true` first writes each Custom C# step's pins from its code, and
the answer's `graph` is the graph with those pins and nothing else of the definition.

**save** checks as preview does, stores the definition and runs it (unless it is
off), replacing the revision running. It waits up to 5 seconds for the
definition's `Start()`; one still starting is answered with status `setup`. The
answer's `diagnostics` are the preview's warnings.

**set_enabled** turns a saved definition on or off without a new revision. Turned
on, it is rebuilt against the libraries as saved now; off, it is unloaded and the
entities it has created stay in Home Assistant as unavailable. A definition saved
off (`enabled: false`) creates its entities the first time it is turned on.

**delete** removes the file, the run history's identity, the entities' stored
states, and the entities and the device in Home Assistant. Removing the entities has
30 seconds; a failure is logged ("Cannot remove the entities of deleted entity
definition …") and the delete still succeeds.

**run** starts one Trigger step of a graph definition by hand, as `run` does for an
automation. An unknown definition is `not_found`; otherwise it is refused with
`invalid_request`, in this order: a class definition ("A class definition
has no steps to run"), one that is off ("The entity definition is off"), one whose
saved revision is not running ("The saved revision of this entity definition is
not running: …"), an unknown step ("Trigger not found"), a step that is not a
Trigger ("Only a Trigger step can be run by hand; … is not one"), and a Trigger
whose manual runs are off ("Trigger does not permit manual execution").

## Runs and traces

A graph definition's runs are read with the run history commands of
[run history](run-history.md), by the document's `automationId`:
`{type:"hass_sharp/run_history",automationId,offset?,limit?,status?}`, then
`{type:"hass_sharp/run_trace",runId}`. Besides an automation's causes, a run's
`cause` can be `Command` (an Entity node's command), `Setup` (When started) or
`Frame` (When a frame is needed); an Entity node's state run is `Entity`. An Entity
node's `config.history` (`{state, commands, frame}`) says whether its runs are
recorded: its commands' unless `commands` is false, its state's and its frame's only
when `state` or `frame` is true. Another step's runs are recorded by its
`trackHistory`; when it is not set: on for When started, off for a Trigger. A run
started with `run` is always recorded. A class definition records no runs.

A trace's graph is the composed graph: it carries the definition under
`definition`, with no setting values. It is not a body to save; `read` gives that.

## Node library answers

A library `save` or `delete` checks and rebuilds every entity definition as it does
every automation (each class definition and graph embeds the libraries). Its answer
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
