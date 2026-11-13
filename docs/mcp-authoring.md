# Authoring through Home Assistant MCP

HassSharp requires **Home Assistant 2026.11 or newer** and uses Home Assistant's
built-in MCP Server. There is no separate HassSharp MCP server or port. The local
Compose image is pinned to `homeassistant/home-assistant:2026.11.1`.

Add **MCP Server** under Settings → Devices & services if it is not already
configured. Open its options and select **HassSharp** from the available LLM APIs
(or include all APIs). Keep administrator authentication enabled. A client using
an administrator's Home Assistant credentials can connect to `/api/mcp`; for the
local development instance that is `http://localhost:5001/api/mcp`.

The direct endpoint `/api/mcp/hass_sharp` selects only HassSharp and always
requires an administrator. Home Assistant's direct endpoint does **not** apply
the configured MCP entry's read-only setting. Use configured `/api/mcp` with
**Read only** enabled to expose only queries and previews. Refresh/reconnect the
client's tool discovery after changing selected APIs, options, or reloading
HassSharp. The API registers only after the integration is ready; unloading it
unregisters the API and invalidates tools a client has cached.

Use the MCP client's supported Home Assistant authentication, or an administrator
long-lived access token in the `Authorization: Bearer ...` header. Keep tokens out
of source control and prompts. HTTP POST accepts JSON-RPC MCP requests with
`Content-Type: application/json` and `Accept: application/json`; Home Assistant
handles transport/session behavior. The API also checks the current user on every
tool invocation, including direct calls and cached tools.

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
   its `config.fields`. In port types, `$any` stands for every type and `$type`
   (generally `$<name>`) for the type set in the node's `config.<name>`. Without
   `kind` it returns the whole catalog.
3. Use `discover` to find entities, services, devices, areas or installed trigger
   descriptions. Supply `kind`, optional `query`, `offset` (default 0) and `limit`
   (default 50, maximum 100); entities and services also take `domain`.
   Filtering happens before stable sorting/pagination. Services come from the same
   normalised schemas the validator uses: without `service`, one summary line per
   service (`id`, `name`, `description`, `hasTarget`, `response`); with `service`,
   that service's complete schema, fields in Home Assistant's order. A service that
   is not registered (yet) gives `known: false` here and in `get_node_schema`.
   Discovery never attaches triggers or calls services.
4. `list` returns every saved document with its complete graph (named by
   `graph.name`), revision, activation state and
   `triggers:[{triggerId,method,manual,runnable}]`, without the generated source.
   `read({id})` returns one complete document including `source` and `sourceMap`.
5. `preview({graph})` validates and compiles a complete graph against the
   registered service schemas, including HA-native trigger declarations, returning
   `valid`, `source`, `sourceMap`, `diagnostics`. It does not attach triggers or
   execute custom code.
6. `save({id,expectedRevision,graph})` persists and activates through the editor's
   same authoring service. New IDs use revision 0; existing edits use the exact
   read revision. Warnings are allowed; severity 3 errors prevent persistence.
   Saving can enable startup, scheduled or native effects, so authorize those
   effects before saving. The result separates `saved:true` from `active` and
   `activationError`.
7. With authorization, `run({id,triggerId})` dispatches a manually eligible trigger
   on the exact current saved active revision and returns `{version:1,runId}`.
8. `history({id,status?,offset?,limit?})` returns run history version 2 for that
   document, `{id,version:2,offset,limit,runs}`, newest first, optionally only runs
   with the given statuses. See [run history](backend/run-history.md) for the run
   fields.
9. `trace({runId,node?,graph?,offset?,limit?})` reads one run's trace. By default it returns the
   run, `live`, the snapshot identity (`revision`, `visualId`, `visualRevision`),
   and the trace's `rootNodeId`, `startedUtc`, `complete`, `truncated`, `dropped`,
   `summary` and a page of the events in order without their values (node, frame,
   status, port, `t`, `ms`, error), with `totalEvents`. `node` returns that node's
   events with their `inputs` and `outputs`, or `not_found` when it never ran;
   `graph: true` adds the graph revision that ran. `offset` and `limit` page the
   events; `limit` defaults to 100 without `node` and 10 with it, at most 500. The catalog stored
   with the snapshot is never returned. `trace` is `null` for a run without one.
10. `delete({id,expectedRevision})` deletes a document and stops its runtime.

Graph nodes are `{id,kind,config,label?,position?}`; a stored `size` from an older
editor is removed from documents MCP returns, and the graph schema rejects it.
Write entity IDs for domain-filtered inputs with their exact type: the target of
`light.turn_on` takes `{"kind":"literal","type":"entity_id:light","value":"light.hall"}`,
and a wired plain `entity_id` needs a Convert node with type `entity_id:light`
first. See [visual authoring](writing-visual-automations.md#types-and-refinement).

Tool arguments are strict: unknown fields and wrong types are rejected. Revisions
and pagination values must be integers (booleans are not accepted). Stable IDs
match `[a-z0-9][a-z0-9_-]{0,79}`. All tools require administrator privileges.
Read-only MCP mode removes save, delete, run, library_save and library_delete;
`trace` and `history` stay available.

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

See [custom node libraries](node-libraries.md) for the complete typed example and
[visual authoring](writing-visual-automations.md) for graph execution rules.
