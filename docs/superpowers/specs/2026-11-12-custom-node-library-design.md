# C# node libraries and rich graph types

This extends [`mcp_and_node_improvements.md`](../plans/mcp_and_node_improvements.md) with the user's supplied editor references and clarifications: graph v4 only, arbitrary C# port types, a typed entity-state result that custom nodes can consume, and a full Roslyn-assisted custom node editor.

## Authoring experience

Add a **Node library** page with a file list, Monaco editor, and a right-hand list of visual previews for the methods in the selected file. Support create, rename, edit, preview, save, and delete with optimistic revision checks. The graph palette includes built-in and saved custom nodes together. The graph inspector, colors, port labels, descriptions, error tooltips, and compact cards follow the supplied screenshots within HassSharp's existing theme.

Public static C# methods marked `[Node("Title", Category = "Custom")]` define reusable nodes. Method parameters define named typed inputs; `[Port("Label")]` overrides a port's label. The return value defines a typed `Result` output. XML `<summary>`, `<param>`, and `<returns>` documentation supplies descriptions. Default parameter values and nullable annotations supply optionality. Support synchronous methods and `Task<T>`/`ValueTask<T>`; `void`, `Task`, and `ValueTask` have execution outputs but no data result. A `CancellationToken` parameter is supplied by the invocation and is not a visible data input.

Library files share a documented `NodeLib` namespace so their classes, records, enums, and helper methods can reference each other. The editor and compiler use the same wrapper/source mapping. Nodes can use any closed C# type, including framework types, user classes/records, interfaces, nullable values, arrays, and constructed generic types. Open generic methods and ref/out/pointer parameters produce precise authoring diagnostics because they do not define fixed graph ports.

For example, a custom method can accept the concrete entity-state class returned by **Read entity**, access its state and attributes, and return a user-defined result record. This remains a typed C# object throughout graph execution.

## Approaches and type model

Use Roslyn to derive node and type metadata from C# signatures. A manually authored JSON schema for every custom node would duplicate the user's C# type declarations and cannot reliably answer C# assignability; restricting values to five JSON types would contradict the user's clarification.

The existing JSON catalog remains the source for built-in node configuration and behavior. At runtime, merge it with Roslyn-generated custom-node descriptors and a type registry into one catalog snapshot. The editor, MCP, backend validator, and compiler consume this same snapshot. Give types stable C# identities, readable labels, nullability, a visual category, and server-derived assignability information. Boolean/Number/String/Object/Array remain visual categories and literal editor choices, not a closed type system.

Connections follow C# identity/reference/standard implicit conversion rules; narrowing or unrelated types require an explicit conversion node. The frontend uses the catalog's assignability relation, and the backend independently validates the same connection against the compiler's type information. User-defined conversion operators are explicit conversions rather than hidden effects of drawing a wire.

**Read entity** exposes a concrete public C# entity-state snapshot with entity ID, state, attributes, and timestamps. Scalar state/attribute extraction remains available through typed extraction nodes. Arbitrary custom types can be wired directly, including from built-in nodes. Rich values are not flattened to anonymous JSON bags between nodes.

Literal editors cover supported scalar/JSON/enum types. Types without a meaningful literal editor are constructed by a custom factory node and connected by wire; they are still first-class port types. Display the actual C# type and nullable status in the inspector and custom-node previews.

Execution remains explicit: data connections read outputs of nodes that have already run. Custom methods do not execute during catalog discovery, validation, compilation, or preview. They run only when execution reaches their graph node.

## Values, scopes, and isolation

The runtime keeps actual CLR values per invocation and scope. Each output records its declared type; consumers use generated typed access/calls. Repeated iterations discard prior body/condition outputs. Parallel branches never share mutable value dictionaries or merge values implicitly.

Snapshot rules must cover rich C# values as well as built-in arrays/objects. Immutable known values may be shared. Mutable arrays/collections and serializable classes are deep-copied at isolated scope boundaries, with an explicit custom snapshot contract available for types that need it. Any CLR value can flow within a scope; a value that cannot be copied safely must fail clearly before it crosses an isolation boundary, rather than silently share mutable state. Custom code's static fields/external resources remain the author's responsibility, as with existing advanced C#.

## Language services

The custom node editor uses the existing Roslyn/Monaco infrastructure with a library-aware workspace. Every request includes the current unsaved file, the other library files, HassSharp framework symbols, and generated Home Assistant entity/service symbols. Provide completion (including members of typed inputs and custom return types), diagnostics, hover/documentation, signature help, formatting, and symbol navigation where supported by the workspace. Respect cancellation, model versions, source mapping, and stale-response suppression.

The right-hand previews update after debounced compilation. Errors mark the exact code locations and prevent saving; warnings remain visible but do not block save. No preview invokes user methods or attaches HA triggers.

## Persistence and activation proposal

Store library source documents under `.storage/hass_sharp` with stable IDs and revisions. Preview/save use a coherent catalog/source snapshot so a concurrent library edit cannot change types halfway through graph validation or compilation. Include the referenced library source snapshot in a graph's generated compilation artifact so each running generation uses fixed definitions.

Saving a library validates it together with all affected saved graphs. Incompatible signature changes are rejected with the affected graph/node/input diagnostics. A valid save regenerates and activates affected graphs through the existing graph persistence/runtime path, reporting saved and activated outcomes separately; graph revisions advance so open editors detect conflicts. Deleting a library that is still referenced is rejected with the list of affected graphs. Already-running code never switches definitions mid-invocation; the normal automation replacement/cancellation policy applies.

Expose library list/read/preview/save/delete and language services through administrator-only shared application services. MCP gets matching library authoring tools and discovers custom descriptors through the same node-schema tools. There is no separate agent-only library store or compiler.

## Verification

- Compile a library defining custom records/classes/enums, inheritance/interfaces and generic collections; derive exact ports/docs/nullability without executing methods.
- Execute **Read entity → custom method accepting entity state → custom result consumer**, preserving actual CLR types.
- Reject incompatible typed wires, malformed signatures, unavailable libraries, stale revisions, and incompatible edits to referenced definitions.
- Verify async cancellation, rich value scope snapshots, parallel isolation, missing values and stale iteration prevention.
- Verify completion for entity-state members and custom types, cross-file symbols, diagnostics/hover/signature help/formatting/source positions, and stale response suppression.
- Test library editor CRUD/previews, graph palette/inspector integration, MCP schema/discovery/authoring, dependent graph activation and revision conflicts.

No graph v1–v3 execution, migration, or compatibility UI is included.
