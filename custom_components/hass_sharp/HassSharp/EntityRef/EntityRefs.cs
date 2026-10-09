using System.Diagnostics.Contracts;

namespace HassSharp;

public struct EntityRef<T>
{
    HasEntityState _raw;

    internal HasEntityState Raw
    {
        get
        {
            var trigger = UserScriptManager.CurrentTrigger.Value;
            if (trigger is not null &&
                trigger.State.EntityId == _raw.EntityId &&
                trigger.State.LastReported >
                _raw.LastReported) // This last check is important, otherwise this trigger assignment would overwrite fresh state fetched using Refresh();
            {
                _raw = trigger.State;
            }

            return _raw;
        }
        private set => _raw = value;
    }

    public string EntityId => Raw.EntityId;

    public string RawState => Raw.State;

    public double LastChanged => Raw.LastChanged;
    public double LastReported => Raw.LastReported;
    public string ObjectId => Raw.ObjectId;
    public string Domain => Raw.Domain;

    internal EntityRef(HasEntityState raw) => _raw = raw;

    public TValue? GetAttribute<TValue>(string key)
    {
        var found = Raw.Attributes.TryGetValue(key, out var value);
        if (!found) return default;

        return (TValue?)value;
    }

    public EntityRef<T>? Previous
    {
        get
        {
            if (Raw.OldState == null) return null;
            return new(Raw.OldState);
        }
    }

    public bool StateChanged()
    {
        if (Previous == null) return false;
        return Raw.State != Previous.Value.Raw.State;
    }

    public bool AttributeChanged(string key)
    {
        if (Previous == null) return true;

        var hasNew = Raw.Attributes.TryGetValue(key, out var newValue);
        var hasOld = Previous.Value.Raw.Attributes.TryGetValue(key, out var oldValue);

        if (hasNew != hasOld) return true;
        if (!hasNew) return false;

        return newValue != oldValue;
    }

    public EntityRef<T> Refresh()
    {
        Raw = PyInterop.Entity(EntityId) ?? Raw;
        return this;
    }
}

public class EntityRefWrapper<T>
    where T : EntityRefWrapper<T>
{
    public string EntityId { get; init; }

    public EntityRefWrapper(string entityId) => EntityId = entityId;
};

public class HaSwitch(string entityId) : EntityRefWrapper<HaSwitch>(entityId);

public sealed class HaLight(string entityId) : EntityRefWrapper<HaLight>(entityId);

public sealed class HaInputNumber(string entityId) : EntityRefWrapper<HaInputNumber>(entityId);

public sealed class HaSun(string entityId) : EntityRefWrapper<HaSun>(entityId);

public sealed class HaBinarySensor(string entityId) : EntityRefWrapper<HaBinarySensor>(entityId);

public sealed class HaSensor(string entityId) : EntityRefWrapper<HaSensor>(entityId);

public sealed class HaInputButton(string entityId) : EntityRefWrapper<HaInputButton>(entityId);

public sealed class HaMediaPlayer(string entityId) : EntityRefWrapper<HaMediaPlayer>(entityId);

public sealed class HaAiTask(string entityId) : EntityRefWrapper<HaAiTask>(entityId);

public sealed class HaAirQuality(string entityId) : EntityRefWrapper<HaAirQuality>(entityId);

public sealed class HaAlarmControlPanel(string entityId) : EntityRefWrapper<HaAlarmControlPanel>(entityId);

public sealed class HaAlert(string entityId) : EntityRefWrapper<HaAlert>(entityId);

public sealed class HaAssistSatellite(string entityId) : EntityRefWrapper<HaAssistSatellite>(entityId);

public sealed class HaAutomation(string entityId) : EntityRefWrapper<HaAutomation>(entityId);

public sealed class HaButton(string entityId) : EntityRefWrapper<HaButton>(entityId);

public sealed class HaCalendar(string entityId) : EntityRefWrapper<HaCalendar>(entityId);

public sealed class HaCamera(string entityId) : EntityRefWrapper<HaCamera>(entityId);

public sealed class HaClimate(string entityId) : EntityRefWrapper<HaClimate>(entityId);

public sealed class HaConversation(string entityId) : EntityRefWrapper<HaConversation>(entityId);

public sealed class HaCounter(string entityId) : EntityRefWrapper<HaCounter>(entityId);

public sealed class HaCover(string entityId) : EntityRefWrapper<HaCover>(entityId);

public sealed class HaDate(string entityId) : EntityRefWrapper<HaDate>(entityId);

public sealed class HaDateTime(string entityId) : EntityRefWrapper<HaDateTime>(entityId);

public sealed class HaDeviceTracker(string entityId) : EntityRefWrapper<HaDeviceTracker>(entityId);

public sealed class HaEvent(string entityId) : EntityRefWrapper<HaEvent>(entityId);

public sealed class HaFan(string entityId) : EntityRefWrapper<HaFan>(entityId);

public sealed class HaGroup(string entityId) : EntityRefWrapper<HaGroup>(entityId);

