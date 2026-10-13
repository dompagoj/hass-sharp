# HassSharp

HassSharp is a Home Assistant custom integration for writing automations in C#. It runs .NET inside Home Assistant through Python.NET, compiles your C# scripts with Roslyn, and connects them to Home Assistant entity changes and services.

The **HassSharp** sidebar panel provides a code editor with entity completion, diagnostics, hover information, and formatting. Save an automation to compile and load it without restarting Home Assistant.

The central idea is automatic dependency discovery: **reading an entity with `Entity(...)` subscribes the automation method to changes to that entity.** Conditions and actions are ordinary C#.

## Contents

- [Comparison with NetDaemon](#comparison-with-netdaemon)
- [Getting started](#getting-started)
- [How automations work](#how-automations-work)
- [Entity tracking and state](#entity-tracking-and-state)
- [Initialization and InitGuard](#initialization-and-initguard)
- [Service calls: Run and RunAsync](#service-calls-run-and-runasync)
- [Automation modes and cancellation](#automation-modes-and-cancellation)
- [Examples: basic to advanced](#examples-basic-to-advanced)
- [RunnableClassScript](#runnableclassscript)
- [Built-in MotionLightAutomation](#built-in-motionlightautomation)
- [Practical notes](#practical-notes)
- [Building from source](#building-from-source)

## Comparison with NetDaemon

Both HassSharp and [NetDaemon](https://netdaemon.xyz/) let you write Home Assistant automations in C#. The main difference is where they run: NetDaemon runs in a separate process, deployed as an [add-on, Docker container, or standalone application](https://netdaemon.xyz/docs/user/), and communicates with Home Assistant through its WebSocket API. **HassSharp runs directly inside the Home Assistant process**, embedding .NET through Python.NET.

This integration provides a web editor directly in the Home Assistant sidebar. Write and save your C# scripts there; HassSharp dynamically compiles them with Roslyn at runtime, loads them, and runs them inside Home Assistant. Updating an automation does not require a separate build and deployment step or a Home Assistant restart.

Service calls also stay within the Home Assistant process. For example, `light.TurnOn().Run()` calls Home Assistant's service registry through the Python.NET bridge, using the same internal service API that YAML automations and Python integrations use. Avoiding a WebSocket round trip removes networking overhead between the automation and Home Assistant and should reduce service-dispatch latency. The size of any performance improvement requires benchmarking; Python.NET interop and service-data serialization still have a cost, and device or integration latency still affects how quickly an action completes.

## Getting started

Install the integration through HACS using this repository as a custom integration repository, or extract the release's `hass_sharp.zip` into your Home Assistant configuration directory's `custom_components/hass_sharp` folder. Release packages include the compiled .NET library and frontend.

Restart Home Assistant, add **HassSharp** under **Settings → Devices & services → Add integration**, and open **HassSharp** in the sidebar. First startup downloads the .NET SDK into the integration directory, so it needs internet access and may take longer than subsequent starts. The project currently targets .NET 10; `hacs.json` declares Home Assistant 2025.12.3 as the minimum version.

Select **New Automation**, choose a name, and paste an example below. Replace its sample entities with entities from your installation, then save. Scripts are stored as `.cs` files under:

```text
<Home Assistant configuration>/custom_components/hass_sharp/user_scripts/
```

You can also place scripts there directly and reload the integration to load them. Each file is compiled separately; keep shared types in the same file as the automations that use them.

## How automations work

A basic automation is a public, concrete class deriving from `Automation`, with a parameterless constructor (an implicit constructor is fine). Its public methods declared on that class are automation entry points. Use parameterless methods returning `void`, `Task`, or `ValueTask`; prefer `Task` for asynchronous work. Keep ordinary helper methods private so they are not discovered as separate automations.

For an `Automation` class:

1. HassSharp creates an instance and invokes its automation methods during initialization.
2. Calls to `Entity(...)` register dependencies for the calling method.
3. When a tracked entity changes, HassSharp invokes that method again with the triggering state available.
4. The method's mode decides how a new invocation interacts with an invocation already running.

One class can contain multiple independent automation methods. A method's name is your choice; it does not need to be `Run`.

The editor supplies imports for `System`, `System.Threading`, `System.Threading.Tasks`, `System.Collections.Generic`, `System.Linq`, and `HassSharp`. Examples can be pasted without adding those `using` statements. Additional namespaces, such as `System.Globalization`, need an import or a fully qualified name.

## Entity tracking and state

### Entity tracks; EntityUntracked does not

```csharp
var light = Entity(Entities.Light.LivingRoom);
```

This reads `light.living_room` and **tracks it**. Whenever its state changes, the calling automation method is eligible to run again, subject to its automation mode. Home Assistant state-change events can also contain attribute changes, so a rerun does not necessarily mean the state string changed.

```csharp
var light = EntityUntracked(Entities.Light.LivingRoom);
```

This reads the entity **without registering a dependency**. A later change to this light does not, by itself, rerun this method. Use tracked reads for inputs that should trigger an automation, and untracked reads for action targets or conditions that should only be checked when another input triggers it.

Prefer identifiers from the generated static `Entities` class over raw entity ID strings for editor completion and compile-time checking.

`Entities.Light.LivingRoom` itself is a generated typed identifier. Merely accessing that identifier does not read or track the entity. Generated identifiers use PascalCase: for example, `binary_sensor.hallway_motion` becomes `Entities.BinarySensor.HallwayMotion`.

Both methods also accept string IDs. String reads return an unknown entity type; use `.As<HaLight>()`, `.As<HaInputNumber>()`, and similar conversions for domain-specific helpers. Conversion does not change whether the original read was tracked.

### Tracking is discovered by executing code

Only `Entity(...)` calls that are actually reached register dependencies. Read all intended trigger entities before early returns, conditions, or `InitGuard()`. Dependencies discovered later in a run are also registered, and stay registered until the script is unloaded or replaced; returning from a different branch does not unsubscribe them.

Tracking normally uses the calling method's name. When a private helper performs tracked reads, pass the entry point's name explicitly with `caller: nameof(MyAutomationMethod)`; see example 20.

### Entity references and Refresh

An `EntityRef<T>` contains a state snapshot, with properties such as `EntityId`, `RawState`, `Domain`, `LastChanged`, and `LastReported`. Typed helpers include light `IsOn()`, motion `IsMotionDetected()`, and media player `VolumeLevel`. `GetAttribute<T>("name")` reads an attribute; the requested type must match its stored value, and a missing attribute returns the type's default value.

References can use the current invocation's triggering state for the triggering entity, including references stored during initialization. They are not continuous live queries for every entity. After a delay or service call, use **`Refresh()`** to fetch the latest state Home Assistant currently has:

```csharp
player.Refresh();
var volume = player.VolumeLevel;
```

`Refresh()` updates the reference and returns it, so chaining such as `motion.Refresh().IsClear()` works. It does not register tracking, trigger a device poll, or execute a service. If Home Assistant no longer has the entity, it keeps the previous snapshot. To check the result of a service call, await `RunAsync()`, then refresh the reference before reading its state.

`EntityRef<T>` is a struct: assigning it to another variable copies it. Refresh a local variable, or assign the returned reference back when working with a property or collection entry.

`TriggeredBy()` returns an unknown typed reference to the entity responsible for the current invocation, or `null` when there is no entity trigger, including initialization. It helps distinguish several inputs to the same method.

## Initialization and InitGuard

For classes derived directly from `Automation`, dependency discovery executes the same methods that later perform actions. You often want the tracked reads to execute during initialization, but want to skip device actions, notifications, delays, and other work.

**`InitGuard();` stops the current method during initialization.** HassSharp catches its internal initialization exception as an expected exit. During normal execution, the guard does nothing and the rest of the method continues.

```csharp
public class ButtonLightAutomation : Automation
{
    public void ToggleLight()
    {
        var button = Entity(Entities.InputButton.ToggleLight);
        var light = EntityUntracked(Entities.Light.Desk);

        InitGuard(); // Discover dependencies above; skip the action below on load.

        light.Toggle().Run();
    }
}
```

On initialization, the button is registered and the light is read, but nothing is toggled. Pressing the button later runs the entire method, and the guard allows the toggle. The untracked light does not cause a second invocation when its state changes.

Place the guard **after the reads that discover dependencies and before the work you want to skip**. Putting it first prevents discovery. Omitting it allows actions during initialization, which may be intentional for an automation that should reconcile device state immediately on load.

The `Initializing` property lets you branch explicitly when needed. `InitGuard()` is usually simpler when the remaining method should be skipped. Avoid catching its exception in a broad `catch (Exception)` around the guard.

For an explicit separation of setup and execution, use [`RunnableClassScript`](#runnableclassscript). Its `Initialize()` discovers initial dependencies, and its `Run()` performs the actions. `Run()` can still add tracked dependencies.

## Service calls: Run and RunAsync

Most service helpers return a **description of a service call**, not an executed action:

```csharp
light.TurnOn();                  // Builds a call; nothing is sent.
light.TurnOn().Run();            // Dispatches the service without waiting.
await light.TurnOn().RunAsync(); // Dispatches and awaits service completion.
```

**You must call `Run()` or `RunAsync()` on a returned `ServiceCall`.** This applies to entity methods such as `TurnOn`, `SetValue`, `Activate`, and `Speak`, and to methods on `HassServices`.

| API                               | Behavior                                                                                           | Use when                                                          |
| --------------------------------- | -------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------- |
| `ServiceCall.Run()`               | Dispatches the call and immediately returns `void`.                                                | You do not need to wait for the service to complete.              |
| `ServiceCall.RunAsync()`          | Returns a `Task` that completes when the Home Assistant service finishes; failures fault the task. | You want to await completion, check the result, or handle errors. |
| `ServiceCall<TResult>.RunAsync()` | Executes a response-producing service and returns `Task<TResult?>`.                                | You need response data, such as weather forecasts.                |

**Use `await ...RunAsync()` when you want to wait for the service to complete.** For example, await turning a light on before refreshing its reference and checking `IsOn()`. Calling `RunAsync()` without awaiting its task does not make your method wait.

**Use `Run()` when you do not need to wait.** It returns immediately, so the automation can continue sooner instead of waiting for service completion. This is the faster path for the caller when no subsequent work depends on the result. `Run()` provides no completion or error task to your method.

There are three common ways to call services:

- Entity helpers: `light.TurnOn().Run()`.
- Domain helpers: `HassServices.Light.TurnOn("light.desk").Run()`.
- Arbitrary services: `Services.Call("domain", "service", new { ... }).Run()`.

Domain service helpers take string entity IDs, so pass `.EntityId` from a generated identifier or an entity reference. Calling a service does not itself subscribe the automation to its target.

One convenience exception is **`await todo.GetItems()`**: that entity helper already executes and awaits the underlying call, and returns the items directly. In contrast, `HassServices.Todo.GetItems(...)` returns a service description and requires `.RunAsync()`.

The names `RunnableClassScript.Run()`, a script entity's `Run()`, and `ServiceCall.Run()` have different roles. HassSharp invokes the first as an automation entry point. A script entity's `Run()` constructs a call, so executing a Home Assistant script requires `script.Run().Run()` or `await script.Run().RunAsync()`.

## Automation modes and cancellation

Apply `[Mode(AutomationMode.X)]` to an automation method, or to a `RunnableClassScript.Run()` override. These modes mirror the run policies in [Home Assistant automation modes](https://www.home-assistant.io/docs/automation/modes/):

| HassSharp mode | Home Assistant mode | When another trigger arrives during a run                    |
| -------------- | ------------------- | ------------------------------------------------------------ |
| `Single`       | `single`            | Skip the new run. This is the default.                       |
| `Restart`      | `restart`           | Request cancellation of the current run and start a new one. |
| `Queued`       | `queued`            | Wait to execute until the current run releases the queue.    |
| `Parallel`     | `parallel`          | Start another run alongside existing runs.                   |

These are run policies; HassSharp conditions remain C# code inside your method. HassSharp's mode attribute currently has no `max` or `max_exceeded` settings. Queued execution serializes runs, but the current semaphore implementation does not promise strict trigger ordering.

Cancellation is cooperative. Use the inherited **`CancellationToken`** with delays and APIs that accept it, and call `CancellationToken.ThrowIfCancellationRequested()` between actions when needed:

```csharp
await Task.Delay(TimeSpan.FromSeconds(30), CancellationToken);
CancellationToken.ThrowIfCancellationRequested();
```

Without cancellation checks, a restarted invocation may continue executing alongside its replacement. Service calls do not accept this token and cannot retract an action already dispatched. Script replacement, deletion, and unloading also request cancellation of active runs.

Await the work belonging to an invocation. A detached task is no longer counted as an active method run once the method returns. Parallel runs share the same class instance; keep per-run state in local variables or synchronize access to fields.

## Examples: basic to advanced

Each numbered example is a complete script. Entity names are illustrative; replace them with your installation's generated identifiers or string IDs. The examples generally use `InitGuard()` so loading or saving a script discovers dependencies without performing actions.

### 1. Log whenever a light changes

The light is the tracked input. Changing its state or attributes reruns `LogLight`.

```csharp
public class LightLogger : Automation
{
    public void LogLight()
    {
        var light = Entity(Entities.Light.LivingRoom);
        InitGuard();

        Logger.Info($"{light.EntityId}: {light.RawState}");
    }
}
```

### 2. Make a light follow an input boolean

Only the helper triggers this method. Manually changing the light does not rerun it because the light is untracked. The next helper change applies the desired state again.

```csharp
public class DeskLightFollower : Automation
{
    public async Task FollowHelper()
    {
        var enabled = Entity(Entities.InputBoolean.DeskLightEnabled);
        var light = EntityUntracked(Entities.Light.Desk);
        InitGuard();

        if (enabled.IsOn())
            await light.TurnOn().RunAsync();
        else if (enabled.IsOff())
            await light.TurnOff().RunAsync();
    }
}
```

Remove the guard if you also want the light synchronized when the script initializes.

### 3. Toggle a light with an input button

An input button updates its state when pressed. Reading it with `Entity` supplies the trigger even though the returned value is not otherwise used. The guard prevents a toggle when the script loads.

```csharp
public class DeskButton : Automation
{
    public void OnPress()
    {
        var button = Entity(Entities.InputButton.ToggleLight);
        var light = EntityUntracked(Entities.Light.Desk);
        InitGuard();

        light.Toggle().Run();
    }
}
```

This reacts to state-change events for the button, including any attribute updates it emits. For event entities such as a Shelly button, inspect their event attributes as in example 7.

### 4. Use string IDs and typed conversions

String IDs work without generated identifiers. `.As<HaLight>()` supplies light helpers while preserving the untracked read.

```csharp
public class StringIdAutomation : Automation
{
    public void OnPress()
    {
        var button = Entity("input_button.toggle_light");
        var light = EntityUntracked("light.desk").As<HaLight>();
        InitGuard();

        light.Toggle().Run();
    }
}
```

### 5. Set brightness, color temperature, and transitions

Light option objects describe service data. The entity helper fills in the target entity ID. The device must support the requested options.

```csharp
public class ReadingLight : Automation
{
    public async Task OnPress()
    {
        var button = Entity(Entities.InputButton.ReadingMode);
        var light = EntityUntracked(Entities.Light.Desk);
        InitGuard();

        await light.TurnOn(new LightOnOpts
        {
            BrightnessPct = 65,
            ColorTempKelvin = 3000,
            Transition = 2,
        }).RunAsync();
    }
}
```

Use `light.TurnOff(new LightOffOpts { Transition = 3 }).Run()` for a fade out. Options such as `RgbColor = [255, 120, 40]` are available for color-capable lights.

### 6. Call a domain service for several entities

You can target entities without reading them. None of these service targets becomes a tracked dependency.

```csharp
public class AllLightsOff : Automation
{
    public async Task OnPress()
    {
        var button = Entity(Entities.InputButton.AllLightsOff);
        InitGuard();

        await HassServices.Light.TurnOff(new[]
        {
            Entities.Light.Desk.EntityId,
            Entities.Light.Hallway.EntityId,
            Entities.Light.Kitchen.EntityId,
        }).RunAsync();
    }
}
```

### 7. Inspect attributes and Shelly button events

`ShellyButton` interprets the `event_type` attribute. Use it with an event entity that reports the supported Shelly event names, such as `press`, `double_press`, and `long_press`.

```csharp
public class ShellyDeskButton : Automation
{
    public void OnButtonEvent()
    {
        var button = Entity("event.desk_button").As<ShellyButton>();
        var light = EntityUntracked(Entities.Light.Desk);
        InitGuard();

        Logger.Debug($"Button event: {button.GetAttribute<string>("event_type")}");

        if (button.IsSinglePress())
            light.Toggle().Run();
        else if (button.IsDoublePress())
            light.TurnOn(new LightOnOpts { BrightnessPct = 100 }).Run();
        else if (button.IsLongPress())
            light.TurnOff().Run();
    }
}
```

### 8. Combine motion with an untracked daylight condition

Motion is the trigger. The sun is checked each time motion changes, but a sunrise or sunset does not invoke this method. Use `Entity(Entities.Sun.SunEntity)` instead if changes to the sun should also trigger it.

```csharp
public class NightMotionLight : Automation
{
    public async Task OnMotion()
    {
        var motion = Entity(Entities.BinarySensor.HallwayMotion).AsMotion();
        var sun = EntityUntracked(Entities.Sun.SunEntity);
        var light = EntityUntracked(Entities.Light.Hallway);
        InitGuard();

        if (motion.IsMotionDetected() && sun.IsBelowHorizon())
            await light.TurnOn(new LightOnOpts { BrightnessPct = 30 }).RunAsync();
    }
}
```

The generated name for `sun.sun` is `Entities.Sun.SunEntity`: the generator appends `Entity` when the property would otherwise have the same name as its containing domain class.

### 9. Track both a sensor and an adjustable threshold

Both inputs are read before the guard, so either can trigger reevaluation. Parse raw sensor state defensively because Home Assistant may report `unknown` or `unavailable`.

```csharp
public class TemperatureFan : Automation
{
    public async Task ControlFan()
    {
        var temperature = Entity(Entities.Sensor.OfficeTemperature);
        var threshold = Entity(Entities.InputNumber.FanThreshold);
        var fan = EntityUntracked(Entities.Switch.OfficeFan);
        InitGuard();

        if (!double.TryParse(temperature.RawState, out var degrees))
            return;

        if (!double.TryParse(threshold.RawState, out var limit))
            return;

        if (degrees >= limit)
            await fan.TurnOn().RunAsync();
        else
            await fan.TurnOff().RunAsync();
    }
}
```

Typed numeric helpers such as an input number's `Value` are convenient when a numeric state is guaranteed; they parse the state and can throw for nonnumeric values.

### 10. Determine which input triggered the method

Both buttons register dependencies. `TriggeredBy()` distinguishes which one caused this invocation without checking the buttons' timestamp values.

```csharp
public class TwoButtonLight : Automation
{
    public async Task OnButton()
    {
        var onButton = Entity(Entities.InputButton.LightOn);
        var offButton = Entity(Entities.InputButton.LightOff);
        var light = EntityUntracked(Entities.Light.Desk);
        InitGuard();

        var triggerId = TriggeredBy()?.EntityId;
        if (triggerId == onButton.EntityId)
            await light.TurnOn().RunAsync();
        else if (triggerId == offButton.EntityId)
            await light.TurnOff().RunAsync();
    }
}
```

### 11. Single mode: ignore triggers during a cooldown

The method stays active through the delay. Presses during the cooldown are skipped. `Single` is also the mode used when no attribute is supplied.

```csharp
public class NotificationCooldown : Automation
{
    [Mode(AutomationMode.Single)]
    public async Task OnPress()
    {
        var button = Entity(Entities.InputButton.RemindMe);
        InitGuard();

        await HassServices.PersistentNotification.Create(
            "Time for a break!", title: "Reminder").RunAsync();

        await Task.Delay(TimeSpan.FromMinutes(1), CancellationToken);
    }
}
```

### 12. Restart mode: turn off after motion clears

Motion turns the light on immediately. When motion clears, a 30-second countdown starts. New motion cancels that countdown. The delay uses the automation's token, and the final refresh checks the latest sensor state.

```csharp
public class HallwayMotionLight : Automation
{
    [Mode(AutomationMode.Restart)]
    public async Task OnMotion()
    {
        var motion = Entity(Entities.BinarySensor.HallwayMotion).AsMotion();
        var light = EntityUntracked(Entities.Light.Hallway);
        InitGuard();

        if (motion.IsMotionDetected())
        {
            await light.TurnOn().RunAsync();
            return;
        }

        if (!motion.IsClear()) return;

        await Task.Delay(TimeSpan.FromSeconds(30), CancellationToken);
        CancellationToken.ThrowIfCancellationRequested();

        if (motion.Refresh().IsClear())
            await light.TurnOff().RunAsync();
    }
}
```

### 13. Queued mode: serialize a sequence

Each button invocation waits for exclusive execution of the sequence. Awaiting the service calls and the delay keeps the queue occupied until the sequence finishes.

```csharp
public class QueuedLightPulse : Automation
{
    [Mode(AutomationMode.Queued)]
    public async Task OnPress()
    {
        var button = Entity(Entities.InputButton.PulseLight);
        var light = EntityUntracked(Entities.Light.Desk);
        InitGuard();

        await light.TurnOn().RunAsync();
        await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken);
        CancellationToken.ThrowIfCancellationRequested();
        await light.TurnOff().RunAsync();
    }
}
```

### 14. Parallel mode: keep invocations independent

Each invocation captures its own button timestamp and continues independently. All per-invocation values are local; there is no shared mutable field.

```csharp
public class ParallelReminders : Automation
{
    [Mode(AutomationMode.Parallel)]
    public async Task OnPress()
    {
        var button = Entity(Entities.InputButton.RemindMe);
        InitGuard();

        var pressedAt = button.RawState;
        await Task.Delay(TimeSpan.FromSeconds(10), CancellationToken);
        CancellationToken.ThrowIfCancellationRequested();

        await HassServices.PersistentNotification.Create(
            $"Reminder for the press at {pressedAt}").RunAsync();
    }
}
```

### 15. Refresh a media player after turning it on

An untracked reference read before the service still contains its earlier snapshot. Refresh it before reading state or attributes that may have changed.

```csharp
public class SpeakerStatus : Automation
{
    public async Task OnPress()
    {
        var button = Entity(Entities.InputButton.CheckSpeaker);
        var player = EntityUntracked(Entities.MediaPlayer.LivingRoom);
        InitGuard();

        await player.TurnOn().RunAsync();
        CancellationToken.ThrowIfCancellationRequested();

        player.Refresh();
        Logger.Info($"Speaker state: {player.RawState}, volume: {player.VolumeLevel}");
    }
}
```

Awaiting the service and refreshing the reference are separate steps: `RunAsync()` waits for service completion, while `Refresh()` updates the snapshot used by the state and attribute helpers.

### 16. Await a service and check its result

Await the turn-on service, then refresh the light and check its state. There is no polling loop: `RunAsync()` already waits for the service to complete. If the refreshed light is not on, log that the expected result was not observed. The light remains untracked, so its updates do not restart the method.

```csharp
public class CheckDeskLight : Automation
{
    [Mode(AutomationMode.Restart)]
    public async Task OnPress()
    {
        var button = Entity(Entities.InputButton.ReadingMode);
        var light = EntityUntracked(Entities.Light.Desk);
        InitGuard();

        await light.TurnOn().RunAsync();

        CancellationToken.ThrowIfCancellationRequested();

        if (light.Refresh().IsOn())
            Logger.Info("Desk light is on.");
        else
            Logger.Warn("Turn-on service completed, but the desk light is not on.");
    }
}
```

If you only want to send the turn-on command and continue immediately, use `light.TurnOn().Run()` instead. An immediate state check after `Run()` can happen before the service completes.

### 17. Activate a scene and start a Home Assistant script

A script entity's `Run()` returns a `ServiceCall`. The final `.RunAsync()` executes it. This dispatches `script.turn_on`; it does not wait for every action inside the Home Assistant script to finish.

```csharp
public class MovieNight : Automation
{
    public async Task OnPress()
    {
        var button = Entity(Entities.InputButton.MovieNight);
        var scene = EntityUntracked(Entities.Scene.MovieNight);
        var script = EntityUntracked(Entities.Script.StartMovie);
        InitGuard();

        await scene.Activate(transition: 2).RunAsync();
        CancellationToken.ThrowIfCancellationRequested();
        await script.Run().RunAsync();
    }
}
```

### 18. Call an arbitrary service and handle errors

`Services.Call` covers services without a dedicated helper. Supply the Home Assistant domain, service name, and service data. Data is serialized with snake_case property names and null values omitted.

```csharp
public class PhoneNotification : Automation
{
    public async Task OnPress()
    {
        var button = Entity(Entities.InputButton.NotifyPhone);
        InitGuard();

        try
        {
            await Services.Call("notify", "mobile_app_my_phone", new
            {
                title = "HassSharp",
                message = "Hello from C#!",
                data = new { tag = "hass-sharp-example" },
            }).RunAsync();
        }
        catch (InvalidOperationException error)
        {
            Logger.Error($"Notification failed: {error.Message}");
        }
    }
}
```

Replace `mobile_app_my_phone` with an installed notification service. Awaiting the call allows its failure to reach this method.

### 19. Read typed service responses and update a to-do list

The entity convenience method `GetItems()` returns a `Task<TodoItemResult[]>` and already executes the underlying service. Updating an item returns a service description, so it still needs `.RunAsync()`.

```csharp
public class CompleteShoppingList : Automation
{
    [Mode(AutomationMode.Single)]
    public async Task OnPress()
    {
        var button = Entity(Entities.InputButton.CompleteShopping);
        var todos = EntityUntracked(Entities.Todo.ShoppingList);
        InitGuard();

        var items = await todos.GetItems(TodoItemStatus.NeedsAction);
        foreach (var item in items)
        {
            CancellationToken.ThrowIfCancellationRequested();
            if (item.Uid is null) continue;

            await HassServices.Todo.UpdateItem(
                todos.EntityId, item.Uid, status: TodoItemStatus.Completed).RunAsync();
        }

        Logger.Info($"Processed {items.Length} shopping list items.");
    }
}
```

Using an untracked to-do list avoids rerunning the automation because its own updates changed the list. The lower-level `await HassServices.Todo.GetItems(todos.EntityId).RunAsync()` returns a response dictionary keyed by entity ID.

### 20. Use private helpers without losing the tracking owner

`Entity` uses the caller's method name by default. A helper must forward the automation entry point's name so that changes rerun the entry point, rather than trying to run the private helper.

```csharp
public class MotionWithHelper : Automation
{
    public async Task OnMotion()
    {
        var motion = ReadMotion(nameof(OnMotion));
        var light = EntityUntracked(Entities.Light.Hallway);
        InitGuard();

        if (motion.IsMotionDetected())
            await light.TurnOn().RunAsync();
    }

    private EntityRef<HaMotionSensor> ReadMotion(string owner)
    {
        return Entity(Entities.BinarySensor.HallwayMotion, caller: owner).AsMotion();
    }
}
```

### 21. Query a weather forecast response

Response-producing service descriptions expose `RunAsync()` and return typed data. The weather response is keyed by the requested entity ID. The weather integration must support the requested forecast type.

```csharp
public class WeatherSummary : Automation
{
    public async Task OnPress()
    {
        var button = Entity(Entities.InputButton.CheckWeather);
        var weather = EntityUntracked(Entities.Weather.Home);
        InitGuard();

        var response = await weather.GetForecasts(WeatherForecastType.Daily).RunAsync();
        if (response is null || !response.TryGetValue(weather.EntityId, out var result))
            return;

        foreach (var forecast in result.Forecast.Take(3))
            Logger.Info($"{forecast.Datetime}: {forecast.Condition}, {forecast.Temperature}");
    }
}
```

### 22. Run independent service calls concurrently

Use `Task.WhenAll` when neither action depends on the other. Each builder is executed with `RunAsync()` before its task is passed to `WhenAll`; awaiting the combined task keeps the automation active until both services finish.

```csharp
public class EveningDevices : Automation
{
    public async Task OnPress()
    {
        var button = Entity(Entities.InputButton.EveningMode);
        InitGuard();

        await Task.WhenAll(
            HassServices.Light.TurnOn("light.desk").RunAsync(),
            HassServices.Switch.TurnOff("switch.office_fan").RunAsync());
    }
}
```

## RunnableClassScript

`RunnableClassScript` derives from `Automation` and separates the two mechanisms that a plain automation combines:

- **`Initialize()`** runs on load to set up references and discover initial dependencies. It returns `Task` and has a default implementation that does nothing.
- **`Run()`** is the single automation entry point. It is an abstract method returning `Task`, and is invoked when a tracked entity changes.

HassSharp calls `Initialize()` during initialization **instead of calling `Run()`**. You usually do not need `InitGuard()` inside `Run()` because it is not used for the initial discovery pass. Keep actions out of `Initialize()` when you only want discovery and setup there.

### 23. Separate dependency discovery from actions

```csharp
public class RunnableDeskButton : RunnableClassScript
{
    private EntityRef<HaLight> _light;

    public override Task Initialize()
    {
        // Register the trigger without toggling the light.
        var button = Entity(Entities.InputButton.ToggleLight);
        _light = EntityUntracked(Entities.Light.Desk);
        return Task.CompletedTask;
    }

    [Mode(AutomationMode.Single)]
    public override async Task Run()
    {
        await _light.Toggle().RunAsync();
    }
}
```

Tracked reads in `Initialize()` are routed to `Run()` when their entities change. The light reference can be stored for later service calls; use `_light.Refresh()` before making decisions based on its latest state.

### 24. Add tracked dependencies inside Run

**Tracking still works in `Run()`.** This example discovers a button during initialization and reads a tracked brightness helper each time `Run()` executes. After the first button invocation, changes to that helper also rerun the automation.

```csharp
public class RunnableBrightness : RunnableClassScript
{
    public override Task Initialize()
    {
        var button = Entity(Entities.InputButton.ApplyBrightness);
        return Task.CompletedTask;
    }

    [Mode(AutomationMode.Restart)]
    public override async Task Run()
    {
        // This also registers a dependency, once Run reaches it.
        var brightness = Entity(Entities.InputNumber.DeskBrightness);
        var light = EntityUntracked(Entities.Light.Desk);

        if (!double.TryParse(brightness.RawState, out var percent))
            return;

        await light.TurnOn(new LightOnOpts
        {
            BrightnessPct = (int)Math.Clamp(percent, 0, 100),
        }).RunAsync();
    }
}
```

If the brightness helper must trigger `Run()` immediately after loading, also read it with `Entity(...)` in `Initialize()`. A dependency read only inside `Run()` cannot trigger the first invocation because it has not been discovered yet. At least one initial dependency is needed to start an entity-driven runnable script.

## Built-in MotionLightAutomation

[`MotionLightAutomation`](custom_components/hass_sharp/HassSharp/HelperBaseAutomations/MotionLightAutomation.cs), in `HassSharp.HelperBaseAutomations`, is a reusable base class derived from `RunnableClassScript`. Configure it in your subclass's constructor:

### 25. Configure the motion light helper

```csharp
using HassSharp.HelperBaseAutomations;

public class HallwayLightHelper : MotionLightAutomation
{
    public HallwayLightHelper()
    {
        MotionSensor = Entities.BinarySensor.HallwayMotion;
        Light = Entities.Light.Hallway;
        TurnOffDelay = TimeSpan.FromSeconds(30);

        LightOnOptions = new LightOnOpts
        {
            BrightnessPct = 60,
            ColorTempKelvin = 3000,
        };

        LightOffOptions = new LightOffOpts
        {
            Transition = 2,
        };
    }
}
```

| Property          | Purpose                                                   | Default                 |
| ----------------- | --------------------------------------------------------- | ----------------------- |
| `MotionSensor`    | Required binary sensor identifier, interpreted as motion. | Must be supplied.       |
| `Light`           | Required light identifier.                                | Must be supplied.       |
| `TurnOffDelay`    | Interval before checking whether motion has cleared.      | 10 seconds.             |
| `LightOnOptions`  | Optional brightness, color, and transition settings.      | Standard turn-on call.  |
| `LightOffOptions` | Optional turn-off settings.                               | Standard turn-off call. |

The helper's `Initialize()` tracks the motion sensor and reads the light untracked. Its `Run()` uses `Restart` mode: when motion is detected it dispatches a light turn-on and schedules a delayed check. If motion is still detected at that check, it schedules another check; when motion is clear, it dispatches turn-off. The delay is a repeated check interval, rather than a guaranteed duration measured from the moment motion cleared.

The current helper launches its delayed checks as detached tasks and returns from `Run()` immediately. Those checks are therefore outside the active invocation tracked by the mode runner; `Restart` does not reliably cancel an earlier scheduled check after `Run()` has finished. For a countdown that resets when motion returns, with the complete delay managed as part of the invocation, use example 12.

## Practical notes

- **Avoid feedback loops.** Tracking an entity that your method changes can cause another invocation. Use `EntityUntracked` for action targets unless their changes are intentional triggers.
- **Read dependencies before branching.** A tracked read hidden behind a false condition or after `InitGuard()` is not discovered on that pass.
- **Use actual entity IDs.** Reads throw if an entity is missing. Generated identifiers come from the entities known to Home Assistant; names in this README are examples.
- **Handle unavailable values.** Typed helpers do not make a missing or `unavailable` sensor value numeric. Validate `RawState` before parsing when necessary.
- **Refresh after waiting.** A reference to another entity can be stale, and a service call does not update the stored reference automatically.
- **Await asynchronous work.** Prefer `Task` entry points over `async void`, and await actions and delays that belong to a run so modes can manage its lifetime.
- **Keep discovery finite.** Initialization needs to complete. Put long delays and loops after `InitGuard()`, or inside `RunnableClassScript.Run()`.
- **Use logs.** `Logger.Debug`, `Info`, `Warn`, and `Error` write through Home Assistant's integration logger. Awaited failures that escape your method are logged by the automation runner.
- **State history helpers need care.** `Previous`, `StateChanged()`, and `AttributeChanged(...)` exist, but the current reference implementation can substitute the current trigger state when reading the previous reference. Do not depend on them for reliable transition detection until that behavior is addressed; the examples use current state and trigger identity instead.

Source references: [automation API](custom_components/hass_sharp/HassSharp/UserScripts/Automation.cs), [initialization guard](custom_components/hass_sharp/HassSharp/UserScripts/UserScriptBase.cs), [runnable scripts](custom_components/hass_sharp/HassSharp/UserScripts/RunnableClassScript.cs), [entity helpers](custom_components/hass_sharp/HassSharp/EntityRef/EntityRefs.cs), and [service helpers](custom_components/hass_sharp/HassSharp/EntityRef/Service.cs).

## Building from source

The repository contains a Python custom integration, a .NET 10 library, and a SolidJS/Monaco frontend. Building a source checkout requires the .NET 10 SDK and Bun. From the repository root:

```sh
dotnet publish -c Release ./custom_components/hass_sharp/HassSharp -o ./custom_components/hass_sharp/out
cd custom_components/hass_sharp/frontend
bun install
bun run build
```

The library is published to `out/` and the frontend builds into `www/`, both inside the integration directory. Deploy the complete `custom_components/hass_sharp` directory to Home Assistant and restart it to load a changed integration. Saving an individual automation in the editor compiles and replaces that script without a Home Assistant restart.
