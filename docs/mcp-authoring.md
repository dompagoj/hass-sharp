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
  tools as code execution. `entities_save` also creates entities and a device in
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
library_delete; `trace`, `history` and `entities_history` stay available. Of the 26
tools, 16 remain.

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
   is `{definitions:[…]}`: complete definitions that validate, the five in
   [`authoring_examples.json`](../custom_components/hass_sharp/authoring_examples.json),
   the class one with its library source. `get_node_schema({kind})` adds
   `entityDomains` for `entity` (the Entity node) and `set_entity_state`, whose
   ports come from the entity's type.
2. `entities_types({})` lists the classes the node libraries offer:
   `{types:[…], generation}`, each type `{id, sourceId, typeExpression, title,
   description, kind, domain, settings:[{key, label, description, type, secret,
   options, minimum?, maximum?}]}` (`minimum` and `maximum` only when the setting has
   them). A class is written with `library_save`.
3. `entities_list({})` summarises every definition without its graph: `{id, name,
   revision, enabled, status, activationError, logic, device, entities:[{key,
   domain, name, entityId}], triggers:[{triggerId, label?, type, config, manual,
   runnable}]}`. `status` is `running`, `setup` (starting), `failed` or `off`;
   `logic` is `"graph"` or the class's type id; `device` whether it makes a device:
   it gives device details, or its class derives from `SharpDevice` (for a class no
   node library offers any more, whether it gives device details).
   `entities_read({id})` returns one complete document, with its `revision`,
   `source`, `automationId` and `registeredEntities`, and, for a graph,
   `entities`, the entities its Entity nodes declare, for reading only (a class's
   entities come from its code and are in `registeredEntities`). Read before every
   edit, and send back only `name`, `device`, `settings` and `logic`.
4. `entities_preview({definition})` validates and compiles a definition, `{name,
   device?, settings?, logic}` with `logic` `{kind:"graph", graph}` or
   `{kind:"class", type}`, without creating anything. It returns `valid`,
   `diagnostics` and, for a graph, `graph` with each Custom C# step's pins worked
   out (no settings come back). Unlike the panel's API, it leaves out the generated
   `source` and `sourceMap`, and so does a refusal for errors (`validation`) from it
   or from `entities_save`, whose detail is the preview; `entities_read` has a saved
   definition's `source`.
5. `entities_save({id, expectedRevision, definition, enabled?})` saves it and
   creates or updates its entities, returning `saved`, `document` (as
   `entities_read` gives it, without `source` and `sourceMap`), `active`,
   `activationError` and, when the save has warnings, `diagnostics`. `enabled` is
   only for a new definition (`false` saves it off, and it creates its entities the
   first time it is turned on); `new` is not an id. An entity's key is permanent:
   changing it makes a new entity. A secret setting is sent back as answered to
   keep its value, with a new `value` to replace it, or with `value: null` and no
   `hasValue` to clear it.
6. `entities_set_enabled({id, enabled})` turns a definition on or off (off, the
   entities it has created stay as unavailable; a definition saved off creates its
   entities the first time it is turned on) and returns `enabled`, `active` and
   `activationError`. `entities_delete({id, expectedRevision})` deletes it and
   removes its entities and device.
7. `entities_run({id, triggerId})` runs one Trigger step of a running definition,
   one whose `runnable` is true, and returns `runId`. Nothing else runs by hand: a
   command runs when the entity's service is called, an Entity node's state follows
   the entities it reads, and When started runs when the definition starts.
8. `entities_history({id, status?, offset?, limit?})` returns the definition's runs
   as `history` does an automation's; `trace` reads any run of either. An Entity
   node's `config.history` (`{state, commands, frame}`) says which of its runs are
   recorded: its commands' unless `commands` is false, its state's and its frame's
   only when `state` or `frame` is true. When started records its runs unless its
   `trackHistory` is false, a Trigger only when it is true (the opposite of an
   automation's default); a run started with `entities_run` is always recorded. An
   Entity node's runs are named `Relay: State`, `Relay: Turn on` or `Relay: When a
   frame is needed` (its entity's name, else its key, and the pin), and their trace
   has the node as `rootNodeId` and the pin as `rootPort` (`state` for its state).
   A graph that `trace` returns with `graph: true` carries the definition without
   its settings' values: it is not a definition to save.

### The Entity node

Each entity of a graph definition is a node of kind `entity`. Its `config` is
`{key, domain, name, icon?, deviceClass?, entityCategory?, enabledByDefault?,
options?, history?, arguments?}`: `key` matches `^[a-z][a-z0-9_]{0,39}$` and is
unique, `domain` is one of the ten in the catalog's `entityDomains` and `options`
are that domain's. Leave out a field the entity does not need: `name`, `icon`,
`deviceClass` and `entityCategory` take no `null`. An entity of a definition
without a device needs a `name` (`entity.name`). Other steps, such as Set state,
Set available and Show frame, name it by its key in `config.entity`.

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
- **Frames.** A camera's node has the execution output `frame` instead (When a
  frame is needed); a camera that no Show frame step names is a warning
  (`camera.no_frame`).

The relay of the example *Plug over HTTP* and the edges of its two commands:

<!-- example: Plug over HTTP -->
```json
{
  "nodes": [
    {"id": "relay", "kind": "entity", "config": {"key": "relay", "domain": "switch", "name": "Relay", "deviceClass": "outlet"}, "position": {"x": 60, "y": 640}}
  ],
  "edges": [
    {"id": "relay:command:turn_on:switch_on", "source": "relay", "target": "switch_on", "port": "command:turn_on"},
    {"id": "relay:command:turn_off:switch_off", "source": "relay", "target": "switch_off", "port": "command:turn_off"}
  ]
}
```

A run that reaches a Stop step is Stopped, not failed: a command it handled
succeeds and a When started flow that stops lets the definition start.

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