public sealed class HaGeoLocation(string entityId) : EntityRefWrapper<HaGeoLocation>(entityId);

public sealed class HaHumidifier(string entityId) : EntityRefWrapper<HaHumidifier>(entityId);

public sealed class HaImage(string entityId) : EntityRefWrapper<HaImage>(entityId);

public sealed class HaImageProcessing(string entityId) : EntityRefWrapper<HaImageProcessing>(entityId);

public sealed class HaInputBoolean(string entityId) : EntityRefWrapper<HaInputBoolean>(entityId);

public sealed class HaInputDateTime(string entityId) : EntityRefWrapper<HaInputDateTime>(entityId);

public sealed class HaInputSelect(string entityId) : EntityRefWrapper<HaInputSelect>(entityId);

public sealed class HaInputText(string entityId) : EntityRefWrapper<HaInputText>(entityId);

public sealed class HaLawnMower(string entityId) : EntityRefWrapper<HaLawnMower>(entityId);

public sealed class HaLock(string entityId) : EntityRefWrapper<HaLock>(entityId);

public sealed class HaNotify(string entityId) : EntityRefWrapper<HaNotify>(entityId);

public sealed class HaNumber(string entityId) : EntityRefWrapper<HaNumber>(entityId);

public sealed class HaPerson(string entityId) : EntityRefWrapper<HaPerson>(entityId);

public sealed class HaPlant(string entityId) : EntityRefWrapper<HaPlant>(entityId);

public sealed class HaRemote(string entityId) : EntityRefWrapper<HaRemote>(entityId);

public sealed class HaScene(string entityId) : EntityRefWrapper<HaScene>(entityId);

public sealed class HaSchedule(string entityId) : EntityRefWrapper<HaSchedule>(entityId);

public sealed class HaScript(string entityId) : EntityRefWrapper<HaScript>(entityId);

public sealed class HaSelect(string entityId) : EntityRefWrapper<HaSelect>(entityId);

public sealed class HaSiren(string entityId) : EntityRefWrapper<HaSiren>(entityId);

public sealed class HaStt(string entityId) : EntityRefWrapper<HaStt>(entityId);

public sealed class HaTag(string entityId) : EntityRefWrapper<HaTag>(entityId);

public sealed class HaText(string entityId) : EntityRefWrapper<HaText>(entityId);

public sealed class HaTime(string entityId) : EntityRefWrapper<HaTime>(entityId);

public sealed class HaTimer(string entityId) : EntityRefWrapper<HaTimer>(entityId);

public sealed class HaTodo(string entityId) : EntityRefWrapper<HaTodo>(entityId);

public sealed class HaTts(string entityId) : EntityRefWrapper<HaTts>(entityId);

public sealed class HaUpdate(string entityId) : EntityRefWrapper<HaUpdate>(entityId);

public sealed class HaVacuum(string entityId) : EntityRefWrapper<HaVacuum>(entityId);

public sealed class HaValve(string entityId) : EntityRefWrapper<HaValve>(entityId);

public sealed class HaWakeWord(string entityId) : EntityRefWrapper<HaWakeWord>(entityId);

public sealed class HaWaterHeater(string entityId) : EntityRefWrapper<HaWaterHeater>(entityId);

public sealed class HaWeather(string entityId) : EntityRefWrapper<HaWeather>(entityId);

public sealed class HaZone(string entityId) : EntityRefWrapper<HaZone>(entityId);

public sealed class HaUnknown(string entityId) : EntityRefWrapper<HaUnknown>(entityId);

public sealed class HaMotionSensor(string entityId) : EntityRefWrapper<HaMotionSensor>(entityId);

public sealed class ShellyButton(string entityId) : EntityRefWrapper<ShellyButton>(entityId);

public static class EntityRefExtensions
{
    extension(EntityRef<HaUnknown> unknown)
    {
        public string Value => unknown.Raw.State;
        public EntityRef<T> As<T>() => new(unknown.Raw);
    }

    extension(EntityRef<HaInputNumber> num)
    {
        public float Value => float.Parse(num.Raw.State);

        [Pure]
        public ServiceCall SetValue(int value) => HassServices.InputNumber.SetValue(num.EntityId, value);
    }

    extension(EntityRef<HaSwitch> sw)
    {
        public bool IsOn() => sw.Raw.State == "on";
        public bool IsOff() => sw.Raw.State == "off";

        public ServiceCall TurnOn() => HassServices.Switch.TurnOn(sw.EntityId);
        public ServiceCall TurnOff() => HassServices.Switch.TurnOff(sw.EntityId);
        public ServiceCall Toggle() => HassServices.Switch.Toggle(sw.EntityId);
    }

    extension(EntityRef<HaBinarySensor> s)
    {
        public bool isState(string state) => s.Raw.State == state;
        public bool isOn() => s.Raw.State == "on";
        public bool isOff() => s.Raw.State == "off";

        public EntityRef<HaMotionSensor> AsMotion() => new(s.Raw);
    }

