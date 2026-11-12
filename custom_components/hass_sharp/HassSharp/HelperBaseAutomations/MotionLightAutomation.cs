namespace HassSharp.HelperBaseAutomations;

public abstract class MotionLightAutomation : Automation
{
    public EntityRefWrapper<HaBinarySensor> MotionSensor { get; set; } = null!;
    public EntityRefWrapper<HaLight> Light { get; set; } = null!;

    public TimeSpan TurnOffDelay { get; set; } = TimeSpan.FromSeconds(10);

    public LightOnOpts? LightOnOptions { get; set; }
    public LightOffOpts? LightOffOptions { get; set; }

    public Func<Task<bool>>? ShouldTurnOffCallback { get; set; }

    EntityRef<HaLight> _light;
    EntityRef<HaMotionSensor> _motion;

    public override AutomationConfiguration Configure() => new(
        new AutomationAction(nameof(Run), AutomationMode.Restart, Entities: [MotionSensor.EntityId]));

    public async Task Run()
    {
        _motion = Entity(MotionSensor).AsMotion();
        _light = Entity(Light);
        if (_motion.IsMotionDetected())
        {
            if (LightOnOptions is not null) _light.TurnOn(LightOnOptions).Run();
            else _light.TurnOn().Run();
        }
        do { await Task.Delay(TurnOffDelay, CancellationToken); }
        while (_motion.Refresh().IsMotionDetected());
        if (ShouldTurnOffCallback is { } cb && !await cb()) return;
        CancellationToken.ThrowIfCancellationRequested();
        if (LightOffOptions is not null) _light.TurnOff(LightOffOptions).Run();
        else _light.TurnOff().Run();
    }
}
