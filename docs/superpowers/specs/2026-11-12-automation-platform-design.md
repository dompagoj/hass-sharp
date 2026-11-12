# HassSharp automation platform design

## Intent

Make C# and visual automations reliable, observable, and pleasant to author in the Home Assistant sidebar. Both authoring modes compile to the same C# runtime. There are no existing user scripts to migrate.

## Decisions

- A synchronous configuration phase declares entity, startup, manual, and time triggers. User work runs only through a single asynchronous invocation pipeline. Configuration cannot await a long-running action. Remove the old `RunnableClassScript.Initialize()` pattern rather than retaining compatibility.
- All trigger kinds use the same Single, Restart, Queued, or Parallel policy and carry a structured cause. Cancellation belongs to an invocation; service dispatch and actual device state are separate outcomes.
- Store run records in a private SQLite database under Home Assistant's persistent configuration directory, using EF Core migrations. Do not write HassSharp tables into Home Assistant Recorder. Retain bounded records and paginate reads.
- Register wall-clock schedules through Home Assistant time helpers, with explicit local timezone, daylight-saving behavior, missed-run policy, and unsubscribe on replacement/unload.
- Offer C# and Visual at creation. A versioned, JSON-serializable graph generates deterministic C# and source mappings. The graph and generated source save atomically. Arbitrary C# remains code-authored; custom C# nodes and sections provide capability parity. Editing generated C# explicitly switches to code mode.
- Use solid-flow behind an adapter after a canvas feasibility test; patch or fork if required for state updates, keyboard/touch input, resizing, and style isolation.
- Keep the Lit panel boundary and replace visible `ha-*` controls with polished Solid components. Improve the existing Roslyn-backed Monaco service before adding a new language server.
- Add Run now and preview/validation before save. Preview shows generated C# and maps errors to nodes.

## Acceptance

The BathroomHeating scenario can start on a configured trigger, restart a cancellable one-hour delay, refresh the entity, and record each attempt and outcome. Reload neither loses subscriptions nor leaves scheduled callbacks. Code and graph examples produce equivalent behavior. Monaco completes current entities after refresh, saves with Ctrl/Cmd+S, and discards stale responses. The UI works with keyboard, light/dark themes, and narrow widths.

## Boundaries

No promise of lossless C# to graph conversion, exactly-once external service effects, or hard real-time scheduling. No existing-script migration is required.