    extension(EntityRef<HaMotionSensor> m)
    {
        public bool IsMotionDetected() => m.Raw.State == "on";
        public bool IsClear() => m.Raw.State == "off";
    }

    extension(EntityRef<ShellyButton> btn)
    {
        public string? Value => btn.GetAttribute<string>("event_type");

        bool IsState(string state) => btn.Value == state;

        public bool IsSinglePress() => btn.IsState("press");
        public bool IsDoublePress() => btn.IsState("double_press");
        public bool IsTriplePress() => btn.IsState("triple_press");
        public bool IsLongPress() => btn.IsState("long_press");
        public bool IsLongDoublePress() => btn.IsState("long_double_press");
        public bool IsLongTriplePress() => btn.IsState("long_triple_press");
        public bool IsHoldPress() => btn.IsState("hold_press");
    }

    extension(EntityRef<HaSun> sun)
    {
        public bool IsRising() => sun.GetAttribute<bool>("rising");

        public bool IsBelowHorizon() => sun.Raw.State == "below_horizon";
        public bool IsAboveHorizon() => sun.Raw.State == "above_horizon";
    }

    extension(EntityRef<HaMediaPlayer> p)
    {
        public ServiceCall TurnOn() => HassServices.MediaPlayer.TurnOn(p.EntityId);

        public ServiceCall TurnOff() => HassServices.MediaPlayer.TurnOff(p.EntityId);

        public ServiceCall Toggle() => HassServices.MediaPlayer.Toggle(p.EntityId);

        /// <summary>
        /// Will be 0 if the media player is off, call TurnOn first and then Refresh before calling this
        /// </summary>
        public double VolumeLevel => p.GetAttribute<double>("volume_level");

        public ServiceCall PlayLocal(string media, string? mediaContentType = null)
            => HassServices.MediaPlayer.MediaPlayLocal(p.EntityId, media, mediaContentType);

        public ServiceCall TextToSpeech(string speech, string? ttsEnttiyId = null)
            => HassServices.MediaPlayer.TextToSpeech(p.EntityId, speech, ttsEnttiyId);

        public ServiceCall SetVolumeLevel(double level)
            => HassServices.MediaPlayer.SetVolumeLevel(p.EntityId, level);
    }

    extension(EntityRef<HaLight> light)
    {
        public bool IsOn() => light.Raw.State == "on";
        public bool IsOff() => light.Raw.State == "off";

        [Pure]
        public ServiceCall TurnOn() => HassServices.Light.TurnOn(light.EntityId);

        [Pure]
        public ServiceCall TurnOn(LightOnOpts opts) => HassServices.Light.TurnOn(light.EntityId, opts);

        [Pure]
        public ServiceCall TurnOff() => HassServices.Light.TurnOff(light.EntityId);

        [Pure]
        public ServiceCall TurnOff(LightOffOpts opts) => HassServices.Light.TurnOff(light.EntityId, opts);

        [Pure]
        public ServiceCall Toggle() => HassServices.Light.Toggle(light.EntityId);
    }

    extension(EntityRef<HaInputBoolean> input)
    {
        public bool IsOn() => input.Raw.State == "on";
        public bool IsOff() => input.Raw.State == "off";

        public ServiceCall TurnOn() => HassServices.InputBoolean.TurnOn(input.EntityId);
        public ServiceCall TurnOff() => HassServices.InputBoolean.TurnOff(input.EntityId);
        public ServiceCall Toggle() => HassServices.InputBoolean.Toggle(input.EntityId);
    }

    extension(EntityRef<HaButton> button)
    {
        public bool IsPressed() => button.StateChanged();
        public ServiceCall Press() => HassServices.Button.Press(button.EntityId);
    }

    extension(EntityRef<HaInputButton> button)
    {
        public ServiceCall Press() => HassServices.InputButton.Press(button.EntityId);
    }

    extension(EntityRef<HaAlarmControlPanel> alarm)
    {
        public bool IsDisarmed() => alarm.Raw.State == "disarmed";
        public bool IsArmed() => alarm.Raw.State.StartsWith("armed_", StringComparison.Ordinal);
        public bool IsTriggered() => alarm.Raw.State == "triggered";

        public ServiceCall Disarm(string? code = null) =>
            HassServices.AlarmControlPanel.Disarm(alarm.EntityId, code);

        public ServiceCall ArmHome(string? code = null) =>
            HassServices.AlarmControlPanel.ArmHome(alarm.EntityId, code);

        public ServiceCall ArmAway(string? code = null) =>
            HassServices.AlarmControlPanel.ArmAway(alarm.EntityId, code);

        public ServiceCall ArmNight(string? code = null) =>
            HassServices.AlarmControlPanel.ArmNight(alarm.EntityId, code);

        public ServiceCall ArmVacation(string? code = null) =>
            HassServices.AlarmControlPanel.ArmVacation(alarm.EntityId, code);

