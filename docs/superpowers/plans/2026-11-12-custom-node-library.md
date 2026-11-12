# Rich C# Node Library Implementation Plan

> **For agentic workers:** Use superpowers:subagent-driven-development. This extends the active `mcp_and_node_improvements.md` implementation; use its existing execution ledger and task reviews, not a second independent execution sequence.

**Goal:** Let built-in and reusable custom nodes exchange actual C# types, with a Roslyn-assisted library editor and a shared discoverable catalog.

**Architecture:** Roslyn derives public node signatures, descriptions, and type compatibility into a catalog snapshot merged with the built-in JSON catalog. Generated graphs embed the referenced library definitions and invoke typed methods under the structured runtime. Shared revision-checked services own library storage, dependent graph revalidation/activation, and language requests.

**Tech Stack:** .NET 10, Roslyn, Python.NET, Home Assistant 2026.11, SolidJS, Monaco, Solid Flow.

**Spec:** `docs/superpowers/specs/2026-11-12-custom-node-library-design.md` (user approved with “Approve and implement”).

## Global Constraints

- Graph v4 only; no old-graph migration or execution branches. API/document envelope 1 remains separate.
- Arbitrary closed C# types can flow directly between built-in and custom nodes; five visual categories are not a type whitelist.
- **Read entity** emits a concrete public C# entity-state snapshot that a custom method can accept directly.
- Data edges never execute producers; preview/discovery never invoke custom methods or attach triggers.
- Public `[Node]` methods define inputs/results; `[Port]` labels and XML docs define presentation.
- Reuse shared catalog/application services and existing graph persistence/activation; administrator-only calls with revisions.
- Full library-aware completion, diagnostics, hover, signature help, formatting, and symbol navigation.
- Deep scope isolation must not silently share mutable CLR values that cannot be safely copied.

## Review Focus

- Two types with identical short names in different namespaces must never connect accidentally (Task 1).
- Unsaved cross-file edits and Unicode positions must produce correct completions/diagnostics and never overwrite newer editor state (Task 3).
- Nullable, interface, derived, generic and numeric assignments must agree between frontend and Roslyn (Task 1/2).
- Invalid shared-node edits and concurrent graph/library saves must not leave a mixed catalog/source generation (Task 3).
- Cyclic/non-serializable rich values at parallel boundaries must fail clearly or use the explicit snapshot contract; no mutable alias leakage (Task 2).

## Task 1: Rich type registry and custom-node metadata (master Task 1b)

**Files:** Create `HassSharp/Visual/NodeAttribute.cs`, `NodeLibraryCompiler.cs`, `VisualCatalogSnapshot.cs`, `EntityStateSnapshot.cs`; update `VisualNodeCatalog.cs`, `VisualGraphValidator.cs`, `VisualGraphContract.cs`, `visual_node_catalog.json`, and frontend `graphV4.ts`. Paths are under `custom_components/hass_sharp`. Tests: `tests/Runtime/NodeLibraryTests.cs`, frontend `tests/visual-v4.test.ts` and shared catalog fixtures.

**Interfaces:**
- `NodeLibrarySource(string Id, string Source, long Revision)` stores source snapshot identity.
- `NodeLibraryCompiler` consumes `DiagnosticsProvider`/framework references and a set of `NodeLibrarySource` values; `Preview` returns validity, mapped per-file diagnostics and a `VisualCatalogSnapshot` without executing/loading user methods.
- `VisualCatalogSnapshot` holds the merged machine-readable catalog, stable custom method/type descriptors, and immutable source snapshots. Built-in helpers/validators accept a snapshot, with the built-in snapshot as their default for isolated tests.
- Custom input storage is namespaced under node `config.arguments` to avoid collisions with method parameters named `type`, `output`, or `service`. Ports retain parameter names as stable IDs and labels independently. Result port ID is `result`.
- Type identities use canonical fully-qualified C# identities, including nullable/generic structure; no random compiled assembly names. Compatibility comes from Roslyn's identity/reference/standard implicit conversions, not short-name equality. Custom conversion operators require an explicit conversion node.

- [ ] Add failing tests that derive a custom record-returning node accepting `EntityStateSnapshot`, preserve XML docs/port labels, and expose the exact declared types.
- [ ] Add tests for async/void/default/nullable/generic/interface signatures, namespace collisions, invalid open-generic/ref/out/pointer signatures, reserved parameter names, and preview without static initialization/method execution.
- [ ] Run the focused .NET harness to demonstrate missing feature failures.
- [ ] Implement attributes, safe Roslyn metadata extraction, type registry and merged catalog; define the concrete entity-state snapshot (ID/state/attributes/timestamps).
- [ ] Extend C#/TS validation and helpers to consume the merged catalog and its assignability relation. Preserve the reviewed malformed-input and bounded terminal-analysis checks.
- [ ] Make Read entity's primary output the concrete entity-state type; keep scalar extraction as a separate operation. Remove its obsolete scalar-only shape and update shared fixtures accordingly.
- [ ] Run focused .NET/TS tests and TypeScript build; record exact interfaces and schema examples for master Task 2, then review/commit.

## Task 2: Typed calls, snapshots and scope execution (within master Task 2)

