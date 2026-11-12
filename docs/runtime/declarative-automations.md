# Declarative automations

Task 1 replaces `Initialize`, `RunnableClassScript`, implicit dependency discovery,
and `InitGuard`. Use this API instead of the older README examples.

```csharp
public class BathroomHeating : Automation
{
    public override AutomationConfiguration Configure() => new(
        new AutomationAction(nameof(Heat), AutomationMode.Restart,
            Startup: true, Manual: true, Entities: ["sensor.bathroom_temperature"]));

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