        public ServiceCall Trigger(string? code = null) =>
            HassServices.AlarmControlPanel.Trigger(alarm.EntityId, code);
    }

    extension(EntityRef<HaAlert> alert)
    {
        public bool IsOn() => alert.Raw.State == "on";
        public bool IsOff() => alert.Raw.State == "off";

        public ServiceCall TurnOn() => HassServices.Alert.TurnOn(alert.EntityId);
        public ServiceCall TurnOff() => HassServices.Alert.TurnOff(alert.EntityId);
        public ServiceCall Toggle() => HassServices.Alert.Toggle(alert.EntityId);
    }

    extension(EntityRef<HaAutomation> automation)
    {
        public bool IsOn() => automation.Raw.State == "on";
        public bool IsOff() => automation.Raw.State == "off";

        public ServiceCall TurnOn() => HassServices.Automation.TurnOn(automation.EntityId);

        public ServiceCall TurnOff(bool stopActions = true) =>
            HassServices.Automation.TurnOff(automation.EntityId, stopActions);

        public ServiceCall Toggle() => HassServices.Automation.Toggle(automation.EntityId);

        public ServiceCall Trigger(bool skipCondition = true) =>
            HassServices.Automation.Trigger(automation.EntityId, skipCondition);
    }

    extension(EntityRef<HaAssistSatellite> satellite)
    {
        public ServiceCall Announce(string message, bool preannounce = true) =>
            HassServices.AssistSatellite.Announce(satellite.EntityId, message, preannounce);

        public ServiceCall AnnounceMedia(string mediaId, bool preannounce = true) =>
            HassServices.AssistSatellite.AnnounceMedia(satellite.EntityId, mediaId, preannounce);

        public ServiceCall StartConversation(string message, string? extraSystemPrompt = null,
            bool preannounce = true) =>
            HassServices.AssistSatellite.StartConversation(
                satellite.EntityId, message, extraSystemPrompt, preannounce);
    }

    extension(EntityRef<HaEvent> eventEntity)
    {
        public string? EventType => eventEntity.GetAttribute<string>("event_type");
        public bool IsEvent(string eventType) => eventEntity.EventType == eventType;
    }

    extension(EntityRef<HaDeviceTracker> tracker)
    {
        public bool IsHome() => tracker.Raw.State == "home";
        public bool IsAway() => tracker.Raw.State == "not_home";
        public double? Latitude => tracker.GetAttribute<double?>("latitude");
        public double? Longitude => tracker.GetAttribute<double?>("longitude");
        public int? GpsAccuracy => tracker.GetAttribute<int?>("gps_accuracy");
    }

    extension(EntityRef<HaPerson> person)
    {
        public bool IsHome() => person.Raw.State == "home";
        public bool IsAway() => person.Raw.State == "not_home";
        public double? Latitude => person.GetAttribute<double?>("latitude");
        public double? Longitude => person.GetAttribute<double?>("longitude");
    }

    extension(EntityRef<HaPlant> plant)
    {
        public bool IsOk() => plant.Raw.State == "ok";
        public bool HasProblem() => plant.Raw.State == "problem";
    }

    extension(EntityRef<HaCalendar> calendar)
    {
        public bool HasActiveEvent() => calendar.Raw.State == "on";
        public string? Message => calendar.GetAttribute<string>("message");
        public string? Location => calendar.GetAttribute<string>("location");

        public ServiceCall CreateEvent(string summary, DateTimeOffset start, DateTimeOffset end,
            string? description = null, string? location = null) =>
            HassServices.Calendar.CreateEvent(
                calendar.EntityId, summary, start, end, description, location);

        public ServiceCall CreateAllDayEvent(string summary, DateOnly start, DateOnly end,
            string? description = null, string? location = null) =>
            HassServices.Calendar.CreateAllDayEvent(
                calendar.EntityId, summary, start, end, description, location);

        public ServiceCall<Dictionary<EntityId, CalendarEventsResult>> GetEvents(
            DateTimeOffset end, DateTimeOffset? start = null) =>
            HassServices.Calendar.GetEvents(calendar.EntityId, end, start);

        public ServiceCall<Dictionary<EntityId, CalendarEventsResult>> GetEvents(
            TimeSpan duration, DateTimeOffset? start = null) =>
            HassServices.Calendar.GetEvents(calendar.EntityId, duration, start);
    }

    extension(EntityRef<HaCamera> camera)
    {
        public bool IsStreaming() => camera.Raw.State == "streaming";
        public bool IsRecording() => camera.Raw.State == "recording";

        public ServiceCall TurnOn() => HassServices.Camera.TurnOn(camera.EntityId);
        public ServiceCall TurnOff() => HassServices.Camera.TurnOff(camera.EntityId);

        public ServiceCall EnableMotionDetection() =>
            HassServices.Camera.EnableMotionDetection(camera.EntityId);

        public ServiceCall DisableMotionDetection() =>
            HassServices.Camera.DisableMotionDetection(camera.EntityId);

        public ServiceCall Snapshot(string filename) => HassServices.Camera.Snapshot(camera.EntityId, filename);

