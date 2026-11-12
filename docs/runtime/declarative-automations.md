# Declarative automations

Task 1 replaces `Initialize`, `RunnableClassScript`, implicit dependency discovery,
and `InitGuard`. Use this API instead of the older README examples.

```csharp
public class BathroomHeating : Automation
{
    public override AutomationConfiguration Configure() => new(
        new AutomationAction(nameof(Heat), AutomationMode.Restart,
            Startup: true, Manual: true, Entities: [Entities.Sensor.BathroomTemperature]));

    public async Task Heat()
    {
        var temperature = Entity("sensor.bathroom_temperature");
        await Task.Delay(TimeSpan.FromHours(1), CancellationToken);
        temperature = temperature.Refresh();
        CancellationToken.ThrowIfCancellationRequested();
        Logger.Info($"{Cause}: refreshed temperature {temperature.RawState}");
    }
}
```

Configure returns declarations synchronously; it cannot be an async method.
Actions must be public parameterless Task/ValueTask methods. Entity reads do not
register triggers. Duplicate entity IDs for an action register only once.
All classes are configured and subscribed before startup actions are dispatched.
Startup does not block loading. Startup, entity and manual calls share each
method's Single (skip), Restart (cancel prior), Queued (arrival order), or Parallel
policy. Restart cancellation is cooperative: use CancellationToken for delays
and check it before external effects. It cannot undo an already dispatched service.
Cause identifies the current invocation; TriggeredBy is only set for entity calls.
The runtime manual entry point is UserScriptClass.RunMethod; the public bridge and
UI follow in Task 3. No history persistence or scheduling is included here.

Verification: `dotnet run --project tests/Runtime --disable-build-servers` and
`python3 -m unittest discover -s tests/python` from the repo root. The executable
harness exits nonzero on any failed assertion, without extra test dependencies.

### Migrating entity triggers

`AutomationAction.Entities` now accepts `IEntityReference[]`. Replace string IDs such as `Entities: ["binary_sensor.hall_motion"]` with `Entities: [Entities.BinarySensor.HallMotion]`. The generated `Entities` catalog comes from the live Home Assistant entity registry and is shared by compilation, diagnostics and completion. Domain wrappers retain their state and service APIs when passed to `Entity(...)`. Mixed domains can share one trigger list.

Names use PascalCase, invalid identifier characters become word boundaries, and leading digits gain `_`. Names matching their enclosing domain gain `Entity` (for example `Entities.Sun.SunEntity`). Plain symbols belong only to their canonical snake_case identity: `hall_motion` owns `HallMotion`, while aliases such as `hall-motion` always receive an ID-derived suffix, even when they are the only entity present. Domain aliases follow the same rule. This ownership is independent of catalog contents and survives replacement, removal and restart. Reserved names also receive suffixes (for example `sun.sun_entity` cannot take `Sun.SunEntity` from `sun.sun`). Refreshing the live catalog updates editor symbols and subsequent compilations. Stored visual graphs keep entity ID strings; compiling them resolves those IDs through the same catalog and reports missing entities at their graph node. On startup, saved graphs are regenerated against the current catalog, including older graphs whose saved source used string triggers. Read/list responses retain the last-saved source and its matching maps as provenance; request a live preview for current generated source/maps. Startup compiles regenerated source without rewriting the saved document or its revision.

External DLL projects compile outside the live editor and must provide their own typed catalog (for example `EntityRefWrapper<HaInputButton>` properties), as shown in `examples/ExampleUserDll`. They do not have the runtime generated `Entities` class at build time.