**Files:** The master v4 generator/runtime files in `HassSharp/Visual/`, plus new `VisualValueSnapshot.cs` and `tests/Runtime/VisualRuntimeV4Tests.cs`.

**Interfaces:** Consume `VisualCatalogSnapshot` and library method descriptors. Generated source includes a fixed referenced-library source snapshot using the same `NodeLib` wrapping/source mapping as preview. Runtime values retain actual CLR objects; generated calls use their declared C# types. Define an explicit public custom snapshot interface for values needing custom deep-copy behavior and document it.

- [ ] Add a failing executed graph for Read entity → custom method accepting its concrete type → custom record consumer, asserting actual CLR types and contents.
- [ ] Add async cancellation, nullable values, numeric/reference conversions, generics/interfaces, missing producer and explicit conversion tests.
- [ ] Add mutable class/collection isolation, cyclic graph copying, custom snapshot and uncopyable boundary tests; no sibling may observe another branch's mutations.
- [ ] Implement typed invocation and rich values in the master structured runtime, without implicit producer evaluation or JSON flattening between nodes.
- [ ] Implement safe snapshot behavior at scope boundaries; preserve shared invocation budgets and cleanup semantics.
- [ ] Run master runtime tests for every built-in/custom node kind and structured flow, then review/commit with the master runtime stage.

## Task 3: Library persistence, dependency activation and Roslyn services (master Task 3b)

**Files:** Create `HassSharp/Visual/NodeLibraryStore.cs`, `NodeLibraryApi.cs`, `HassSharp/NodeLibraryLanguageServices.cs`; modify `VisualApi.cs`, `VisualStore.cs`, `DiagnosticsProvider.cs`, manager initialization, Python shared application services and WebSocket handlers. Tests: `tests/Runtime/NodeLibraryStoreTests.cs`, `HassSharp.Editor.Tests/NodeLibraryLanguageServicesTests.cs`, `tests/python/test_node_libraries.py`.

**Interfaces:** Manager `NodeLibraryRequest(string requestJson): Task<string>` uses the version-1 success/error envelope. Operations: list/read/preview/save/delete and completion/hover/signature/format/definition. Save/delete require `expectedRevision`; source-aware language requests carry library ID, unsaved source and UTF-16 position. Responses contain file identity and mapped source coordinates. Use the existing manager authoring gate for coherent graph/library snapshots.

- [ ] Write failing storage/revision tests, including simultaneous graph and library saves and deletion of a referenced library.
- [ ] Write tests proving an incompatible signature edit identifies dependent graph/node/input errors before persistence; valid edits refresh generated source and activation through the existing path and advance affected graph revisions.
- [ ] Implement atomic library documents and coherent dependency validation/save; return saved/activated status per affected automation, keeping activation errors visible.
- [ ] Write failing language-service tests for typed entity-state/custom result members, cross-file imports/types, nullable/generic signatures, Unicode coordinates, and malformed unsaved source.
- [ ] Implement library-aware Roslyn requests and admin-only WebSocket adapters using shared services. Do not create a second compiler schema or persistence path.
- [ ] Run focused .NET/editor/Python tests and provide exact frontend/MCP request schemas; review/commit.

## Task 4: Node library editor and graph integration (master Task 4b)

**Files:** Create frontend `views/NodeLibraryView.tsx`, `visual/library-api.ts`, library session/preview components; update `App.tsx`, `layout/MainLayout.tsx`, `components/Editor/Editor.tsx`, `editor-actions.ts` and typed graph editor/catalog loading. Tests: `tests/NodeLibraryView.test.tsx`, editor-action tests and typed graph tests.

- [ ] Write failing tests for file CRUD/revision conflicts, debounced previews, typed cards/docs, member completion, diagnostic navigation, stale responses and save blocking.
- [ ] Implement the file-list/Monaco/right-preview layout from `references/library.png` in the master plan workspace, including full-screen and normal layout.
- [ ] Reuse Monaco language providers with a library context; add signature help and definition navigation and preserve existing cancellation/model-version isolation.
- [ ] Fetch merged catalog snapshots for graph palette, inspector, wire validation and actual C# type labels. Apply the user's compact visual references; wire-only types show an actionable factory/connection explanation.
- [ ] Run full frontend tests/build and browser verification; review/commit.

## Task 5: MCP/docs/live acceptance (within master Tasks 5/6)

**Files:** Existing master MCP adapter/docs, plus real HA integration and library workflow tests.

- [ ] Extend schema discovery to merged custom types/nodes and add prefixed library list/read/preview/save/delete tools using the same application services and revisions.
- [ ] Test administrator checks, true parameter schemas, no-code-execution preview, read-only filtering and graph/library concurrent edits through real HA interfaces.
- [ ] Document attributes, XML docs, concrete entity-state usage, rich type conversions, scope copying, language services and dependent activation semantics with complete examples.
- [ ] Run the approved local HA/MCP workflow for create library → preview/save → discover node → create/preview/save/run graph → history → delete graph/library, with harmless synthetic data/events.
- [ ] Complete all master Python/.NET/frontend/publish/Python.NET packaging checks and final whole-branch review. Record evidence and unresolved limits honestly.