        public ServiceCall PlayStream(EntityId mediaPlayer, string format = "hls") =>
            HassServices.Camera.PlayStream(camera.EntityId, mediaPlayer, format);

        public ServiceCall Record(string filename, int duration = 30, int lookback = 0) =>
            HassServices.Camera.Record(camera.EntityId, filename, duration, lookback);
    }

    extension(EntityRef<HaClimate> climate)
    {
        public string HvacMode => climate.Raw.State;
        public double? CurrentTemperature => climate.GetAttribute<double?>("current_temperature");
        public double? TargetTemperature => climate.GetAttribute<double?>("temperature");
        public int? CurrentHumidity => climate.GetAttribute<int?>("current_humidity");

        public ServiceCall SetTemperature(double temperature, string? hvacMode = null) =>
            HassServices.Climate.SetTemperature(climate.EntityId, temperature, hvacMode);

        public ServiceCall SetTemperatureRange(double low, double high, string? hvacMode = null) =>
            HassServices.Climate.SetTemperatureRange(climate.EntityId, low, high, hvacMode);

        public ServiceCall SetHvacMode(string mode) =>
            HassServices.Climate.SetHvacMode(climate.EntityId, mode);

        public ServiceCall SetPresetMode(string mode) =>
            HassServices.Climate.SetPresetMode(climate.EntityId, mode);

        public ServiceCall SetHumidity(int humidity) =>
            HassServices.Climate.SetHumidity(climate.EntityId, humidity);

        public ServiceCall TurnOn() => HassServices.Climate.TurnOn(climate.EntityId);
        public ServiceCall TurnOff() => HassServices.Climate.TurnOff(climate.EntityId);
        public ServiceCall Toggle() => HassServices.Climate.Toggle(climate.EntityId);
    }

    extension(EntityRef<HaCounter> counter)
    {
        public int Value => int.Parse(counter.Raw.State);

        public ServiceCall Increment() => HassServices.Counter.Increment(counter.EntityId);
        public ServiceCall Decrement() => HassServices.Counter.Decrement(counter.EntityId);
        public ServiceCall Reset() => HassServices.Counter.Reset(counter.EntityId);
        public ServiceCall SetValue(int value) => HassServices.Counter.SetValue(counter.EntityId, value);
    }

    extension(EntityRef<HaCover> cover)
    {
        public bool IsOpen() => cover.Raw.State == "open";
        public bool IsClosed() => cover.Raw.State == "closed";
        public bool IsOpening() => cover.Raw.State == "opening";
        public bool IsClosing() => cover.Raw.State == "closing";
        public int? Position => cover.GetAttribute<int?>("current_position");

        public ServiceCall Open() => HassServices.Cover.OpenCover(cover.EntityId);
        public ServiceCall Close() => HassServices.Cover.CloseCover(cover.EntityId);
        public ServiceCall Stop() => HassServices.Cover.StopCover(cover.EntityId);
        public ServiceCall Toggle() => HassServices.Cover.Toggle(cover.EntityId);

        public ServiceCall SetPosition(int position) =>
            HassServices.Cover.SetCoverPosition(cover.EntityId, position);

        public ServiceCall OpenTilt() => HassServices.Cover.OpenCoverTilt(cover.EntityId);
        public ServiceCall CloseTilt() => HassServices.Cover.CloseCoverTilt(cover.EntityId);
        public ServiceCall StopTilt() => HassServices.Cover.StopCoverTilt(cover.EntityId);

        public ServiceCall SetTiltPosition(int position) =>
            HassServices.Cover.SetCoverTiltPosition(cover.EntityId, position);
    }

    extension(EntityRef<HaDate> date)
    {
        public DateOnly Value => DateOnly.Parse(date.Raw.State);
        public ServiceCall SetValue(DateOnly value) => HassServices.Date.SetValue(date.EntityId, value);
    }

    extension(EntityRef<HaDateTime> dateTime)
    {
        public DateTimeOffset Value => DateTimeOffset.Parse(dateTime.Raw.State);

        public ServiceCall SetValue(DateTimeOffset value) =>
            HassServices.DateTime.SetValue(dateTime.EntityId, value);
    }

    extension(EntityRef<HaFan> fan)
    {
        public bool IsOn() => fan.Raw.State == "on";
        public bool IsOff() => fan.Raw.State == "off";
        public int? Percentage => fan.GetAttribute<int?>("percentage");

        public ServiceCall TurnOn(int? percentage = null, string? presetMode = null) =>
            HassServices.Fan.TurnOn(fan.EntityId, percentage, presetMode);

        public ServiceCall TurnOff() => HassServices.Fan.TurnOff(fan.EntityId);
        public ServiceCall Toggle() => HassServices.Fan.Toggle(fan.EntityId);

        public ServiceCall SetPercentage(int percentage) =>
            HassServices.Fan.SetPercentage(fan.EntityId, percentage);

