# HassSharp MCP and typed visual automations

## Summary

Enable agents to discover, create, edit, validate, activate, run, and debug HassSharp visual automations through Home Assistant’s built-in MCP server. Expand the editor with typed data connections, all installed HA trigger types, parallel branches, and richer loops.

Register a separate administrator-only **HassSharp** tool set. Keep saving and activation coupled, matching current behavior. Raise the minimum supported Home Assistant version to **2026.11**.

## 1. Shared graph contract and typed connections

- Make **graph v4** the only supported visual graph format. Backward compatibility with v1–v3 and migration of existing graphs are not required; breaking existing graphs is acceptable. Keep the version-1 API/document envelope and revision checks, which are separate from the graph format.
- Define one machine-readable node catalog containing configuration, inputs, outputs, types, descriptions, defaults, and constraints. Use it for the editor, MCP discovery, and backend validation.
- Distinguish **execution edges** from **data edges**. Execution edges retain their current shape; data edges identify source and destination ports. Missing edge kind means execution.
- Execution connections determine when nodes run. Data connections read outputs from nodes that have executed; they never execute dependencies implicitly.
- Support Boolean, Number, String, Object, and Array data types, with descriptive refinements such as Entity ID. Require explicit conversions between incompatible types.
- Allow one data source per input and multiple consumers per output. Reject incompatible types, flow/data mixing, forbidden scope crossings, and data cycles.
- Store outputs per invocation and execution scope. Missing values produce precise errors rather than defaults.
- Connecting an input replaces its active literal. Disconnecting leaves it explicitly unset; required inputs become invalid. Undo restores the complete prior state.
- Support named variable references and custom C# capabilities in v4. Preserve supplied node IDs, trigger identities, and layout when editing v4 documents. Do not add legacy graph execution or migration paths.

## 2. Clear node details and error feedback

Reorganize the inspector into a node heading, **Inputs**, **Outputs**, and collapsed **Appearance** settings.

- Every input shows its name, type badge, required/optional status, description, and either a literal editor or its connected source.
- Every output appears individually with its name, type, meaning, and connected destinations. Preserve keyboard and touch connection controls.
- For **If**, show **Condition · Boolean · Required** and explain its branching behavior. True and False are execution outputs, not Boolean data outputs.
- Render labeled data and execution ports on the canvas. Use shared color tokens across canvas circles, wires, and inspector rows: Boolean purple, Number blue, String green, Object orange, Array cyan, execution neutral, and error routes red. Always include text labels so color is supplementary.
- Move width and height into Appearance. Size nodes to accommodate their visible ports.

Enforce validation at both interaction and backend boundaries:

- Reject an incompatible drag before creating an edge. Highlight the destination input and explain the exact mismatch, such as “Condition requires Boolean; Temperature outputs Number.”
- Invalid graphs show persistent red node borders and highlights. Hovering or keyboard-focusing a node opens its exact errors.
- Show a unified, clickable error list immediately below the canvas. Selecting an error focuses its node and input.
- Combine local connection/configuration errors with backend compilation and HA trigger diagnostics. Clear resolved errors; errors block saving, warnings do not.
- Apply identical validation to MCP submissions so agents cannot bypass editor restrictions.

**Connection removal:** Alt-primary-click, or Option-click on macOS, removes only the clicked execution or data connection. Commit one undoable change, suppress canvas click actions, and preserve ordinary clicks, existing removal controls, and undo/redo.

## 3. Trigger, flow, and data capabilities

### Home Assistant triggers

- Add a native HA trigger configuration to graph v4 and optional trigger declarations to `AutomationAction`.
- Validate and attach using HA’s trigger helpers. Provide friendly fields for state, numeric-state, event, time, time-pattern, sun, and zone triggers, plus a structured JSON editor for other installed trigger platforms.
- Expose trigger variables through typed data outputs and immutable `TriggerData` in C#. Add an invocation cause without changing existing enum values.
- Await trigger registration during activation. Roll back partial registrations, ignore callbacks from inactive script generations, and unsubscribe on replacement, deletion, and unload.
- Preview validates without attaching triggers or executing code. Preserve existing daily/once scheduling semantics; native HA triggers retain HA’s own timing semantics.

