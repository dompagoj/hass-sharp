# Authoring through Home Assistant MCP

HassSharp requires **Home Assistant 2026.11 or newer** and registers an LLM API
with Home Assistant. Home Assistant's built-in MCP Server serves it to MCP clients,
and any conversation agent an administrator configures with it (OpenAI, Anthropic,
Google, Ollama and others) gets the same tools. There is no separate HassSharp MCP
server or port. The local Compose image is pinned to
`homeassistant/home-assistant:2026.11.1`. Read [Security](#security) before
giving the API to an agent.

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
- **What a change can do.** `save` and `library_save` compile C# and load it into
  Home Assistant's own process, with its privileges: the configuration directory
  with `secrets.yaml` and the `.storage` tokens, the network and every service.
  Static constructors run when the code is activated. Treat these tools as code
  execution. Agents do not ask before a tool call; the API prompt tells the model
  to change nothing unless the user asks for it in the conversation, and that is
  advice to the model, not a control.
- **Read only.** Give an agent that must not change anything an MCP Server entry
  with **Read only** enabled. That removes `save`, `set_enabled`, `delete`, `run`,
  `library_save` and `library_delete`. A conversation agent given the API gets
  every tool, the changing ones included.
- **What leaves the house.** Tool results go to the model's provider: entity ids,
  names and states (discovery lists entities whether or not they are exposed to
  assistants), service descriptions, graphs and their C#, run errors and trace
  values. Discovery returns only a few attributes of each entity (see step 3), so
  a camera's `access_token` and an `entity_picture` URL with a token stay in Home
  Assistant.
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
   history, C#) and complete example graphs that validate and compile.
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
   C# node's `config.signature.types`. Without `kind` it returns the whole catalog.
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
   and the trace's `rootNodeId`, `startedUtc`, `complete`, `truncated`, `dropped`,
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
Read-only MCP mode removes save, set_enabled, delete, run, library_save and
library_delete; `trace` and `history` stay available.

A failed replacement can leave an older generation running. Document `active`
means some generation is active; **`currentRevisionActive`** means the saved
revision and source match the active runtime. `activeRevision`, `activeSourceHash`
and `activationError` explain the difference. Run resolves the stable trigger and
checks/dispatches under the same managed authoring gate as saves and library
mutations. A stale active generation cannot run as though it were the newer saved
graph. Trigger method identities are `Run_` plus uppercase UTF-8 hexadecimal of
the trigger node ID; clients should pass `triggerId`, not construct method names.

## Libraries

`library_list({})` returns `{documents,generation}`; `library_read({id})` returns
`{version:1,id,name,revision,source}`. Names are display metadata; IDs are stable.

`library_preview({id,source,workspaceVersion,generation?,sources?})` overlays the
selected source and optional `sources:[{id,source}]` on saved sources. The selected
source wins. The optional generation guards the saved workspace. The response is
`{id,workspaceVersion,generation,sourceGeneration,saved:false,data:{valid,diagnostics,catalog}}`.
Keep the echoed workspaceVersion and sourceGeneration separate from saved
generation when handling delayed results. Preview never executes the methods.

`library_save({id,name,source,expectedRevision})` creates, edits or renames a source;
`library_delete({id,expectedRevision})` removes it. Both validate the remaining
libraries and **all saved graphs**, including C# references, before committing.
Every successful library mutation advances every graph revision because generated
graphs embed the complete library snapshot. Refresh graph documents after changes.
Results contain `saved`, `document`, `deleted`, `generation`, `catalog` and
`automations:[{id,saved,document,active,activationError}]`. Compilation/dependency
errors and revision conflicts preserve their structured details. Persistence may
succeed while activation fails for a graph; inspect each activation result.

See [writing C# nodes](node-libraries.md) for Custom C# nodes, the complete library example and
[visual authoring](writing-visual-automations.md) for graph execution rules.
