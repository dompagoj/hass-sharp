# Editor regressions

From the repository root:

```sh
dotnet test custom_components/hass_sharp/HassSharp.Editor.Tests
(cd custom_components/hass_sharp/frontend && bun install --frozen-lockfile && bun run test && bun run build)
```

For a sandbox that prohibits the test host's local socket, the same xUnit suite can run in process:

```sh
dotnet run --project custom_components/hass_sharp/HassSharp.Editor.Tests --disable-build-servers
```

The .NET tests cover the generated typed service calls (`context.Services`) and the node library
editor's real Roslyn completions, hover, signatures, definitions and formatting. Frontend tests run the real editor
actions and Solid component against a Monaco host double and controlled WebSocket replies. They
cover stale results (including changes between reply delivery and consumption), cancellation,
model isolation, Save, and resource disposal. They do not replace a live Home Assistant/Monaco
browser smoke test.
