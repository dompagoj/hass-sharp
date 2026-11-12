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
fresh read rather than retrying with the old revision.

1. Call `get_capabilities` and `get_node_schema`. These return the actual merged
   built-in/custom catalog, graph version4, CLR types, assignability, nullability,
   defaults, constraints and complete harmless examples.
2. Use `discover` to find entities, services, devices, areas or installed trigger
   descriptions. Supply `kind`, optional `query`, `offset` (default0) and `limit`
   (default50, maximum100). Filtering happens before stable sorting/pagination.
   Service entries include data fields and target descriptions; discovery never
   attaches triggers or calls services.
3. `list` and `read({id})` return saved graphs, revisions, activation state and
   `triggers:[{triggerId,method,manual,runnable}]`.
4. `preview({graph})` validates and compiles a complete graph, including HA-native
   trigger declarations, returning `valid`, `source`, `sourceMap`, `diagnostics`.
   It does not attach triggers or execute custom code.
5. `save({id,expectedRevision,graph})` persists and activates through the editor's
   same authoring service. New IDs use revision0; existing edits use the exact
   read revision. Warnings are allowed; severity3 errors prevent persistence.
   Saving can enable startup, scheduled or native effects, so authorize those
   effects before saving. The result separates `saved:true` from `active` and
   `activationError`.
6. With authorization, `run({id,triggerId})` dispatches a manually eligible trigger
   on the exact current saved active revision and returns `{version:1,runId}`.
   `history({id,offset?,limit?})` returns its existing runtime run records.
7. `delete({id,expectedRevision})` deletes a document and stops its runtime.

Tool arguments are strict: unknown fields and wrong types are rejected. Revisions
and pagination values must be integers (booleans are not accepted). Stable IDs
match `[a-z0-9][a-z0-9_-]{0,79}`. All mutations require administrator privileges.
Read-only MCP mode removes save, delete, run, library_save and library_delete.

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