        public ServiceCall SetPresetMode(string mode) =>
            HassServices.Fan.SetPresetMode(fan.EntityId, mode);

        public ServiceCall Oscillate(bool oscillating) =>
            HassServices.Fan.Oscillate(fan.EntityId, oscillating);
    }

    extension(EntityRef<HaHumidifier> humidifier)
    {
        public bool IsOn() => humidifier.Raw.State == "on";
        public bool IsOff() => humidifier.Raw.State == "off";
        public int? CurrentHumidity => humidifier.GetAttribute<int?>("current_humidity");
        public int? TargetHumidity => humidifier.GetAttribute<int?>("humidity");

        public ServiceCall TurnOn() => HassServices.Humidifier.TurnOn(humidifier.EntityId);
        public ServiceCall TurnOff() => HassServices.Humidifier.TurnOff(humidifier.EntityId);
        public ServiceCall Toggle() => HassServices.Humidifier.Toggle(humidifier.EntityId);

        public ServiceCall SetHumidity(int humidity) =>
            HassServices.Humidifier.SetHumidity(humidifier.EntityId, humidity);

        public ServiceCall SetMode(string mode) =>
            HassServices.Humidifier.SetMode(humidifier.EntityId, mode);
    }

    extension(EntityRef<HaImage> image)
    {
        public ServiceCall Snapshot(string filename) => HassServices.Image.Snapshot(image.EntityId, filename);
    }

    extension(EntityRef<HaImageProcessing> imageProcessing)
    {
        public ServiceCall Scan() => HassServices.ImageProcessing.Scan(imageProcessing.EntityId);
    }

    extension(EntityRef<HaInputDateTime> input)
    {
        public string Value => input.Raw.State;

        public ServiceCall SetDate(DateOnly date) => HassServices.InputDateTime.SetDate(input.EntityId, date);
        public ServiceCall SetTime(TimeOnly time) => HassServices.InputDateTime.SetTime(input.EntityId, time);

        public ServiceCall SetDateTime(DateTimeOffset dateTime) =>
            HassServices.InputDateTime.SetDateTime(input.EntityId, dateTime);

        public ServiceCall SetTimestamp(double timestamp) =>
            HassServices.InputDateTime.SetTimestamp(input.EntityId, timestamp);
    }

    extension(EntityRef<HaInputSelect> input)
    {
        public string Value => input.Raw.State;

        public ServiceCall Select(string option) => HassServices.InputSelect.SelectOption(input.EntityId, option);

        public ServiceCall SelectNext(bool cycle = true) =>
            HassServices.InputSelect.SelectNext(input.EntityId, cycle);

        public ServiceCall SelectPrevious(bool cycle = true) =>
            HassServices.InputSelect.SelectPrevious(input.EntityId, cycle);

        public ServiceCall SelectFirst() => HassServices.InputSelect.SelectFirst(input.EntityId);
        public ServiceCall SelectLast() => HassServices.InputSelect.SelectLast(input.EntityId);

        public ServiceCall SetOptions(params string[] options) =>
            HassServices.InputSelect.SetOptions(input.EntityId, options);
    }

    extension(EntityRef<HaInputText> input)
    {
        public string Value => input.Raw.State;
        public ServiceCall SetValue(string value) => HassServices.InputText.SetValue(input.EntityId, value);
    }

    extension(EntityRef<HaLawnMower> mower)
    {
        public bool IsMowing() => mower.Raw.State == "mowing";
        public bool IsDocked() => mower.Raw.State == "docked";
        public bool IsPaused() => mower.Raw.State == "paused";

        public ServiceCall Start() => HassServices.LawnMower.StartMowing(mower.EntityId);
        public ServiceCall Dock() => HassServices.LawnMower.Dock(mower.EntityId);
        public ServiceCall Pause() => HassServices.LawnMower.Pause(mower.EntityId);
    }

    extension(EntityRef<HaLock> lockEntity)
    {
        public bool IsLocked() => lockEntity.Raw.State == "locked";
        public bool IsUnlocked() => lockEntity.Raw.State == "unlocked";
        public bool IsJammed() => lockEntity.Raw.State == "jammed";

        public ServiceCall Lock(string? code = null) => HassServices.Lock.Lock(lockEntity.EntityId, code);
        public ServiceCall Unlock(string? code = null) => HassServices.Lock.Unlock(lockEntity.EntityId, code);
        public ServiceCall Open(string? code = null) => HassServices.Lock.Open(lockEntity.EntityId, code);
    }

    extension(EntityRef<HaNotify> notification)
    {
        public ServiceCall Send(string message, string? title = null) =>
            HassServices.Notification.SendMessage(notification.EntityId, message, title);
    }

    extension(EntityRef<HaNumber> number)
    {
        public double Value => double.Parse(number.Raw.State);
        public ServiceCall SetValue(double value) => HassServices.Number.SetValue(number.EntityId, value);
    }

    extension(EntityRef<HaRemote> remote)
    {
        public bool IsOn() => remote.Raw.State == "on";
        public bool IsOff() => remote.Raw.State == "off";

