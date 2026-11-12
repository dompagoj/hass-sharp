# Writing C# automations

In **Automations**, choose **New automation → C#**, give it a name, and replace
the starter code with an example below. Use your own entity IDs. Save compiles
and activates the script; actions marked `Startup: true` can run immediately.
Use **Run now…** to test an action and **Run history** to inspect its result.

These examples use the current `Automation` and `Configure()` API. Older
`RunnableClassScript`, `Initialize` and `InitGuard` examples are not compatible
with this API. Reading an entity does **not** subscribe to changes: declare
subscriptions in `AutomationAction.Entities`.

## 1. A manual action

This is a safe first test: it writes a log entry without controlling a device.
Save it, open **Run now…**, and choose `HelloHome.SayHello`.

```csharp
public class HelloHome : Automation
{
    public override AutomationConfiguration Configure() => new(
        new AutomationAction(nameof(SayHello), Manual: true));

    public Task SayHello()
    {
        Logger.Info($"Hello from HassSharp! Cause: {Cause}");
        return Task.CompletedTask;
    }
}
```

`Configure()` declares when each action runs. Action methods must be **public,
parameterless**, and return `Task` or `ValueTask`. `Configure()` itself is
synchronous. Classes can declare several actions in the same configuration.
Common `System` namespaces and `HassSharp` are already imported for scripts.

## 2. Turn on a light when motion starts

Change `binary_sensor.hall_motion` and `light.hall` to IDs from your instance.
The subscription fires on entity changes, so the action explicitly checks for
`on` before turning on the light. `Manual: false` keeps this example out of the
manual action chooser.

```csharp
public class HallMotion : Automation
{
    public override AutomationConfiguration Configure() => new(
        new AutomationAction(nameof(TurnOn), AutomationMode.Single,
            Manual: false, Entities: ["binary_sensor.hall_motion"]));

    public async Task TurnOn()
    {
        if (Entity("binary_sensor.hall_motion").RawState != "on") return;

        await Services.Call("light", "turn_on", new
        {
            entity_id = "light.hall"
        }).RunAsync();
    }
}
```

`Services.Call(domain, service, data)` builds a service call. **Call `.Run()` or
await `.RunAsync()` to execute it.** `RunAsync()` waits for the Home Assistant
service call to complete and reports service errors to the action's run history.

Entity reads require an entity with a current state. Registered entities without
a current state still appear in suggestions, but a read can fail until they are
available. An entity reference is a snapshot; use `.Refresh()` or read it again
after a delay when you need its latest state.

## 3. Turn a light off after motion has been clear for one minute

`Restart` cancels the previous invocation when another state change arrives.
Passing `CancellationToken` to the delay ensures fresh motion cancels the pending
off action. The final check avoids dispatching a service after cancellation.

```csharp
public class HallOffDelay : Automation
{
    public override AutomationConfiguration Configure() => new(
        new AutomationAction(nameof(TurnOffLater), AutomationMode.Restart,
            Manual: false, Entities: ["binary_sensor.hall_motion"]));

    public async Task TurnOffLater()
    {
        if (Entity("binary_sensor.hall_motion").RawState != "off") return;

        await Task.Delay(TimeSpan.FromMinutes(1), CancellationToken);
        CancellationToken.ThrowIfCancellationRequested();
        if (Entity("binary_sensor.hall_motion").RawState != "off") return;

        await Services.Call("light", "turn_off", new
        {
            entity_id = "light.hall"
        }).RunAsync();
    }
}
```

Cancellation is cooperative. It cannot undo a service call already dispatched.
Await work started by an action; avoid unobserved background tasks.

## 4. A daily schedule

This logs a reminder at 07:30 in Zagreb and also offers a manual test action.
Use an explicit IANA time zone, such as `Europe/Zagreb` or `UTC`.

```csharp
public class MorningReminder : Automation
{
    public override AutomationConfiguration Configure() => new(
        new AutomationAction(nameof(Remind), Schedules: [
            AutomationSchedule.Daily("morning", new TimeOnly(7, 30), "Europe/Zagreb")
        ]));

    public Task Remind()
    {
        Logger.Info("Good morning!");
        return Task.CompletedTask;
    }
}
```

Keep schedule IDs stable across edits and unique within each action. Missed starts
are skipped. A daylight-saving gap is skipped; a repeated time runs at its first
instant. For a one-time action, use `AutomationSchedule.Once("reminder", at)` with
a `DateTimeOffset` containing an explicit offset. See [Scheduling](scheduling.md).

## Choose how overlapping runs behave

| Mode | What happens when the action is already running |
| --- | --- |
| `Single` (default) | The new invocation is skipped. |
| `Restart` | The previous invocation is asked to cancel, and a new one starts. |
| `Queued` | Invocations run in arrival order. |
| `Parallel` | Invocations may run at the same time. |

Manual, entity, startup and schedule invocations share the same action policy.
Class fields persist across invocations; protect shared state if runs overlap.

## Entity IDs and editor help

Type `Entities.` to explore generated typed IDs. For example, `light.hall`
becomes `Entities.Light.Hall`; its `.EntityId` property is the exact string ID.
`Entity(Entities.Light.Hall)` reads it with typed helpers. String IDs, as shown
above, remain supported and make the examples easy to adapt.

After adding or renaming entities, an administrator can choose **Automations →
Refresh entity IDs**. Subsequent completions, diagnostics and compilation use the
updated symbols. This does not reload or execute your running automations.

Use **Ctrl+Space** for completions, hover for type information, and **Ctrl+S / ⌘S**
to save. Editor settings include font size and Vim mode. Keys typed in the code
editor stay in the editor instead of opening Home Assistant shortcut dialogs.

If saving fails, inspect editor diagnostics and the error message. For execution
failures, inspect Run history and the Home Assistant logs. `Logger.Info(...)`
and `Logger.Error(...)` messages are forwarded to Home Assistant.

Prefer building with nodes? See [Writing visual automations](writing-visual-automations.md).
