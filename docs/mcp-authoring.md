# Authoring through Home Assistant MCP

HassSharp requires **Home Assistant 2026.11 or newer** and registers an LLM API
with Home Assistant. Home Assistant's built-in MCP Server serves it to MCP clients,
and any conversation agent an administrator configures with it (OpenAI, Anthropic,
Google, Ollama and others) gets the same tools. There is no separate HassSharp MCP
server or port. The local Compose image is pinned to
`homeassistant/home-assistant:2026.11.1`. Read [Security](#security) before
giving the API to an agent. The tools build two kinds of document: automations
(`list`, `read`, `preview`, `save` and the rest) and
[entity definitions](custom-entities.md), which create Home Assistant entities
(the `entities_*` tools; see [Entity definitions](#entity-definitions)). An
automation and a definition may have the same id: each tool reads and changes only
its own kind.

Add **MCP Server** under Settings → Devices & services if it is not already
configured. Open its options and select **HassSharp** from the available LLM APIs
(or include all APIs). Keep administrator authentication enabled. The API
requires an administrator, and Home Assistant applies that to everything it is
combined with: an agent or MCP entry that uses HassSharp together with Assist (or
all APIs) answers only administrators. A non-administrator, or a voice satellite's
pipeline, which has no user, gets "Error preparing LLM API" for the whole
conversation. Give HassSharp only to an agent that administrators use. A client using
an administrator's Home Assistant credentials can connect to `/api/mcp`; for the
local development instance that is `http://localhost:5001/api/mcp`.

The direct endpoint `/api/mcp/hass_sharp` selects only HassSharp and always
requires an administrator. Home Assistant's direct endpoint does **not** apply
the configured MCP entry's read-only setting. Use configured `/api/mcp` with
**Read only** enabled to expose only queries and previews. Refresh/reconnect the
client's tool discovery after changing selected APIs, options, or reloading
HassSharp. The API registers only after the integration is ready; unloading it
unregisters the API and invalidates tools a client has cached. While it reloads,
the API is briefly unregistered: an agent that asks for it gets Home Assistant's
"Error preparing LLM API" until setup finishes, and a cached tool answers
`not_ready`. A tool call already running when the reload or turn-off starts gets
up to 30 seconds to finish before HassSharp unloads its automations.

Use the MCP client's supported Home Assistant authentication, or an administrator
long-lived access token in the `Authorization: Bearer ...` header. Keep tokens out
of source control and prompts. HTTP POST accepts JSON-RPC MCP requests with
`Content-Type: application/json` and `Accept: application/json`; Home Assistant
handles transport/session behavior. The API also checks the current user on every
tool invocation, including direct calls and cached tools.

## Security

- **Who can use it.** Every MCP client and every conversation agent an
  administrator selects the API for, not only the MCP Server. Each instance and
  each tool call checks that the conversation's user is an administrator.
- **What a change can do.** `save`, `entities_save` and `library_save` compile C#
  and load it into Home Assistant's own process, with its privileges: the
  configuration directory with `secrets.yaml` and the `.storage` tokens, the network
  and every service. Static constructors run when the code is activated. Treat these
  tools as code execution. `entities_save` also creates entities and devices in
  Home Assistant. Agents do not ask before a tool call; the API prompt tells the
  model to change nothing unless the user asks for it in the conversation, and that
  is advice to the model, not a control.
- **Read only.** Give an agent that must not change anything an MCP Server entry
  with **Read only** enabled. That removes `save`, `set_enabled`, `delete`, `run`,
  `entities_save`, `entities_set_enabled`, `entities_delete`, `entities_run`,
  `library_save` and `library_delete`. A conversation agent given the API gets
  every tool, the changing ones included.
- **What leaves the house.** Tool results go to the model's provider: entity ids,
  names and states (discovery lists entities whether or not they are exposed to
  assistants), service descriptions, graphs and their C#, entity definitions, run
  errors and trace values. Discovery returns only a few attributes of each entity
  (see step 3), so a camera's `access_token` and an `entity_picture` URL with a
  token stay in Home Assistant.
- **Secret settings.** A definition's secret setting's value is never answered as
  the setting's value: every answer has it as `value: null, secret: true, hasValue`,
  and the API refuses to pass on an answer that carries one ("The answer held the
  value of a secret setting, so it was not passed on…"). A secret typed into a
  conversation reaches the assistant's provider all the same: enter secrets in the
  HassSharp panel, and let the assistant send the setting back as it was answered.
  The host and port of an address that comes from a secret setting are not secret:
  failures, traces and the download log line name them (see [custom
  entities](custom-entities.md#secrets)). No tool returns a camera's picture. A
  trace can still show a true or false worked out from a secret, or what a step it
  was given returns; see [custom
  entities](custom-entities.md#run-history-and-traces).
- **Injected text.** Entity names and attributes, service descriptions, errors and
  trace values come from devices and integrations. The prompt tells the model they
  are data and never instructions, but a model can still be misled by them.
- **Trace redaction is by name.** Values under secret-looking names (`password`,
  `token`, `*_key` and the like) and `token=` in strings are recorded as `***`. A
  secret under another name is recorded as it is, so redaction is not a security
  boundary; see [run history](backend/run-history.md).

## Workflow and tool contract

Every name begins `hass_sharp__`. Results are JSON objects:
`{version:1,ok:true,result:...}` or
`{version:1,ok:false,error:{code,detail}}`. Errors also set the MCP tool error flag.
Validation details include mapped node/input diagnostics; conflicts require a
fresh read rather than retrying with the old revision. The API prompt and the
tool descriptions summarise the workflow below and the entity typing rule.

1. Call `get_capabilities` once. It returns graph version 5, the actual merged
   built-in/custom catalog, the authoring rules as `semantics` (execution and data
   edges, types and refinement, trigger outputs, the Call service node, run
   history, C#) and complete example graphs that validate and compile. Pass
   `document: "entity"` for entity definitions instead; see
   [Entity definitions](#entity-definitions).
2. Call `get_node_schema({kind})` for each node kind you add. It returns the node,
   the type table, `parametricTypes` and limits. For `trigger` it adds the
   `triggerOutputs` table. For `service`, pass `service` (`domain.service`) to get
   the ports the validator gives that service (the library `service_ports`
   operation): every `field:<name>` input with types and constraints, the target
   types (for example `["target","entity_id:light"]`), `hasTarget`, the response
   mode and the outputs. A graph's node has only the field ports whose keys are in
   its `config.fields`. In port types, `$any` stands for every type, `$properties`
   for every type whose entry lists `properties` (such an input takes a wire only:
   a value typed in, null included, is refused), and `$type` (generally `$<name>`)
   for the type set in the node's `config.<name>`. An output with
   `propertyOf: <input>` (Select property's) has an empty `type`: it takes the type
   of the property `config.property` names, of the value wired into that input. A
   type entry's `properties` (`{name, type, nullable, description?}`) are in the
   type table that `get_node_schema` and `get_capabilities` return, and in a Custom
   C# node's `config.signature.types`. Without `kind` it returns the whole catalog;
   `document: "entity"` gives a definition's steps instead.
3. Use `discover` to find entities, services, devices, areas or installed trigger
   descriptions. Supply `kind`, optional `query`, `offset` (default 0) and `limit`
   (default 50, maximum 100); entities and services also take `domain`.
   Filtering happens before stable sorting/pagination. Each entity is
   `{id, name, domain, state, attributes}`, where `attributes` holds only what an
   author needs: device class, unit, supported features, option lists (such as
   `hvac_modes`, `preset_modes`, `options`, `effect_list` or `source_list`) and
   ranges (`min`, `max`, `step`, `min_temp`, `max_temp` and the like). Discovery
   lists every entity, whether or not it is exposed to assistants: the tools are
   for an administrator building automations. Services come from the same
   normalised schemas the validator uses: without `service`, one summary line per
   service (`id`, `name`, `description`, `hasTarget`, `response`); with `service`,
   that service's complete schema, fields in Home Assistant's order. A service that
   is not registered (yet) gives `known: false` here and in `get_node_schema`.
   A page also ends before about 32 KiB of items, and `nextOffset` gives the offset
   of the next page, `null` after the last. A `token=` value in an entity's state is
   replaced with `[removed]`. Discovery never attaches triggers or calls services.
4. `list` summarises every saved automation without its graph:
   `{id, name, revision, enabled, active, currentRevisionActive, activationError,
   triggers:[{triggerId, label?, type, config, manual, runnable}]}`. The triggers
   say what starts it, and `activationError` why it is not running. `read({id})`
   returns one complete document: its graph, `source` and `sourceMap`. Read before
   every edit.
5. `preview({graph})` validates and compiles a complete graph against the
   registered service schemas, including HA-native trigger declarations, returning
   `valid`, `source`, `sourceMap`, `diagnostics` and `graph`. It does not attach
   triggers or execute custom code. A graph has `version`, `name`, `nodes` and
   `edges` only; `imports` and `members` are refused. A Custom C# node (kind
   `custom`) is one static method marked `[Node]` in `config.source`, with an
   operand per input in `config.arguments`. Its pins (`config.signature`) are worked
   out from the code: MCP preview and save pass `deriveSignatures: true`, and the
   returned `graph` carries the pins. See [writing C# nodes](node-libraries.md).
6. `save({id,expectedRevision,graph})` persists and activates through the editor's
   same authoring service, working out Custom C# pins as preview does. New IDs use
   revision 0; existing edits use the exact
   read revision. Warnings are allowed; severity 3 errors prevent persistence.
   Preview and save warn (`entity.unknown`) about each entity ID written into the
   graph that Home Assistant has no entity for; fix the ID unless the entity is
   still to be added.
   Saving can enable startup, scheduled or native effects, so authorize those
   effects before saving. The result separates `saved:true` from `active` and
   `activationError`.
7. With authorization, `set_enabled({id,enabled})` turns a saved automation on or
   off; it takes no revision and changes neither the graph nor the revision. While
   it is off, none of its triggers and no startup run starts it, and its runs in
   progress are cancelled. `run` still starts it.
8. With authorization, `run({id,triggerId})` dispatches a manually eligible trigger
   on the exact current saved active revision and returns `{version:1,runId}`.
9. `history({id,status?,offset?,limit?})` returns run history version 2 for that
   document, `{id,version:2,offset,limit,runs,nextOffset}`, newest first, optionally
   only runs with the given statuses. A page also ends before about 32 KiB of runs;
   `nextOffset` gives the offset of the next page and is `null` after the last run
   (after a full page the next one can be empty). A `token=` value in a run's error is
   replaced with `[removed]`, as it is in a trace event's error. See
   [run history](backend/run-history.md) for the run fields.
10. `trace({runId,node?,graph?,offset?,limit?})` reads one run's trace. By default it returns the
   run, `live`, the snapshot identity (`revision`, `visualId`, `visualRevision`),
   and the trace's `rootNodeId`, `rootPort` (only for a run of an Entity node's flow), `startedUtc`, `complete`, `truncated`, `dropped`,
   `summary` and a page of the events in order without their values (node, frame,
   status, port, `t`, `ms`, error), with `totalEvents`. `node` returns that node's
   events with their `inputs` and `outputs`, or `not_found` when it never ran;
   `graph: true` adds the graph revision that ran. `offset` and `limit` page the
   events; `limit` defaults to 100 without `node` and 10 with it, at most 500. A
   page also ends before about 32 KiB of events (one larger event still comes on a
   page of its own), and `nextOffset` gives the offset of the next page, `null`
   after the last. The catalog stored with the snapshot is never returned. `trace`
   is `null` for a run without one.
11. `delete({id,expectedRevision})` deletes a document and stops its runtime.

Graph nodes are `{id,kind,config,label?,position?}`; a stored `size` from an older
editor is removed from documents MCP returns, and the graph schema rejects it.
Write entity IDs for domain-filtered inputs with their exact type: the target of
`light.turn_on` takes `{"kind":"literal","type":"entity_id:light","value":"light.hall"}`,
and a wired plain `entity_id` needs a Convert node with type `entity_id:light`
first. Convert and the other value nodes (catalog `pure: true`, such as `constant`
and `compare`) take no execution edges: connect only their data ports, and the
step that reads them works them out each time it runs. A `constant` gives
`config.value`, a literal of the type in `config.type`, such as
`{"type":"number","value":{"kind":"literal","type":"number","value":21}}`; its
input is `connectable: false` in the catalog, so no data edge may end at it. See [visual authoring](writing-visual-automations.md#types-and-refinement).

Tool arguments are strict: unknown fields and wrong types are rejected. Revisions
and pagination values must be integers (booleans are not accepted). Stable IDs
match `[a-z0-9][a-z0-9_-]{0,79}`. All tools require administrator privileges.
Read-only MCP mode removes save, set_enabled, delete, run, entities_save,
entities_set_enabled, entities_delete, entities_run, library_save and
library_delete; `trace`, `history` and `entities_history` stay available. Of the 25
tools, 15 remain.

A failed replacement can leave an older generation running. Document `active`
means some generation is active; **`currentRevisionActive`** means the saved
revision and source match the active runtime. `activeRevision`, `activeSourceHash`
and `activationError` explain the difference. Run resolves the stable trigger and
checks/dispatches under the same managed authoring gate as saves and library
mutations. A stale active generation cannot run as though it were the newer saved
graph. Trigger method identities are `Run_` plus uppercase UTF-8 hexadecimal of
the trigger node ID; clients should pass `triggerId`, not construct method names.

## Entity definitions

The `entities_*` tools do for [entity definitions](custom-entities.md) what the
document tools do for automations, through the panel's own API
([entity definitions API](backend/entity-definitions.md)). Results use the same
envelope; validation, conflicts and revisions work as above.

1. Call `get_capabilities({document:"entity"})`. The catalog then holds the steps a
   definition may use and its `entityDomains` (each entity type's state values,
   commands and options), `semantics` gains `definitions` (the rules) and `examples`
   is `{definitions:[…]}`: complete definitions that validate, the six in
   [`authoring_examples.json`](../custom_components/hass_sharp/authoring_examples.json),
   two of them written in C# as a C# device node and a C# entity node. Each is a
   graph. `get_node_schema({kind})` adds `entityDomains` for `entity` (the Entity
   node) and `set_entity_state`, whose ports come from the entity's type; for
   `device` (the Device node) it gives its config and its `entities` output, and for
   `csharp_entity` and `csharp_device` their config (see
   [C# entity and C# device nodes](#c-entity-and-c-device-nodes)).
2. `entities_list({})` summarises every definition without its graph: `{id, name,
   revision, enabled, status, activationError, devices, entities:[{key, domain,
   name, device, entityId}], triggers:[{triggerId, label?, type, config, manual,
   runnable}]}`. `status` is `running`, `setup` (starting), `failed` or `off`. A
   definition's `entities` are its Entity nodes and C# entity nodes, each with
   `device`, the key of the device it is on or null (a C# entity is named as Home
   Assistant has it, else by its class's title), then each entity a C# device's code
   added, as Home Assistant has registered it, with `device` the C# device's key:
   those are known only once the definition has run. `devices` is how many Home
   Assistant devices it makes: its Device nodes and C# device nodes, which
   `entities_read` lists under `devices`. Beside the
   result, `unreadable: [{id, reason, deletable}]` names each definition file that
   cannot be read; one that is `deletable` only `entities_delete` with
   `expectedRevision` 0 removes (see step 5), and one that is not (its file name is
   not a definition id, or it could not be opened) is removed by hand from Home
   Assistant's `.storage/hass_sharp/entities` folder.
   `entities_read({id})` returns one complete document, with its `revision`,
   `source`, `automationId` and `registeredEntities`, and `devices`, the devices
   its Device nodes and C# device nodes declare (a C# device's with
   `csharp: true`), and `entities`, the entities its Entity nodes and C# entity
   nodes declare, each with `device`, the key of the device it is on or null (a C#
   entity's `{key, domain, name: null, device, csharp: true}`); both are for reading
   only. The entities a C# device's code adds are in neither: they are in
   `liveEntities` and `registeredEntities`, under the unique id `<definition
   id>:<device key>:<entity key>`. Read before every edit, and send back only `name`,
   `settings` and `logic`.
3. `entities_preview({definition})` validates and compiles a definition without
   creating anything: a definition is `{name, settings?, logic}` with `logic`
   `{kind:"graph", graph}`, its devices Device nodes in the graph (one sent with a
   `device` that is not null is refused with `definition.deviceNotAllowed`, which says
   to use Device nodes). A `logic` of another kind is refused before it reaches the
   server: `A definition is built from nodes: logic is {kind: "graph", graph}. Write
   C# in a C# entity or C# device node`. It returns `valid`,
   `diagnostics` and `graph` with each Custom C# step's pins and each
   C# node's signature worked out (no settings come back). Unlike the panel's API, it leaves out the generated
   `source` and `sourceMap`, and so does a refusal for errors (`validation`) from it
   or from `entities_save`, whose detail is the preview; `entities_read` has a saved
   definition's `source`. A definition that carries `entities`, even an empty
   list, is refused with the server's own sentence, `definition.entitiesNotAllowed`:
   "Entities are Entity nodes in the graph now: leave entities out and add one Entity
   node for each". One with no Entity node, C# entity node, C# device node or
   library class node, nor a node of a `library:` kind the catalog lacks, gets
   `definition.entitiesRequired`, "Add an Entity node: an entity definition creates
   the entities on its canvas, and has none".
4. `entities_save({id, expectedRevision, definition, enabled?})` saves it and
   creates or updates its entities, returning `saved`, `document` (as
   `entities_read` gives it, without `source` and `sourceMap`), `active`,
   `activationError` and, when the save has warnings, `diagnostics`. `enabled` is
   only for a new definition (`false` saves it off, and it creates its entities the
   first time it is turned on); `new` is not an id. An entity's key is permanent:
   changing it makes a new entity. A secret setting is sent back as answered to
   keep its value, with a new `value` to replace it, or with `value: null` and no
   `hasValue` to clear it.
5. `entities_set_enabled({id, enabled})` turns a definition on or off (off, the
   entities it has created stay as unavailable; a definition saved off creates its
   entities the first time it is turned on) and returns `enabled`, `active` and
   `activationError`. `entities_delete({id, expectedRevision})` deletes it and
   removes its entities and devices. A graph definition stored by an earlier
   development build (document `version` 1 or 2) is left out of `entities_list`'s
   result and named in its `unreadable` with the reason "saved by an earlier
   development build; delete it and build it again", which `entities_read` of it
   answers too; another file that cannot be read is named there with what was
   wrong. Until it is deleted, its entities stay as unavailable and `library_save`
   is refused with `Cannot read entity definition <path>: <reason>`. Delete it with
   `expectedRevision` 0 (the user can also do so on the **Entities** page) and save
   it again; one listed with `deletable` false cannot be deleted so, and the user
   removes its file from Home Assistant's `.storage/hass_sharp/entities` folder by hand.
6. `entities_run({id, triggerId})` runs one Trigger step of a running definition,
   one whose `runnable` is true, and returns `runId`. Nothing else runs by hand: a
   command runs when the entity's service is called, an Entity node's state follows
   the entities it reads, and When started runs when the definition starts.
7. `entities_history({id, status?, offset?, limit?})` returns the definition's runs
   as `history` does an automation's; `trace` reads any run of either. An Entity
   node's `config.history` (`{state, commands, frame}`, each true or false and no
   other member; an Entity node takes no `trackHistory`) says which of its runs are
   recorded: its commands' unless `commands` is false, its state's and its frame's
   only when `state` or `frame` is true. When started records its runs unless its
   `trackHistory` is false, a Trigger only when it is true (the opposite of an
   automation's default); a run started with `entities_run` is always recorded. An
   Entity node's runs are named `Relay: State`, `Relay: Turn on` or `Front door:
   When a frame is needed` (its entity's name, else its key, else `Entity`, and the
   flow), and their trace has the node as `rootNodeId` and the pin as `rootPort`
   (`state` for its state, `frame` for a camera's frame).
   A graph that `trace` returns with `graph: true` carries the definition without
   its settings' values: it is not a definition to save.

### The Entity node

An Entity node (kind `entity`) is one entity of a definition. Its `config` is
`{key, domain, name, icon?, deviceClass?, entityCategory?, enabledByDefault?,
options?, history?, arguments?}`: `key` matches `^[a-z][a-z0-9_]{0,39}$` and is
unique, `domain` is one of the ten in the catalog's `entityDomains` and `options`
are that domain's. Leave out a field the entity does not need: `name`, `icon`,
`deviceClass` and `entityCategory` take no `null`. An entity on no device needs a
`name` (`entity.name`); one on a device may leave it out. Its `device` input puts
it on a device (see [The Device node](#the-device-node)). Other steps, such as Set
state and Set available, name it by its key in `config.entity`.

- **State.** The node has one input per value of `entityDomains[domain].state`,
  by that value's name (`is_on`, `value`, `brightness`). Give a value under
  `config.arguments.<name>`, as an operand such as
  `{"kind":"literal","type":"boolean","value":true}`, or connect a data edge with
  `targetPort` set to the name, which leaves `{"kind":"unset"}` under `arguments`.
  Once one value is given, every value the type requires is needed too
  (`entity.partialState`). An input cannot read, even through value steps, an
  output of a step that runs in a flow (`scope.stateSource`): set the state with a
  Set state step after that step instead.
- **Commands.** The node has one execution output `command:<command>` per key of
  `entityDomains[domain].commands`, so an execution edge from it with
  `port: "command:turn_on"` starts the steps that run when Home Assistant sends
  that command. The command's values are the data outputs
  `command:<command>:<value>` (`command:turn_on:brightness`), readable only by
  steps that run from that command alone (`scope.flow`). A command with no edge is
  a warning (`command.unhandled`). The steps must finish within 30 seconds, or the
  service call that sent the command fails.
- **Frames.** A camera's node has no state values, no commands and no execution
  output: its `image` input (from `entityDomains.camera.picture`) takes the picture
  it shows, made from values alone each time Home Assistant asks for a frame. Wire
  it from Camera frame (kind `camera_frame`, a value whose `entity` input takes a
  `camera.*` id or a wire from an `ha_entity` node), through Draw
  rectangle and Draw text if the picture is drawn on. An `image` with no wire is a
  warning (`camera.no_frame`), and one that reads a step, even through value steps,
  is an error (`scope.frameSource`).
- **Live or snapshot.** Camera frame is live while it makes a camera's frame: when
  its `image` output reaches a camera's `image` input through value steps alone, it
  gives raw frames from one shared ffmpeg feed of the source camera, at its full
  frame rate; a wrapper camera such as *Camera with a box* is live. Read anywhere
  else, such as by a step after a Trigger that sends a doorbell notification, it
  takes one snapshot from Home Assistant and passes the JPEG on unchanged. The
  editor shows which as a badge on the node; there is no setting for it.
- **Size.** Camera frame's optional `config.maxWidth` and `config.maxHeight`, whole
  numbers from 16 to 8192, make the frame no larger than that, its aspect ratio
  kept and never larger than the camera's own: with both it fits in both, with one
  the other side follows the aspect ratio, and with neither it is the camera's own
  size. A value outside 16 to 8192 or not whole is an error naming the field (Width
  at most, Height at most). A live frame is read at that size; a snapshot is
  scaled to fit.
- **Video.** Video frame (kind `video_frame`, a value) gives a frame of a video:
  its `config.source` is an `http://`, `https://`, `rtsp://`, `rtsps://` or
  `rtmp://` address with a host and no spaces, or a file's absolute path such as
  `/media/clip.mp4`; anything else, such as `concat:`, `pipe:`, `file:` or a
  relative path, and a blank source are errors (`video.source`). `config.loop`
  (true unless set false) starts a file again when it ends, and `maxWidth` and
  `maxHeight` work as Camera frame's. A file must be in a folder Home Assistant
  allows (`allowlist_external_dirs` or `media_dirs`), which is checked only when a
  frame is made: one elsewhere fails the frame, saying so. It is live in a
  camera's frame as Camera frame is; elsewhere it gives the newest frame of an open
  feed of the video, else one frame read by a short run of ffmpeg.

A node of kind `ha_entity` (labelled Home Assistant entity, in the Hass category, in any graph,
automations too) gives one of Home Assistant's entities: its `config` is `{entity}`, an entity id such as
`camera.front_door` (`entity.id` when it is blank or not an entity id), and its one
output `entity` is an entity id of that id's domain (`entity_id:camera`), so it
goes only into an input that takes that domain. An Entity node's state that reads
it follows its entity, as one chosen on the step.

The relay of the example *Plug over HTTP* and the edges of its two commands:

<!-- example: Plug over HTTP -->
```json
{
  "nodes": [
    {"id": "relay", "kind": "entity", "config": {"key": "relay", "domain": "switch", "name": "Relay", "deviceClass": "outlet", "arguments": {"device": {"kind": "unset"}}}, "position": {"x": 60, "y": 640}}
  ],
  "edges": [
    {"id": "relay:command:turn_on:switch_on", "source": "relay", "target": "switch_on", "port": "command:turn_on"},
    {"id": "relay:command:turn_off:switch_off", "source": "relay", "target": "switch_off", "port": "command:turn_off"}
  ]
}
```

A run that reaches a Stop step is Stopped, not failed: a command it handled
succeeds and a When started flow that stops lets the definition start.

### The Device node

A Device node (kind `device`) is one device of a definition; a definition may have
several, or none, and is not a device itself; so is a C# device node (see
[C# entity and C# device nodes](#c-entity-and-c-device-nodes)). Its `config` is
<!-- config: device -->
`{key, name, manufacturer?, model?, swVersion?, hwVersion?, configurationUrl?,
suggestedArea?, via?}`. It has no inputs and no execution ports, and is never part of the run order:
an execution edge into or out of it is an error. Its one output, `entities`, connects
only to the `device` input of an Entity node or a C# entity node, with the data edge
`{kind: "data", source: <the Device node>, sourcePort: "entities", target: <the
Entity node>, targetPort: "device"}`; that wire puts the entity on the device and carries no value. The rules
a definition can trip:

- **A key and a name.** `key` follows the entity key rule (`^[a-z][a-z0-9_]{0,39}$`)
  and is unique among the definition's devices, its C# devices included (`definition.deviceKey`,
  `definition.duplicateDevice`); device keys and entity keys are apart. A changed key
  makes a new device in Home Assistant. `name` is what Home Assistant shows: a Device
  node without one is an error (`device.name`).
- **An entity with no device needs a name.** An Entity node whose `device` input is
  not wired stands alone and needs its own `name` (`entity.name`).
- **Part of.** `via` is left out (or null), `{"deviceId": "<Home Assistant device
  id>"}` (an id from `discover` with `kind: "devices"`) or `{"device": "<the key of
  another Device node or C# device node>"}` (`device.via`); a key no device of the
  definition has, and a circle of
  devices each part of the next, are errors (`device.viaUnknown`,
  `device.viaCycle`).
- `configurationUrl` starts with `http://`, `https://` or `homeassistant://`,
  followed by an address; the rule is narrower than Home Assistant's own, so a few
  addresses it would take, such as one with a user name before the host, are refused
  (`device.configurationUrl`). A Device node no entity is wired to is a warning
  (`device.empty`).

The device of the example *Plug over HTTP*, and the wires that put its relay and
its power sensor on it:

<!-- example: Plug over HTTP, its device -->
```json
{
  "nodes": [
    {"id": "plug", "kind": "device", "config": {"key": "plug", "name": "Plug over HTTP", "model": "Smart plug", "suggestedArea": "Living room"}, "position": {"x": 60, "y": 820}}
  ],
  "edges": [
    {"id": "plug.entities:relay.device", "kind": "data", "source": "plug", "target": "relay", "sourcePort": "entities", "targetPort": "device"},
    {"id": "plug.entities:power.device", "kind": "data", "source": "plug", "target": "power", "sourcePort": "entities", "targetPort": "device"}
  ]
}
```

A saved definition's `entities_read` answer lists `devices` (the config of each Device
node with a key, without its blank texts, then each C# device node's `{key, name,
via?, csharp: true}`) and gives each of its `entities` its `device`. Nobody sends them: `devices` sent back with a definition are ignored.
Home Assistant knows a device by the definition's id and the device's key, and an
entity keeps its entity id when it moves to another device or off one.

### C# entity and C# device nodes

Write C# for a definition as nodes of its graph: an
entity is a C# entity node (kind `csharp_entity`) and a device a C# device node (kind
`csharp_device`), each with one class in `config.source`. A C# entity node's
`config` is
<!-- config: csharp_entity -->
`{key, source, signature?, arguments?}`, and a C# device node's
<!-- config: csharp_device -->
`{key, name, via?, source, signature?, arguments?}`.

A node library may also offer a device or entity class as a node of its own, a
library class node, used without code (see
[A class as a node](node-libraries.md#a-class-as-a-node)). Its kind starts with
`library:`: copy it from the catalog of `get_capabilities({document:"entity"})`,
never build it. Its `config` is the C# node's of its class without `source` and
`signature`, and its inputs are the settings its catalog entry lists, which take
values and wires as below.

- **The class.** A C# entity's class derives from an entity class (`SharpBinarySensor`,
  `SharpSensor`, `SharpSwitch`, `SharpButton`, `SharpNumber`, `SharpSelect`,
  `SharpText`, `SharpLight`, `SharpCover` or `SharpCamera`), whose domain is the
  entity's. A C# device's derives from `SharpDevice` and adds its entities with
  `Add(new SharpSwitch("relay", "Relay") { OnTurnOn = …, OnTurnOff = … })` in its
  constructor or `Setup()`. It is public, not abstract or generic, with a public
  constructor that takes nothing; helper types may sit beside it. Each node's class
  is its own: a type one node's code declares is not for another node's code
  (declare it there too, or in a node library). The members a
  class has are in [custom entities](custom-entities.md#c-nodes).
- **The signature.** `config.signature`, `{title, description, domain, settings:
  [{key, label, description, type, secret, options?, minimum?, maximum?, domain?,
  required?}]}`, is worked
  out from the code: `entities_preview` and `entities_save` do it (they pass
  `deriveSignatures: true`) and preview returns the `graph` with it, so leave it out.
  A signature out of date with the code is an error (`csharp.signature`); a value or
  wire of a setting the code no longer has is removed, with a warning naming it.
- **Settings are pins.** Each public `[Setting("Label")]` property is an input pin
  whose id is the property's name. Give it a value under
  `config.arguments.<property name>` as `{"kind": "literal", "type": …, "value": …}`,
  `type` being `string` for a string or an enum (one of its `options`), `number` for
  an int, long or double (whole for an int or a long, within `minimum` and
  `maximum`) and `boolean` for a bool; a value the setting does not take is an error
  (`setting.value`). Or wire a Setting step to it, `{kind: "data", source: <the
  Setting step>, sourcePort: "value", target: <the node>, targetPort: <property
  name>}`, leaving it out of `arguments`; a wire from any other step is an error
  (`scope.settingSource`). A pin given neither keeps the class's own value, unless its
  setting is required (below). A secret setting takes only a wire from a Setting
  step: a value is refused
  (`setting.secretLiteral`), so add a secret setting to the definition and let the
  user enter it in the panel.
- **Entity settings.** A property of type `Entity`, or of a domain's class such as
  `LightEntity`, is a setting of `type: "entity"`, with `domain` for a domain's
  class. Its value is an entity id, typed
  `{"kind": "literal", "type": "entity_id:light", "value": "light.kitchen"}` (`entity_id`
  for `Entity`); an id of another domain is refused on the pin. An entity setting
  takes no Setting step, and cannot be secret.
- **Required settings.** A string or entity setting with no initializer and no `?` is
  required: the signature has `required: true`, and its pin without a value or a wire
  is an error (`{Label} is required`). Give one that may stay empty a `?` or an
  initializer; the C# `required` modifier is refused.
- **Device.** A C# entity node has the `device` input of an Entity node, which a
  Device node's `entities` output is wired to; without it the entity stands alone. A
  C# device node has no `device` input and no entity is wired to it, its entities
  being the ones its code adds: its only inputs are its setting pins. Its `via` is a
  Device node's. Neither has outputs or execution ports,
  nor is part of the run order: no step sets a C# entity's state or names it in
  `config.entity`, and no command pin starts a step; the class does both.
- **Keys and ids.** A C# entity's key is among the entity keys and its unique id is
  `<definition id>:<key>`; a C# device's key is among the device keys and its
  device is `<definition id>:<device key>`. An entity its code adds keeps the key the
  code gives it, with the unique id `<definition id>:<device key>:<entity key>`. A
  C# entity is called the `Name` its code sets, else, standing alone, its class's
  title; a C# device is called its `config.name`.
- **Starting.** Every C# node's constructor and `Setup()` together have the
  definition's set-up time, 60 seconds; one that throws or takes longer keeps the
  definition from starting, with an `activationError` that names the node
  (`<definition>: <node name>: <reason>`, or `<definition>: <node name> could not be
  created: <reason>`). A required setting wired to a Setting step whose setting has no
  value is saved, but the definition does not start:
  `<definition>: <node name>: the setting <Label> has no value`. When started has its
  own time. Each node's `Start()` runs after the definition's own start, in node order (a device before its entities),
  and `Stop()` when it stops or is replaced. Each save makes new instances, whose
  entities take back their last states.

The example *Plug in C#* is one C# device whose Address takes the
definition's `address` setting (its code, given here as `…`, is in the example):

<!-- example: Plug in C#, its nodes -->
```json
{
  "nodes": [
    {"id": "address", "kind": "setting", "config": {"key": "address"}, "position": {"x": 60, "y": 100}},
    {"id": "plug", "kind": "csharp_device", "config": {"key": "plug", "name": "Smart plug", "source": "…", "signature": {"title": "Smart plug", "description": "A smart plug that answers JSON-RPC requests: a switch for its relay and a sensor for the power it draws.", "domain": null, "settings": [{"key": "Address", "label": "Address", "description": "Where the plug answers, such as http://192.168.1.50/rpc.", "type": "string", "secret": false}]}, "arguments": {}}, "position": {"x": 360, "y": 100}}
  ],
  "edges": [
    {"id": "address.value:plug.Address", "kind": "data", "source": "address", "target": "plug", "sourcePort": "value", "targetPort": "Address"}
  ]
}
```

The example *Thermometer over HTTP* is one C# entity, a sensor on no device, with
its Address wired from a setting and its Interval given a value:

<!-- example: Thermometer over HTTP, its nodes -->
```json
{
  "nodes": [
    {"id": "address", "kind": "setting", "config": {"key": "address"}, "position": {"x": 60, "y": 100}},
    {"id": "thermometer", "kind": "csharp_entity", "config": {"key": "thermometer", "source": "…", "signature": {"title": "Thermometer", "description": "A thermometer that answers with the temperature in degrees Celsius at its address.", "domain": "sensor", "settings": [{"key": "Address", "label": "Address", "description": "Where the thermometer answers, such as http://192.168.1.60/temperature.", "type": "string", "secret": false}, {"key": "Interval", "label": "Interval", "description": "How many seconds pass between two readings.", "type": "integer", "secret": false, "minimum": 10, "maximum": 3600, "default": 60}]}, "arguments": {"Interval": {"kind": "literal", "type": "number", "value": 60}}}, "position": {"x": 360, "y": 100}}
  ],
  "edges": [
    {"id": "address.value:thermometer.Address", "kind": "data", "source": "address", "target": "thermometer", "sourcePort": "value", "targetPort": "Address"}
  ]
}
```

## Libraries

`library_list({})` returns `{documents,generation}`; `library_read({id})` returns
`{version:1,id,name,revision,source}`. Names are display metadata; IDs are stable.

`library_preview({id,source,workspaceVersion,generation?,sources?})` overlays the
selected source and optional `sources:[{id,source}]` on saved sources. The selected
source wins. The optional generation guards the saved workspace. The response is
`{id,workspaceVersion,generation,sourceGeneration,saved:false,data:{valid,diagnostics,catalog}}`.
Keep the echoed workspaceVersion and sourceGeneration separate from saved
generation when handling delayed results. Preview never executes the methods.

`library_save({id,name,source,expectedRevision})` creates, edits or renames a
source; `library_delete({id,expectedRevision})` removes it. Both validate the
remaining libraries, **all saved graphs** and every entity definition, including C#
references, before committing. Every successful library mutation advances every
graph revision because generated graphs embed the complete library snapshot. Refresh
graph documents after changes. Results contain `saved`, `document`, `deleted`,
`generation`, `catalog` and
`automations:[{id,saved,document,active,activationError}]`, and for entity
definitions `definitions:[{id,saved,document,active,activationError,enabled}]` and
`invalidDefinitions:[{id,name,diagnostics,running,message}]` (those that already had
errors and are left as they are). A save that would break a working definition is
refused, with those definitions under `definitions` in its validation detail.
Compilation/dependency errors and revision conflicts preserve their structured
details. Persistence may succeed while activation fails for a graph; inspect each
activation result.

See [writing C# nodes](node-libraries.md) for Custom C# nodes, the complete library example and
[visual authoring](writing-visual-automations.md) for graph execution rules.