        public ServiceCall TurnOn(string? activity = null) =>
            HassServices.Remote.TurnOn(remote.EntityId, activity);

        public ServiceCall TurnOff() => HassServices.Remote.TurnOff(remote.EntityId);
        public ServiceCall Toggle() => HassServices.Remote.Toggle(remote.EntityId);

        public ServiceCall SendCommand(string device, params string[] commands) =>
            HassServices.Remote.SendCommand(remote.EntityId, device, commands);
    }

    extension(EntityRef<HaScene> scene)
    {
        public ServiceCall Activate(double? transition = null) =>
            HassServices.Scene.TurnOn(scene.EntityId, transition);
    }

    extension(EntityRef<HaSchedule> schedule)
    {
        public bool IsOn() => schedule.Raw.State == "on";
        public bool IsOff() => schedule.Raw.State == "off";

        public ServiceCall<Dictionary<EntityId, Dictionary<string, ScheduleTimeRange<TData>[]>>>
            GetSchedule<TData>() => HassServices.Schedule.GetSchedule<TData>(schedule.EntityId);
    }

    extension(EntityRef<HaScript> script)
    {
        public bool IsRunning() => script.Raw.State == "on";

        public ServiceCall Run() => HassServices.Script.TurnOn(script.EntityId);
        public ServiceCall Stop() => HassServices.Script.TurnOff(script.EntityId);
        public ServiceCall Toggle() => HassServices.Script.Toggle(script.EntityId);
    }

    extension(EntityRef<HaSelect> select)
    {
        public string Value => select.Raw.State;

        public ServiceCall Select(string option) => HassServices.Select.SelectOption(select.EntityId, option);

        public ServiceCall SelectNext(bool cycle = true) =>
            HassServices.Select.SelectNext(select.EntityId, cycle);

        public ServiceCall SelectPrevious(bool cycle = true) =>
            HassServices.Select.SelectPrevious(select.EntityId, cycle);

        public ServiceCall SelectFirst() => HassServices.Select.SelectFirst(select.EntityId);
        public ServiceCall SelectLast() => HassServices.Select.SelectLast(select.EntityId);
    }

    extension(EntityRef<HaSiren> siren)
    {
        public bool IsOn() => siren.Raw.State == "on";
        public bool IsOff() => siren.Raw.State == "off";

        public ServiceCall TurnOn(string? tone = null, double? volumeLevel = null, TimeSpan? duration = null) =>
            HassServices.Siren.TurnOn(siren.EntityId, tone, volumeLevel, duration);

        public ServiceCall TurnOff() => HassServices.Siren.TurnOff(siren.EntityId);
        public ServiceCall Toggle() => HassServices.Siren.Toggle(siren.EntityId);
    }

    extension(EntityRef<HaText> text)
    {
        public string Value => text.Raw.State;
        public ServiceCall SetValue(string value) => HassServices.Text.SetValue(text.EntityId, value);
    }

    extension(EntityRef<HaTime> time)
    {
        public TimeOnly Value => TimeOnly.Parse(time.Raw.State);
        public ServiceCall SetValue(TimeOnly value) => HassServices.Time.SetValue(time.EntityId, value);
    }

    extension(EntityRef<HaTimer> timer)
    {
        public bool IsActive() => timer.Raw.State == "active";
        public bool IsPaused() => timer.Raw.State == "paused";
        public bool IsIdle() => timer.Raw.State == "idle";
        public string? Remaining => timer.GetAttribute<string>("remaining");

        public ServiceCall Start(TimeSpan? duration = null) => HassServices.Timer.Start(timer.EntityId, duration);
        public ServiceCall Pause() => HassServices.Timer.Pause(timer.EntityId);
        public ServiceCall Cancel() => HassServices.Timer.Cancel(timer.EntityId);
        public ServiceCall Finish() => HassServices.Timer.Finish(timer.EntityId);
        public ServiceCall Change(TimeSpan duration) => HassServices.Timer.Change(timer.EntityId, duration);
    }

    extension(EntityRef<HaTodo> todo)
    {
        public async Task<TodoItemResult[]> GetItems(params TodoItemStatus[] statuses)
        {
            var todos = await HassServices.Todo.GetItems(todo.EntityId, statuses).RunAsync();
            return todos?.GetValueOrDefault(todo.EntityId)?.Items ?? [];
        }

        public ServiceCall Add(string item, string? description = null) =>
            HassServices.Todo.AddItem(todo.EntityId, item, description);

        public ServiceCall Add(string item, DateOnly dueDate, string? description = null) =>
            HassServices.Todo.AddItem(todo.EntityId, item, dueDate, description);

        public ServiceCall Add(string item, DateTimeOffset dueDateTime, string? description = null) =>
            HassServices.Todo.AddItem(todo.EntityId, item, dueDateTime, description);

        public ServiceCall Remove(params string[] items) => HassServices.Todo.RemoveItem(todo.EntityId, items);
        public ServiceCall RemoveCompleted() => HassServices.Todo.RemoveCompletedItems(todo.EntityId);
    }

