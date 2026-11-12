# Authoring experience implementation plan

> **For agentic workers:** Use superpowers:subagent-driven-development task-by-task. User explicitly requested subagents for implementation and code review.

**Goal:** Address all nine reported authoring issues: diagnostics, embedded help, typed entities, reliable completion, advanced code sections, entity/service pickers, useful categorized nodes, and full-height canvas.
**Architecture:** Retain SolidJS, Monaco/Roslyn, generated Entities and EntityRef wrappers, and graph-to-C# compilation. Extend the current systems rather than replacing them. Keep existing saved graphs readable. Runtime strings remain serialization details; authored C# uses generated typed references.
**Tech Stack:** SolidJS, Monaco, Roslyn, .NET 10, Python/Home Assistant.
**Spec:** User's nine requirements in this session; this plan records concrete implementation decisions. The existing automation-platform design remains applicable.

## Global Constraints
- Preserve saved graphs and existing user data. No container recreation, push, or deployment to external services.
- Generated C# entity references must be checked by the compiler, with domain types preserved; no implicit string conversion that defeats type safety.
- Keep stale-response, cancellation, model disposal and per-invocation isolation guarantees.
- Regression tests must exercise actual behavior. Use existing frontend, C# and Python test harnesses.
- Work on fix/authoring-experience in the shared checkout. Do not spawn nested subagents. Do not commit: controller retains reviewable working-tree changes.

## Review Focus
- Typing while a completion is pending must recover for the newest text, without stale edits.
- Monaco overlays must render within Home Assistant's shadow-root styling.
- Generated entity identifiers must survive name collisions and entity registry refresh.
- Picker cancellation must not mutate the stored value or reopen after focus restoration.
- Graph data must be scoped to an invocation and validated before code generation.

### Task 1: Editor diagnostics and completion
**Files:** frontend/src/components/Editor/*, corresponding frontend/tests, HassSharp/DiagnosticsProvider.cs and HassSharp.Editor.Tests where necessary.
- [x] Reproduce and fix missing hover diagnostics, considering Monaco fixed overflow widgets in the Lit shadow root and CSS ownership. Expose diagnostic list state from useEditorActions and render clickable errors/warnings under the editor that focus the exact range. Surface validation transport failures and recovery.
- [x] Reproduce autocomplete gaps during fast typing and incomplete lists. Fix request cancellation/retriggering and Roslyn completion translation as required; preserve stale response rejection and multiple editor isolation. Include C# namespaces and accurate text edits.
- [x] Add failing regression tests before fixes for asynchronous typing recovery, diagnostics clearing and navigation, and meaningful Roslyn cases. Run frontend suite/build and editor tests if changed.
- [x] Write task report including root causes, changes, test commands/results and limitations.

### Task 2: Typed entity configuration
**Files:** HassSharp/EntityGenerator.cs, EntityRef/EntityRefs.cs, UserScripts/AutomationConfiguration.cs, runtime consumers, Visual/VisualGenerator.cs, examples/docs and C# tests.
- [x] Replace AutomationAction.Entities string[] with a typed shared entity-reference abstraction implemented by domain wrappers; convert to IDs only at runtime subscription/serialization boundaries. C# authors can write Entities: [Entities.BinarySensor.HallMotion]. Preserve domain-specific state/service APIs.
- [x] Ensure runtime generated Entities are shared between diagnostics, completions and compilation and refresh correctly. Make generation deterministic and robust to sanitized name collisions, reserved names and invalid identifiers. Use the same naming resolver for graph-generated typed references.
- [x] Update generated visual triggers, examples, docs and regression tests to type-safe references; legacy stored graph IDs remain accepted and validated against live entities when compiled. Explain API migration in docs.
- [x] Test typed mixed-domain triggers, rejection of raw strings, collision handling, refresh and compile compatibility. Run runtime and editor test suites.
- [x] Write task report.

### Task 3: In-app help, discovery dialogs and layout
**Files:** frontend/src/components (HelpDialog, shared picker, EntityAutocomplete, service picker), entities/services data hooks, visual/VisualEditor.tsx, TriggerConfig.tsx, Editor.tsx, style.css and tests. Python service metadata endpoint only if existing HA API is insufficient.
- [x] Replace all Help GitHub links with local accessible dialogs with practical C#/visual help and typed examples. Prefer bundling existing docs with safe rendering or concise maintained content.
- [x] Replace narrow entity dropdown with a large dialog opened by clicking the field; searchable by ID and friendly name, category/domain filter, category icons, current selection, loading/retry/empty states, keyboard navigation, escape and focus return. Searching/canceling must not mutate saved selection. Preserve ability to explicitly retain an unavailable entity ID.
- [x] Provide equivalent service selection from live HA service metadata, searchable name/id/description, domain filter/icons, and integrate all service fields. Fetch fresh catalog on open; avoid hundreds of tab stops and handle long lists with bounded scrolling.
- [x] Collapse imports/members under Advanced C# by default, retain content, and automatically reveal it when locating section errors. Explain helpers and imports clearly.
- [x] Fix visual canvas to stretch to available grid height and viewport space on desktop, with usable minimum on mobile. Do not simply increase fixed pixel height.
- [x] Tests for dialog selection/cancel/filter/keyboard, service metadata and help, advanced sections, and responsive sizing as feasible. Run frontend suite/build.
- [x] Write task report.

### Task 4: Categorized graph logic and values
**Files:** frontend/src/visual/{graph,NodeChooser,FlowAdapter,VisualEditor}.tsx/ts, new focused node configuration modules, HassSharp/Visual/VisualGenerator.cs and focused runtime helper, docs, frontend and runtime tests.
- [x] Add categories Hass, Logic, Flow, Data and Code to both palettes and search chooser. Keep existing nodes and stored graph behavior.
- [x] Implement useful first-class nodes: If, And, Or, Xor, Not, Compare, read entity state/attribute, Set value, Delay, Stop, Log, Repeat and Wait until. Keep C# extension nodes. Branch nodes have true/false/error outputs; ordinary steps next/error. Use invocation-local typed value storage and typed operands (literal or variable reference), with explicit boolean/number/string conversions and actionable validation. Node fields must have labels and constrained selectors, not raw property-name text fields.
- [x] Logical nodes consume boolean literals/variable references and produce named boolean outputs usable by If. Read entity supports state and attribute selection with result type. Compare supports equality/inequality and numeric ordering. Variable use must produce useful diagnostics for missing/incompatible inputs, not silent defaults. Calls support service payload data where practical.
- [x] Repeat and Wait until must be structured, cancellable operations with bounded counts/timeouts, not unrestricted graph cycles. If safe structured body support requires schema extension, use versioned graph data while preserving v1/v2 reads. Keep source-to-node mappings and validation of all new node configurations.
- [x] Test real generated C# compilation/execution for true/false branches, truth tables, extraction/comparison pipeline, invocation isolation, repeat cancellation and wait timeout, invalid configurations, saved graph roundtrip and categorized chooser filtering. Update help/docs to accurately describe the implemented limits.
- [x] Run frontend suite/build, runtime tests and Python tests if wire schema changes. Write task report.
