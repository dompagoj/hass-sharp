namespace HassSharp.HelperBaseAutomations;

public class MotionLightAutomation : RunnableClassScript
{
    public EntityRefWrapper<HaBinarySensor> MotionSensor { get; set; } = null!;
    public EntityRefWrapper<HaLight> Light { get; set; } = null!;

    public TimeSpan TurnOffDelay { get; set; } = TimeSpan.FromSeconds(10);

    public LightOnOpts? LightOnOptions { get; set; }
    public LightOffOpts? LightOffOptions { get; set; }

    EntityRef<HaLight> _light;
    EntityRef<HaMotionSensor> _motion;

    public override Task Initialize()
    {
        if (MotionSensor is null || Light is null)
        {
            throw new Exception("MotionSensor or Light not initialized");
        }

        _motion = Entity(MotionSensor).AsMotion();
        _light = EntityUntracked(Light);

        return Task.CompletedTask;
    }

    Task ScheduleTurnOff()
    {
        return Task.Run(async () =>
        {
            await Task.Delay(TurnOffDelay);
            if (_motion.Refresh().IsMotionDetected())
            {
                Logger.Debug("Motion stil on, not turning off light");
                _ = ScheduleTurnOff();
                return;
            }

            Logger.Debug($"Turning off Light {_light.EntityId}");
            if (LightOffOptions is not null) _light.TurnOff(LightOffOptions).Run();
            else _light.TurnOff().Run();
        }, CancellationToken);
    }

    [Mode(AutomationMode.Restart)]
    public override Task Run()
    {
        Logger.Debug($"Motion state: {_motion.RawState}");

        if (_motion.IsMotionDetected())
        {
            Logger.Debug("Motion detected!");
            if (LightOnOptions is not null) _light.TurnOn(LightOnOptions).Run();
            else _light.TurnOn().Run();
            ScheduleTurnOff();
        }

        return Task.CompletedTask;
    }
}