    extension(EntityRef<HaTts> tts)
    {
        public ServiceCall Speak(EntityId mediaPlayer, string message, bool cache = true,
            string? language = null, object? options = null) =>
            HassServices.Tts.Speak(tts.EntityId, mediaPlayer, message, cache, language, options);
    }

    extension(EntityRef<HaUpdate> update)
    {
        public bool IsAvailable() => update.Raw.State == "on";
        public string? InstalledVersion => update.GetAttribute<string>("installed_version");
        public string? LatestVersion => update.GetAttribute<string>("latest_version");

        public ServiceCall Install(string? version = null, bool backup = false) =>
            HassServices.Update.Install(update.EntityId, version, backup);

        public ServiceCall Skip() => HassServices.Update.Skip(update.EntityId);
        public ServiceCall ClearSkipped() => HassServices.Update.ClearSkipped(update.EntityId);
    }

    extension(EntityRef<HaVacuum> vacuum)
    {
        public bool IsCleaning() => vacuum.Raw.State == "cleaning";
        public bool IsDocked() => vacuum.Raw.State == "docked";
        public bool IsPaused() => vacuum.Raw.State == "paused";

        public ServiceCall Start() => HassServices.Vacuum.Start(vacuum.EntityId);
        public ServiceCall Pause() => HassServices.Vacuum.Pause(vacuum.EntityId);
        public ServiceCall Stop() => HassServices.Vacuum.Stop(vacuum.EntityId);
        public ServiceCall ReturnToBase() => HassServices.Vacuum.ReturnToBase(vacuum.EntityId);
        public ServiceCall Locate() => HassServices.Vacuum.Locate(vacuum.EntityId);

        public ServiceCall SetFanSpeed(string speed) =>
            HassServices.Vacuum.SetFanSpeed(vacuum.EntityId, speed);
    }

    extension(EntityRef<HaValve> valve)
    {
        public bool IsOpen() => valve.Raw.State == "open";
        public bool IsClosed() => valve.Raw.State == "closed";
        public bool IsOpening() => valve.Raw.State == "opening";
        public bool IsClosing() => valve.Raw.State == "closing";
        public int? Position => valve.GetAttribute<int?>("current_position");

        public ServiceCall Open() => HassServices.Valve.Open(valve.EntityId);
        public ServiceCall Close() => HassServices.Valve.Close(valve.EntityId);
        public ServiceCall Stop() => HassServices.Valve.Stop(valve.EntityId);
        public ServiceCall Toggle() => HassServices.Valve.Toggle(valve.EntityId);

        public ServiceCall SetPosition(int position) =>
            HassServices.Valve.SetPosition(valve.EntityId, position);
    }

    extension(EntityRef<HaWaterHeater> waterHeater)
    {
        public bool IsOff() => waterHeater.Raw.State == "off";
        public string OperationMode => waterHeater.Raw.State;
        public double? CurrentTemperature => waterHeater.GetAttribute<double?>("current_temperature");
        public double? TargetTemperature => waterHeater.GetAttribute<double?>("temperature");

        public ServiceCall TurnOn() => HassServices.WaterHeater.TurnOn(waterHeater.EntityId);
        public ServiceCall TurnOff() => HassServices.WaterHeater.TurnOff(waterHeater.EntityId);

        public ServiceCall SetTemperature(double temperature, string? operationMode = null) =>
            HassServices.WaterHeater.SetTemperature(waterHeater.EntityId, temperature, operationMode);

        public ServiceCall SetOperationMode(string mode) =>
            HassServices.WaterHeater.SetOperationMode(waterHeater.EntityId, mode);

        public ServiceCall SetAwayMode(bool away) =>
            HassServices.WaterHeater.SetAwayMode(waterHeater.EntityId, away);
    }

    extension(EntityRef<HaWeather> weather)
    {
        public string Condition => weather.Raw.State;
        public double? Temperature => weather.GetAttribute<double?>("temperature");
        public double? Humidity => weather.GetAttribute<double?>("humidity");

        public ServiceCall<Dictionary<EntityId, WeatherForecastResult>> GetForecasts(
            WeatherForecastType type) => HassServices.Weather.GetForecasts(weather.EntityId, type);
    }

    extension(EntityRef<HaGroup> group)
    {
        public bool IsOn() => group.Raw.State == "on";
        public bool IsOff() => group.Raw.State == "off";

        public ServiceCall TurnOn() => HassServices.HomeAssistant.TurnOn(group.EntityId);
        public ServiceCall TurnOff() => HassServices.HomeAssistant.TurnOff(group.EntityId);
        public ServiceCall Toggle() => HassServices.HomeAssistant.Toggle(group.EntityId);
    }

    extension(EntityRef<HaZone> zone)
    {
        public int PersonCount => int.Parse(zone.Raw.State);
        public bool IsOccupied() => zone.PersonCount > 0;
    }
}