### Structured execution

- **Parallel:** stable branch ports and an explicit joined continuation. Branches receive isolated copies of values, responses, and error state. An unhandled failure cancels siblings, awaits cleanup, and follows the parallel node’s error route. No implicit value merging.
- **While/Until:** a separate visual condition subgraph runs for every test and ends with a Boolean condition-result node. While tests before the body; Until tests after it.
- **For each:** sequential iteration over an array snapshot, with scoped item and index outputs.
- Sequential loops carry named values between iterations and expose only explicitly declared exports. Zero-iteration exports use initialized values; missing exports fail clearly. Node outputs reset between iterations to prevent stale reads.
- Keep structured bodies isolated from outside execution edges. Stop ends the whole invocation, including parallel work.
- Retain limits of eight nesting levels and 100,000 execution steps per invocation; default loop limits to 1,000 iterations, configurable downward. Share the step budget across parallel branches.

### Useful data bindings

Add Object/Array literals and constructors, field extraction using JSON pointers, trigger-data access, service-response access, and explicit conversions. Allow runtime service targets and data to consume typed inputs while keeping service selection and trigger declarations as configuration.

Keep custom C#, imports, and members available for advanced behavior.

## 4. MCP integration and agent workflow

Register an administrator-only LLM API with ID `hass_sharp`, using HA’s built-in registration and lifecycle APIs. Home Assistant serves registered APIs over MCP; `/api/mcp` exposes the selected sets, and `/api/mcp/hass_sharp` addresses this set directly. ([HA API documentation](https://contributors.home-assistant.io/core/internals/llm-api/))

Provide tools prefixed `hass_sharp__`:

| Tools | Purpose |
|---|---|
| `get_capabilities`, `get_node_schema` | Supported graph versions, typed ports, constraints, C# guidance, and examples |
| `discover` | Filtered/paginated entities, services, devices, areas, and trigger capabilities |
| `list`, `read` | Documents, revisions, activation status, and runnable trigger identities |
| `preview` | Graph/HA validation, compilation, generated source, and node diagnostics |
| `save`, `delete` | Revision-checked authoring through existing storage/runtime paths |
| `run`, `history` | Run a selected visual trigger and inspect execution results |

- Share application services with WebSocket handlers; avoid a second persistence or activation implementation.
- Return structured validation/conflict errors and distinguish “saved successfully” from “activated successfully.”
- Enforce administrator access inside tool calls and declare accurate read-only/destructive annotations.
- Provide a prompt guiding discovery → authoring → preview → save → execution/history checks.
- Accept complete graphs, preserve supplied layout, and fill missing layout deterministically so agent-created documents open legibly.
- Register only while HassSharp is ready and unregister on unload.
- Document selecting HassSharp in the existing MCP configuration and refreshing client tool discovery.

## 5. Implementation order and acceptance

Implement in this order: shared v4 contract and validation; typed execution/data runtime; HA trigger bridge and structured flow; inspector/canvas interactions; MCP adapter and documentation.

Required verification:

- Compile and execute representative graphs for every node kind, including Boolean If inputs, response-driven service calls, nested loops, and parallel cancellation.
- Test incompatible connections, missing producers, scope violations, condition paths without Boolean results, zero iterations, stale-value prevention, and execution limits.
- Test trigger validation, payloads, failed attachment cleanup, replacement, reload, and unload against real HA interfaces.
- Verify v4 graph/source round trips, clear rejection of unsupported graph versions, and concurrent editor/MCP revision conflicts.
- Test inspector type labels, matching port colors, rejection explanations, hover/focus errors, error-list navigation, and Alt/Option-click undo/redo.
- Exercise real MCP discovery, parameter schemas, administrator enforcement, read-only filtering, and create → preview → save → read → run → history → delete.
- Run Python tests, the .NET runtime harness, frontend tests/build, publish, and Python.NET packaging smoke checks.
- Verify the complete workflow in the local HA instance using test events and harmless automations.

The configured `hass` connection is currently unavailable to this session, and the local development container is stopped. Live MCP verification remains an implementation acceptance requirement; it must not be reported as passed without evidence.
