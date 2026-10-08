using System.Text.Json;
using System.Text.Json.Serialization;

namespace HassSharp;

public readonly ref struct ServiceCall
{
    readonly string _domain;
    readonly string _service;
    readonly object? _data;

    public ServiceCall(string domain, string service, object? data)
    {
        _domain = domain;
        _service = service;
        _data = data;
    }

    public void Run() => HassServices.CallService(_domain, _service, _data);

    public Task RunAsync() => HassServices.CallServiceAsync(_domain, _service, _data);
}

public readonly ref struct ServiceCall<TResult>
{
    readonly string _domain;
    readonly string _service;
    readonly object? _data;

    public ServiceCall(string domain, string service, object? data)
    {
        _domain = domain;
        _service = service;
        _data = data;
    }

    public Task<TResult?> RunAsync() => HassServices.CallServiceAsync<TResult>(_domain, _service, _data);
}

public class HassServices
{
    public static readonly HassServices Instance = new();

    public static readonly NotificationService Notification = new();
    public static readonly LightService Light = new();
    public static readonly SwitchService Switch = new();
    public static readonly MediaPlayerService MediaPlayer = new();
    public static readonly InputBooleanService InputBoolean = new();
    public static readonly InputNumberService InputNumber = new();
    public static readonly InputSelectService InputSelect = new();
    public static readonly CoverService Cover = new();
    public static readonly ClimateService Climate = new();
    public static readonly FanService Fan = new();
    public static readonly SceneService Scene = new();
    public static readonly TtsService Tts = new();
    public static readonly AiTaskService AiTask = new();
    public static readonly AlarmControlPanelService AlarmControlPanel = new();
    public static readonly AlertService Alert = new();
    public static readonly AssistSatelliteService AssistSatellite = new();
    public static readonly AutomationService Automation = new();
    public static readonly BackupService Backup = new();
    public static readonly ButtonService Button = new();
    public static readonly CalendarService Calendar = new();
    public static readonly CameraService Camera = new();
    public static readonly ConversationService Conversation = new();
    public static readonly CounterService Counter = new();
    public static readonly ReloadOnlyService Derivative = new("derivative");
    public static readonly DateService Date = new();
    public static readonly DateTimeEntityService DateTime = new();
    public static readonly DeviceTrackerService DeviceTracker = new();
    public static readonly FrontendService Frontend = new();
    public static readonly FfmpegService Ffmpeg = new();
    public static readonly ReloadOnlyService Filter = new("filter");
    public static readonly ReloadOnlyService GenericThermostat = new("generic_thermostat");
    public static readonly GoogleAssistantService GoogleAssistant = new();
    public static readonly GroupService Group = new();
    public static readonly HomeAssistantService HomeAssistant = new();
    public static readonly HumidifierService Humidifier = new();
    public static readonly ImageService Image = new();
    public static readonly ImageProcessingService ImageProcessing = new();
    public static readonly InputButtonService InputButton = new();
    public static readonly InputDateTimeService InputDateTime = new();
    public static readonly InputTextService InputText = new();
    public static readonly LawnMowerService LawnMower = new();
    public static readonly LockService Lock = new();
    public static readonly LogbookService Logbook = new();
    public static readonly LoggerService Logging = new();
    public static readonly LovelaceService Lovelace = new();
    public static readonly ReloadOnlyService HistoryStats = new("history_stats");
    public static readonly ReloadOnlyService MinMax = new("min_max");
    public static readonly NumberService Number = new();
    public static readonly PersistentNotificationService PersistentNotification = new();
    public static readonly ReloadOnlyService Person = new("person");
    public static readonly RecorderService Recorder = new();
    public static readonly RemoteService Remote = new();
    public static readonly ScheduleService Schedule = new();
    public static readonly ScriptService Script = new();
    public static readonly SelectService Select = new();
    public static readonly ShoppingListService ShoppingList = new();
    public static readonly SirenService Siren = new();
    public static readonly ReloadOnlyService Statistics = new("statistics");
    public static readonly SystemLogService SystemLog = new();
    public static readonly TextService Text = new();
    public static readonly ReloadOnlyService Template = new("template");
    public static readonly TimeService Time = new();
    public static readonly TimerService Timer = new();
    public static readonly TodoService Todo = new();
    public static readonly ReloadOnlyService Trend = new("trend");
    public static readonly UpdateService Update = new();
    public static readonly UtilityMeterService UtilityMeter = new();
    public static readonly VacuumService Vacuum = new();
    public static readonly ValveService Valve = new();
    public static readonly WaterHeaterService WaterHeater = new();
    public static readonly WeatherService Weather = new();
    public static readonly ReloadOnlyService Zone = new("zone");
    public static readonly CloudService Cloud = new();

    static JsonSerializerOptions _jsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };


    internal static void CallService(string domain, string service, object? data = default)
    {
        var json = data != null ? JsonSerializer.Serialize(data, _jsonOpts) : null;
        Logger.Debug($"Calling service: {domain}.{service} with data: {json}");

        PyInterop.CallService(domain, service, json, null);
    }

    internal static Task CallServiceAsync(string domain, string service, object? data = default)
    {
        var json = data != null ? JsonSerializer.Serialize(data, _jsonOpts) : null;
        Logger.Debug($"Calling service (async): {domain}.{service} with data: {json}");

        var tcs = new TaskCompletionSource();
        PyInterop.CallService(domain, service, json, () => tcs.SetResult());

        return tcs.Task;
    }

    internal static Task<TResult?> CallServiceAsync<TResult>(string domain, string service, object? data = null)
    {
        var json = data != null ? JsonSerializer.Serialize(data, _jsonOpts) : null;
        Logger.Debug($"Calling service for response (async): {domain}.{service} with data: {json}");

        var tcs = new TaskCompletionSource<TResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        PyInterop.CallServiceWithResponse(
            domain,
            service,
            json,
            responseJson =>
            {
                try
                {
                    var result = responseJson == null
                        ? default
                        : JsonSerializer.Deserialize<TResult>(responseJson, _jsonOpts);
                    tcs.SetResult(result);
                }
                catch (Exception exception)
                {
                    tcs.SetException(exception);
                }
            },
            error => tcs.SetException(new InvalidOperationException(error)));

        return tcs.Task;
    }


    public ServiceCall Call(string domain, string service, object? data = default) =>
        new(domain, service, data);
}

public class NotificationService
{
    public class MobileNotificationOpts
    {
        public required string Title { get; init; }
        public required string Message { get; init; }
    }

    public ServiceCall CreateMobileNotification(string deviceName, MobileNotificationOpts opts) =>
        new("notify", deviceName, opts);

    public ServiceCall Notify(string message, string? title = null, object? target = null, object? data = null) =>
        new("notify", "notify", new { message, title, target, data });

    public ServiceCall SendMessage(EntityId entityId, string message, string? title = null) =>
        new("notify", "send_message", new { entity_id = entityId, message, title });

    public ServiceCall CreatePersistentNotification(string message, string? title = null, object? data = null) =>
        new("notify", "persistent_notification", new { message, title, data });
}

public class LightService
{
    public ServiceCall TurnOn(EntityId entityId) => TurnOn([entityId]);

    public ServiceCall TurnOn(params EntityId[] entities)
    {
        return new("light", "turn_on", new { entity_id = entities });
    }

    public ServiceCall TurnOn(LightOnOpts opts)
    {
        return new("light", "turn_on", opts);
    }


    public ServiceCall TurnOff(EntityId entityId) => TurnOff([entityId]);

    public ServiceCall TurnOff(EntityId[] entities)
    {
        return new("light", "turn_off", new { entity_id = entities });
    }

    public ServiceCall TurnOff(LightOffOpts opts) => new("light", "turn_off", opts);

    public ServiceCall Toggle(EntityId entityId) => Toggle([entityId]);

    public ServiceCall Toggle(params EntityId[] entities) =>
        new("light", "toggle", new { entity_id = entities });

    public ServiceCall Toggle(LightOnOpts opts) => new("light", "toggle", opts);
}

public class SwitchService
{
    public ServiceCall TurnOn(EntityId entityId) => new("switch", "turn_on", new { entity_id = entityId });

    public ServiceCall TurnOn(EntityId[] entities) => new("switch", "turn_on", new { entity_id = entities });


    public ServiceCall TurnOff(EntityId entityId) => new("switch", "turn_off", new { entity_id = entityId });

    public ServiceCall TurnOff(EntityId[] entities) => new("switch", "turn_off", new { entity_id = entities });

    public ServiceCall Toggle(EntityId[] entities) => new("switch", "toggle", new { entity_id = entities });
    public ServiceCall Toggle(EntityId entityId) => new("switch", "toggle", new { entity_id = entityId });
}

public class MediaPlayerService
{
    public ServiceCall TurnOn(EntityId entityId) => new("media_player", "turn_on", new { entity_id = entityId });
    public ServiceCall TurnOff(EntityId entityId) => new("media_player", "turn_off", new { entity_id = entityId });
    public ServiceCall Toggle(string entityId) => new("media_player", "toggle", new { entity_id = entityId });

    public ServiceCall VolumeUp(EntityId entityId) =>
        new("media_player", "volume_up", new { entity_id = entityId });

    public ServiceCall VolumeDown(EntityId entityId) =>
        new("media_player", "volume_down", new { entity_id = entityId });


    public ServiceCall MuteVolume(EntityId entityId, bool isVolumeMuted) => new("media_player", "volume_mute",
        new { entity_id = entityId, is_volume_muted = isVolumeMuted });

    public ServiceCall SetVolumeLevel(EntityId entityId, double volumeLevel) => new("media_player", "volume_set",
        new { entity_id = entityId, volume_level = volumeLevel });

    public ServiceCall MediaPlayPause(EntityId entityId) =>
        new("media_player", "media_play_pause", new { entity_id = entityId });

    public ServiceCall MediaPlay(EntityId entityId) => new("media_player", "media_play", new { entity_id = entityId });

    public ServiceCall TextToSpeech(EntityId entityId, string speech, string? ttsEnttiyId = null) => new("tts", "speak",
        new
        {
            entity_id = ttsEnttiyId ?? "tts.google_en.com",
            cache = true,
            media_player_entity_id = entityId,
            message = speech,
        });

    public ServiceCall MediaPlayLocal(EntityId entityId, string media, string? mediaContentType = null) => new(
        "media_player", "play_media", new
        {
            entity_id = entityId,
            media_content_id = $"media-source://media_source/local/{media}",
            media_content_type = mediaContentType ?? "audio/mpeg",
        });

    public ServiceCall MediaPause(EntityId entityId) =>
        new("media_player", "media_pause", new { entity_id = entityId });

    public ServiceCall MediaStop(EntityId entityId) => new("media_player", "media_stop", new { entity_id = entityId });

    public ServiceCall MediaNextTrack(EntityId entityId) =>
        new("media_player", "media_next_track", new { entity_id = entityId });

    public ServiceCall MediaPreviousTrack(EntityId entityId) =>
        new("media_player", "media_previous_track", new { entity_id = entityId });

    public ServiceCall MediaSeek(EntityId entityId, double seekPosition) =>
        new("media_player", "media_seek", new { entity_id = entityId, seek_position = seekPosition });

    public ServiceCall PlayMedia(EntityId entityId, string mediaContentId, string mediaContentType,
        string? enqueue = null, bool? announce = null) =>
        new("media_player", "play_media", new
        {
            entity_id = entityId,
            media_content_id = mediaContentId,
            media_content_type = mediaContentType,
            enqueue,
            announce,
        });

    public ServiceCall PlayMedia(EntityId entityId, string mediaContentId, string mediaContentType,
        MediaPlayerEnqueue enqueue, bool? announce = null) =>
        new("media_player", "play_media", new
        {
            entity_id = entityId,
            media_content_id = mediaContentId,
            media_content_type = mediaContentType,
            enqueue,
            announce,
        });

    public ServiceCall<Dictionary<EntityId, MediaBrowseItem>> BrowseMedia(EntityId entityId,
        string? mediaContentType = null,
        string? mediaContentId = null) =>
        new("media_player", "browse_media", new
        {
            entity_id = entityId,
            media_content_type = mediaContentType,
            media_content_id = mediaContentId,
        });

    public ServiceCall<Dictionary<EntityId, MediaSearchResult>> SearchMedia(EntityId entityId, string searchQuery,
        string? mediaContentType = null, string? mediaContentId = null, string[]? mediaFilterClasses = null) =>
        new("media_player", "search_media", new
        {
            entity_id = entityId,
            search_query = searchQuery,
            media_content_type = mediaContentType,
            media_content_id = mediaContentId,
            media_filter_classes = mediaFilterClasses,
        });

    public ServiceCall SelectSource(EntityId entityId, string source) =>
        new("media_player", "select_source", new { entity_id = entityId, source });

    public ServiceCall SelectSoundMode(EntityId entityId, string soundMode) =>
        new("media_player", "select_sound_mode", new { entity_id = entityId, sound_mode = soundMode });

    public ServiceCall ClearPlaylist(EntityId entityId) =>
        new("media_player", "clear_playlist", new { entity_id = entityId });

    public ServiceCall SetShuffle(EntityId entityId, bool shuffle) =>
        new("media_player", "shuffle_set", new { entity_id = entityId, shuffle });

    public ServiceCall SetRepeat(EntityId entityId, string repeat) =>
        new("media_player", "repeat_set", new { entity_id = entityId, repeat });

    public ServiceCall SetRepeat(EntityId entityId, MediaPlayerRepeatMode repeat) =>
        new("media_player", "repeat_set", new { entity_id = entityId, repeat });

    public ServiceCall Join(EntityId entityId, params EntityId[] groupMembers) =>
        new("media_player", "join", new { entity_id = entityId, group_members = groupMembers });

    public ServiceCall Unjoin(EntityId entityId) =>
        new("media_player", "unjoin", new { entity_id = entityId });
}

public class InputBooleanService
{
    public ServiceCall TurnOn(EntityId entityId) => new("input_boolean", "turn_on", new { entity_id = entityId });
    public ServiceCall TurnOff(EntityId entityId) => new("input_boolean", "turn_off", new { entity_id = entityId });
    public ServiceCall Toggle(EntityId entityId) => new("input_boolean", "toggle", new { entity_id = entityId });
    public ServiceCall Reload() => new("input_boolean", "reload", null);
}

public class InputNumberService
{
    public ServiceCall Decrement(EntityId entityId) =>
        new("input_number", "decrement", new { entity_id = entityId });

    public ServiceCall Increment(EntityId entityId) =>
        new("input_number", "increment", new { entity_id = entityId });

    public ServiceCall SetValue(EntityId entityId, double value) =>
        new("input_number", "set_value", new { entity_id = entityId, value });

    public ServiceCall Reload() => new("input_number", "reload", null);
}

public class InputSelectService
{
    public ServiceCall SelectNext(EntityId entityId, bool cycle = true) =>
        new("input_select", "select_next", new { entity_id = entityId, cycle });

    public ServiceCall SelectOption(EntityId entityId, string option) =>
        new("input_select", "select_option", new { entity_id = entityId, option });

    public ServiceCall SelectPrevious(EntityId entityId, bool cycle = true) =>
        new("input_select", "select_previous", new { entity_id = entityId, cycle });

    public ServiceCall SelectFirst(EntityId entityId) =>
        new("input_select", "select_first", new { entity_id = entityId });

    public ServiceCall SelectLast(EntityId entityId) =>
        new("input_select", "select_last", new { entity_id = entityId });

    public ServiceCall SetOptions(EntityId entityId, params string[] options) =>
        new("input_select", "set_options", new { entity_id = entityId, options });

    public ServiceCall Reload() => new("input_select", "reload", null);
}

public class CoverService
{
    public ServiceCall OpenCover(EntityId entityId, string? speed = null) =>
        new("cover", "open_cover", new { entity_id = entityId, speed });

    public ServiceCall CloseCover(EntityId entityId, string? speed = null) =>
        new("cover", "close_cover", new { entity_id = entityId, speed });

    public ServiceCall StopCover(EntityId entityId) => new("cover", "stop_cover", new { entity_id = entityId });

    public ServiceCall Toggle(EntityId entityId) => new("cover", "toggle", new { entity_id = entityId });

    public ServiceCall SetCoverPosition(EntityId entityId, int position, string? speed = null) =>
        new("cover", "set_cover_position", new { entity_id = entityId, position, speed });

    public ServiceCall OpenCoverTilt(EntityId entityId) =>
        new("cover", "open_cover_tilt", new { entity_id = entityId });

    public ServiceCall CloseCoverTilt(EntityId entityId) =>
        new("cover", "close_cover_tilt", new { entity_id = entityId });

    public ServiceCall ToggleCoverTilt(EntityId entityId) =>
        new("cover", "toggle_cover_tilt", new { entity_id = entityId });

    public ServiceCall SetCoverTiltPosition(EntityId entityId, int tiltPosition) =>
        new("cover", "set_cover_tilt_position", new { entity_id = entityId, tilt_position = tiltPosition });

    public ServiceCall StopCoverTilt(EntityId entityId) =>
        new("cover", "stop_cover_tilt", new { entity_id = entityId });
}

public class ClimateService
{
    public ServiceCall SetTemperature(EntityId entityId, double temperature, string? hvacMode = null) =>
        new("climate", "set_temperature", new
        {
            entity_id = entityId,
            temperature,
            hvac_mode = hvacMode,
        });

    public ServiceCall SetTemperatureRange(EntityId entityId, double targetTempLow, double targetTempHigh,
        string? hvacMode = null) =>
        new("climate", "set_temperature", new
        {
            entity_id = entityId,
            target_temp_low = targetTempLow,
            target_temp_high = targetTempHigh,
            hvac_mode = hvacMode,
        });

    public ServiceCall SetHvacMode(EntityId entityId, string hvacMode) => new("climate", "set_hvac_mode",
        new { entity_id = entityId, hvac_mode = hvacMode });

    public ServiceCall SetPresetMode(EntityId entityId, string presetMode) =>
        new("climate", "set_preset_mode", new { entity_id = entityId, preset_mode = presetMode });

    public ServiceCall SetHumidity(EntityId entityId, int humidity) =>
        new("climate", "set_humidity", new { entity_id = entityId, humidity });

    public ServiceCall SetFanMode(EntityId entityId, string fanMode) =>
        new("climate", "set_fan_mode", new { entity_id = entityId, fan_mode = fanMode });

    public ServiceCall SetSwingMode(EntityId entityId, string swingMode) =>
        new("climate", "set_swing_mode", new { entity_id = entityId, swing_mode = swingMode });

    public ServiceCall SetSwingHorizontalMode(EntityId entityId, string swingHorizontalMode) =>
        new("climate", "set_swing_horizontal_mode",
            new { entity_id = entityId, swing_horizontal_mode = swingHorizontalMode });

    public ServiceCall TurnOn(EntityId entityId) => new("climate", "turn_on", new { entity_id = entityId });
    public ServiceCall TurnOff(EntityId entityId) => new("climate", "turn_off", new { entity_id = entityId });
    public ServiceCall Toggle(EntityId entityId) => new("climate", "toggle", new { entity_id = entityId });
}

public class FanService
{
    public ServiceCall TurnOn(EntityId entityId, int? percentage = null, string? presetMode = null) =>
        new("fan", "turn_on", new
        {
            entity_id = entityId,
            percentage,
            preset_mode = presetMode,
        });

    public ServiceCall TurnOff(EntityId entityId) => new("fan", "turn_off", new { entity_id = entityId });

    public ServiceCall SetPercentage(EntityId entityId, int percentage) =>
        new("fan", "set_percentage", new { entity_id = entityId, percentage });

    public ServiceCall SetPresetMode(EntityId entityId, string presetMode) =>
        new("fan", "set_preset_mode", new { entity_id = entityId, preset_mode = presetMode });

    public ServiceCall Oscillate(EntityId entityId, bool oscillating) =>
        new("fan", "oscillate", new { entity_id = entityId, oscillating });

    public ServiceCall Toggle(EntityId entityId) => new("fan", "toggle", new { entity_id = entityId });

    public ServiceCall SetDirection(EntityId entityId, string direction) =>
        new("fan", "set_direction", new { entity_id = entityId, direction });

    public ServiceCall IncreaseSpeed(EntityId entityId, double? percentageStep = null) =>
        new("fan", "increase_speed", new { entity_id = entityId, percentage_step = percentageStep });

    public ServiceCall DecreaseSpeed(EntityId entityId, double? percentageStep = null) =>
        new("fan", "decrease_speed", new { entity_id = entityId, percentage_step = percentageStep });
}

public class SceneService
{
    public ServiceCall TurnOn(EntityId entityId, double? transition = null) =>
        new("scene", "turn_on", new { entity_id = entityId, transition });

    public ServiceCall Reload() => new("scene", "reload", null);

    public ServiceCall Apply(object entities, double? transition = null) =>
        new("scene", "apply", new { entities, transition });

    public ServiceCall Create(string sceneId, object? entities = null, EntityId[]? snapshotEntities = null) =>
        new("scene", "create", new { scene_id = sceneId, entities, snapshot_entities = snapshotEntities });

    public ServiceCall Delete(EntityId entityId) => new("scene", "delete", new { entity_id = entityId });
}

public class TtsService
{
    public ServiceCall Say(EntityId mediaPlayerEntityId, string message, bool cache = false,
        string? language = null, object? options = null) =>
        new("tts", "say", new
        {
            entity_id = mediaPlayerEntityId,
            message,
            cache,
            language,
            options,
        });

    public ServiceCall Speak(EntityId ttsEntityId, EntityId mediaPlayerEntityId, string message, bool cache = true,
        string? language = null, object? options = null) =>
        new("tts", "speak", new
        {
            entity_id = ttsEntityId,
            media_player_entity_id = mediaPlayerEntityId,
            message,
            cache,
            language,
            options,
        });

    public ServiceCall ClearCache() => new("tts", "clear_cache", null);
}

public sealed class AiTaskService
{
    public ServiceCall<AiTaskDataResult<TResult>> GenerateData<TResult>(string taskName, string instructions,
        EntityId? entityId = null, object? structure = null, string[]? attachments = null) =>
        new("ai_task", "generate_data", new
        {
            task_name = taskName,
            instructions,
            entity_id = entityId,
            structure,
            attachments,
        });

    public ServiceCall<AiTaskImageResult> GenerateImage(string taskName, string instructions, EntityId entityId,
        string[]? attachments = null) =>
        new("ai_task", "generate_image", new
        {
            task_name = taskName,
            instructions,
            entity_id = entityId,
            attachments,
        });
}

public sealed class AlarmControlPanelService
{
    public ServiceCall Disarm(EntityId entityId, string? code = null) => Call("alarm_disarm", entityId, code);

    public ServiceCall ArmCustomBypass(EntityId entityId, string? code = null) =>
        Call("alarm_arm_custom_bypass", entityId, code);

    public ServiceCall ArmHome(EntityId entityId, string? code = null) => Call("alarm_arm_home", entityId, code);
    public ServiceCall ArmAway(EntityId entityId, string? code = null) => Call("alarm_arm_away", entityId, code);
    public ServiceCall ArmNight(EntityId entityId, string? code = null) => Call("alarm_arm_night", entityId, code);

    public ServiceCall ArmVacation(EntityId entityId, string? code = null) =>
        Call("alarm_arm_vacation", entityId, code);

    public ServiceCall Trigger(EntityId entityId, string? code = null) => Call("alarm_trigger", entityId, code);

    static ServiceCall Call(string service, EntityId entityId, string? code) =>
        new("alarm_control_panel", service, new { entity_id = entityId, code });
}

public sealed class AlertService
{
    public ServiceCall TurnOn(EntityId entityId) => new("alert", "turn_on", new { entity_id = entityId });
    public ServiceCall TurnOff(EntityId entityId) => new("alert", "turn_off", new { entity_id = entityId });
    public ServiceCall Toggle(EntityId entityId) => new("alert", "toggle", new { entity_id = entityId });
}

public sealed class AssistSatelliteService
{
    public ServiceCall Announce(EntityId entityId, string message, bool preannounce = true,
        string? preannounceMediaId = null) =>
        new("assist_satellite", "announce", new
        {
            entity_id = entityId,
            message,
            preannounce,
            preannounce_media_id = preannounceMediaId,
        });

    public ServiceCall AnnounceMedia(EntityId entityId, string mediaId, bool preannounce = true,
        string? preannounceMediaId = null) =>
        new("assist_satellite", "announce", new
        {
            entity_id = entityId,
            media_id = mediaId,
            preannounce,
            preannounce_media_id = preannounceMediaId,
        });

    public ServiceCall StartConversation(EntityId entityId, string startMessage, string? extraSystemPrompt = null,
        bool preannounce = true, string? preannounceMediaId = null) =>
        new("assist_satellite", "start_conversation", new
        {
            entity_id = entityId,
            start_message = startMessage,
            extra_system_prompt = extraSystemPrompt,
            preannounce,
            preannounce_media_id = preannounceMediaId,
        });

    public ServiceCall StartConversationWithMedia(EntityId entityId, string startMediaId,
        string? extraSystemPrompt = null, bool preannounce = true, string? preannounceMediaId = null) =>
        new("assist_satellite", "start_conversation", new
        {
            entity_id = entityId,
            start_media_id = startMediaId,
            extra_system_prompt = extraSystemPrompt,
            preannounce,
            preannounce_media_id = preannounceMediaId,
        });

    public ServiceCall<AssistSatelliteAnswerResult<TSlots>> AskQuestion<TSlots>(EntityId entityId, string question,
        AssistSatelliteExpectedAnswer[]? answers = null, bool preannounce = true,
        string? preannounceMediaId = null) =>
        new("assist_satellite", "ask_question", new
        {
            entity_id = entityId,
            question,
            answers,
            preannounce,
            preannounce_media_id = preannounceMediaId,
        });

    public ServiceCall<AssistSatelliteAnswerResult<TSlots>> AskQuestionWithMedia<TSlots>(EntityId entityId,
        string questionMediaId, AssistSatelliteExpectedAnswer[]? answers = null,
        bool preannounce = true, string? preannounceMediaId = null) =>
        new("assist_satellite", "ask_question", new
        {
            entity_id = entityId,
            question_media_id = questionMediaId,
            answers,
            preannounce,
            preannounce_media_id = preannounceMediaId,
        });
}

public sealed class AutomationService
{
    public ServiceCall TurnOn(EntityId entityId) => new("automation", "turn_on", new { entity_id = entityId });

    public ServiceCall TurnOff(EntityId entityId, bool stopActions = true) =>
        new("automation", "turn_off", new { entity_id = entityId, stop_actions = stopActions });

    public ServiceCall Toggle(EntityId entityId) => new("automation", "toggle", new { entity_id = entityId });

    public ServiceCall Trigger(EntityId entityId, bool skipCondition = true) =>
        new("automation", "trigger", new { entity_id = entityId, skip_condition = skipCondition });

    public ServiceCall Reload() => new("automation", "reload", null);
}

public sealed class BackupService
{
    public ServiceCall Create() => new("backup", "create", null);
    public ServiceCall CreateAutomatic() => new("backup", "create_automatic", null);
}

public sealed class ButtonService
{
    public ServiceCall Press(EntityId entityId) => new("button", "press", new { entity_id = entityId });
}

public sealed class CalendarService
{
    public ServiceCall CreateEvent(EntityId entityId, string summary, DateTimeOffset start, DateTimeOffset end,
        string? description = null, string? location = null) =>
        new("calendar", "create_event", new
        {
            entity_id = entityId,
            summary,
            description,
            start_date_time = start,
            end_date_time = end,
            location,
        });

    public ServiceCall CreateAllDayEvent(EntityId entityId, string summary, DateOnly start, DateOnly end,
        string? description = null, string? location = null) =>
        new("calendar", "create_event", new
        {
            entity_id = entityId,
            summary,
            description,
            start_date = start,
            end_date = end,
            location,
        });

    public ServiceCall CreateEventIn(EntityId entityId, string summary, TimeSpan delay,
        string? description = null, string? location = null) =>
        new("calendar", "create_event", new
        {
            entity_id = entityId,
            summary,
            description,
            @in = delay,
            location,
        });

    public ServiceCall<Dictionary<EntityId, CalendarEventsResult>> GetEvents(EntityId entityId,
        DateTimeOffset end, DateTimeOffset? start = null) =>
        new("calendar", "get_events", new
        {
            entity_id = entityId,
            start_date_time = start,
            end_date_time = end,
        });

    public ServiceCall<Dictionary<EntityId, CalendarEventsResult>> GetEvents(EntityId entityId,
        TimeSpan duration, DateTimeOffset? start = null) =>
        new("calendar", "get_events", new
        {
            entity_id = entityId,
            start_date_time = start,
            duration,
        });
}

public sealed class CameraService
{
    public ServiceCall TurnOn(EntityId entityId) => Call("turn_on", entityId);
    public ServiceCall TurnOff(EntityId entityId) => Call("turn_off", entityId);
    public ServiceCall EnableMotionDetection(EntityId entityId) => Call("enable_motion_detection", entityId);
    public ServiceCall DisableMotionDetection(EntityId entityId) => Call("disable_motion_detection", entityId);

    public ServiceCall Snapshot(EntityId entityId, string filename) =>
        new("camera", "snapshot", new { entity_id = entityId, filename });

    public ServiceCall PlayStream(EntityId entityId, EntityId mediaPlayer, string format = "hls") =>
        new("camera", "play_stream", new { entity_id = entityId, media_player = mediaPlayer, format });

    public ServiceCall Record(EntityId entityId, string filename, int duration = 30, int lookback = 0) =>
        new("camera", "record", new { entity_id = entityId, filename, duration, lookback });

    static ServiceCall Call(string service, EntityId entityId) =>
        new("camera", service, new { entity_id = entityId });
}

public sealed class ConversationService
{
    public ServiceCall<ConversationResult<TExtraData, TSpeechSlots>> Process<TExtraData, TSpeechSlots>(string text,
        string? language = null, string? agentId = null, string? conversationId = null) =>
        new("conversation", "process", new
        {
            text,
            language,
            agent_id = agentId,
            conversation_id = conversationId,
        });

    public ServiceCall Reload(string? language = null, string? agentId = null) =>
        new("conversation", "reload", new { language, agent_id = agentId });
}

public sealed class CounterService
{
    public ServiceCall Increment(EntityId entityId) => Call("increment", entityId);
    public ServiceCall Decrement(EntityId entityId) => Call("decrement", entityId);
    public ServiceCall Reset(EntityId entityId) => Call("reset", entityId);

    public ServiceCall SetValue(EntityId entityId, int value) =>
        new("counter", "set_value", new { entity_id = entityId, value });

    static ServiceCall Call(string service, EntityId entityId) =>
        new("counter", service, new { entity_id = entityId });
}

public sealed class DateService
{
    public ServiceCall SetValue(EntityId entityId, DateOnly date) =>
        new("date", "set_value", new { entity_id = entityId, date });
}

public sealed class DateTimeEntityService
{
    public ServiceCall SetValue(EntityId entityId, DateTimeOffset dateTime) =>
        new("datetime", "set_value", new { entity_id = entityId, datetime = dateTime });
}

public sealed class DeviceTrackerService
{
    public ServiceCall See(string deviceId, string? mac = null, string? hostName = null,
        string? locationName = null, double[]? gps = null, int? gpsAccuracy = null, int? battery = null) =>
        new("device_tracker", "see", new
        {
            dev_id = deviceId,
            mac,
            host_name = hostName,
            location_name = locationName,
            gps,
            gps_accuracy = gpsAccuracy,
            battery,
        });
}

public sealed class HumidifierService
{
    public ServiceCall SetMode(EntityId entityId, string mode) =>
        new("humidifier", "set_mode", new { entity_id = entityId, mode });

    public ServiceCall SetHumidity(EntityId entityId, int humidity) =>
        new("humidifier", "set_humidity", new { entity_id = entityId, humidity });

    public ServiceCall TurnOn(EntityId entityId) => Call("turn_on", entityId);
    public ServiceCall TurnOff(EntityId entityId) => Call("turn_off", entityId);
    public ServiceCall Toggle(EntityId entityId) => Call("toggle", entityId);

    static ServiceCall Call(string service, EntityId entityId) =>
        new("humidifier", service, new { entity_id = entityId });
}

public sealed class ImageService
{
    public ServiceCall Snapshot(EntityId entityId, string filename) =>
        new("image", "snapshot", new { entity_id = entityId, filename });
}

public sealed class ImageProcessingService
{
    public ServiceCall Scan(EntityId entityId) =>
        new("image_processing", "scan", new { entity_id = entityId });
}

public sealed class InputButtonService
{
    public ServiceCall Press(EntityId entityId) => new("input_button", "press", new { entity_id = entityId });
    public ServiceCall Reload() => new("input_button", "reload", null);
}

public sealed class InputDateTimeService
{
    public ServiceCall SetDate(EntityId entityId, DateOnly date) =>
        new("input_datetime", "set_datetime", new { entity_id = entityId, date });

    public ServiceCall SetTime(EntityId entityId, TimeOnly time) =>
        new("input_datetime", "set_datetime", new { entity_id = entityId, time });

    public ServiceCall SetDateTime(EntityId entityId, DateTimeOffset dateTime) =>
        new("input_datetime", "set_datetime", new { entity_id = entityId, datetime = dateTime });

    public ServiceCall SetTimestamp(EntityId entityId, double timestamp) =>
        new("input_datetime", "set_datetime", new { entity_id = entityId, timestamp });

    public ServiceCall Reload() => new("input_datetime", "reload", null);
}

public sealed class InputTextService
{
    public ServiceCall SetValue(EntityId entityId, string value) =>
        new("input_text", "set_value", new { entity_id = entityId, value });

    public ServiceCall Reload() => new("input_text", "reload", null);
}

public sealed class LawnMowerService
{
    public ServiceCall StartMowing(EntityId entityId) => Call("start_mowing", entityId);
    public ServiceCall Dock(EntityId entityId) => Call("dock", entityId);
    public ServiceCall Pause(EntityId entityId) => Call("pause", entityId);

    static ServiceCall Call(string service, EntityId entityId) =>
        new("lawn_mower", service, new { entity_id = entityId });
}

public sealed class LockService
{
    public ServiceCall Lock(EntityId entityId, string? code = null) => Call("lock", entityId, code);
    public ServiceCall Unlock(EntityId entityId, string? code = null) => Call("unlock", entityId, code);
    public ServiceCall Open(EntityId entityId, string? code = null) => Call("open", entityId, code);

    static ServiceCall Call(string service, EntityId entityId, string? code) =>
        new("lock", service, new { entity_id = entityId, code });
}

public sealed class NumberService
{
    public ServiceCall SetValue(EntityId entityId, double value) =>
        new("number", "set_value", new { entity_id = entityId, value });
}

public sealed class RemoteService
{
    public ServiceCall TurnOn(EntityId entityId, string? activity = null) =>
        new("remote", "turn_on", new { entity_id = entityId, activity });

    public ServiceCall TurnOff(EntityId entityId) => Call("turn_off", entityId);
    public ServiceCall Toggle(EntityId entityId) => Call("toggle", entityId);

    public ServiceCall SendCommand(EntityId entityId, string device, string[] commands,
        int numRepeats = 1, double delaySeconds = 0.4, double? holdSeconds = null) =>
        new("remote", "send_command", new
        {
            entity_id = entityId,
            device,
            command = commands,
            num_repeats = numRepeats,
            delay_secs = delaySeconds,
            hold_secs = holdSeconds,
        });

    public ServiceCall LearnCommand(EntityId entityId, string device, string[] commands,
        RemoteCommandType commandType = RemoteCommandType.Ir, bool alternative = false, int timeout = 30) =>
        new("remote", "learn_command", new
        {
            entity_id = entityId,
            device,
            command = commands,
            command_type = commandType,
            alternative,
            timeout,
        });

    public ServiceCall DeleteCommand(EntityId entityId, string device, string[] commands) =>
        new("remote", "delete_command", new { entity_id = entityId, device, command = commands });

    static ServiceCall Call(string service, EntityId entityId) =>
        new("remote", service, new { entity_id = entityId });
}

public sealed class ScriptService
{
    public ServiceCall TurnOn(EntityId entityId) => Call("turn_on", entityId);
    public ServiceCall TurnOff(EntityId entityId) => Call("turn_off", entityId);
    public ServiceCall Toggle(EntityId entityId) => Call("toggle", entityId);
    public ServiceCall Reload() => new("script", "reload", null);

    static ServiceCall Call(string service, EntityId entityId) =>
        new("script", service, new { entity_id = entityId });
}

public sealed class SelectService
{
    public ServiceCall SelectFirst(EntityId entityId) => Call("select_first", entityId);
    public ServiceCall SelectLast(EntityId entityId) => Call("select_last", entityId);

    public ServiceCall SelectNext(EntityId entityId, bool cycle = true) =>
        new("select", "select_next", new { entity_id = entityId, cycle });

    public ServiceCall SelectOption(EntityId entityId, string option) =>
        new("select", "select_option", new { entity_id = entityId, option });

    public ServiceCall SelectPrevious(EntityId entityId, bool cycle = true) =>
        new("select", "select_previous", new { entity_id = entityId, cycle });

    static ServiceCall Call(string service, EntityId entityId) =>
        new("select", service, new { entity_id = entityId });
}

public sealed class SirenService
{
    public ServiceCall TurnOn(EntityId entityId, string? tone = null, double? volumeLevel = null,
        TimeSpan? duration = null) =>
        new("siren", "turn_on", new
        {
            entity_id = entityId,
            tone,
            volume_level = volumeLevel,
            duration,
        });

    public ServiceCall TurnOff(EntityId entityId) => new("siren", "turn_off", new { entity_id = entityId });
    public ServiceCall Toggle(EntityId entityId) => new("siren", "toggle", new { entity_id = entityId });
}

public sealed class TextService
{
    public ServiceCall SetValue(EntityId entityId, string value) =>
        new("text", "set_value", new { entity_id = entityId, value });
}

public sealed class TimeService
{
    public ServiceCall SetValue(EntityId entityId, TimeOnly time) =>
        new("time", "set_value", new { entity_id = entityId, time });
}

public sealed class TimerService
{
    public ServiceCall Start(EntityId entityId, TimeSpan? duration = null) =>
        new("timer", "start", new { entity_id = entityId, duration });

    public ServiceCall Pause(EntityId entityId) => Call("pause", entityId);
    public ServiceCall Cancel(EntityId entityId) => Call("cancel", entityId);
    public ServiceCall Finish(EntityId entityId) => Call("finish", entityId);

    public ServiceCall Change(EntityId entityId, TimeSpan duration) =>
        new("timer", "change", new { entity_id = entityId, duration });

    public ServiceCall Reload() => new("timer", "reload", null);

    static ServiceCall Call(string service, EntityId entityId) =>
        new("timer", service, new { entity_id = entityId });
}

public sealed class TodoService
{
    public ServiceCall<Dictionary<EntityId, TodoItemsResult>> GetItems(EntityId entityId,
        params TodoItemStatus[] statuses) =>
        new("todo", "get_items", new
        {
            entity_id = entityId,
            status = statuses.Length == 0 ? null : statuses,
        });

    public ServiceCall<Dictionary<EntityId, TodoItemsResult>> GetAllTodos(params TodoItemStatus[] statuses) =>
        new("todo", "get_items", new
        {
            status = statuses.Length == 0 ? null : statuses,
        });


    public ServiceCall AddItem(EntityId entityId, string item, string? description = null) =>
        new("todo", "add_item", new { entity_id = entityId, item, description });

    public ServiceCall AddItem(EntityId entityId, string item, DateOnly dueDate, string? description = null) =>
        new("todo", "add_item", new
        {
            entity_id = entityId,
            item,
            due_date = dueDate,
            description,
        });

    public ServiceCall AddItem(EntityId entityId, string item, DateTimeOffset dueDateTime,
        string? description = null) =>
        new("todo", "add_item", new
        {
            entity_id = entityId,
            item,
            due_datetime = dueDateTime,
            description,
        });

    public ServiceCall UpdateItem(EntityId entityId, string item, string? rename = null,
        TodoItemStatus? status = null, DateOnly? dueDate = null, DateTimeOffset? dueDateTime = null,
        string? description = null) =>
        new("todo", "update_item", new
        {
            entity_id = entityId,
            item,
            rename,
            status,
            due_date = dueDate,
            due_datetime = dueDateTime,
            description,
        });

    public ServiceCall RemoveItem(EntityId entityId, params string[] items) =>
        new("todo", "remove_item", new { entity_id = entityId, item = items });

    public ServiceCall RemoveCompletedItems(EntityId entityId) =>
        new("todo", "remove_completed_items", new { entity_id = entityId });
}

public sealed class UpdateService
{
    public ServiceCall Install(EntityId entityId, string? version = null, bool backup = false) =>
        new("update", "install", new { entity_id = entityId, version, backup });

    public ServiceCall Skip(EntityId entityId) => Call("skip", entityId);
    public ServiceCall ClearSkipped(EntityId entityId) => Call("clear_skipped", entityId);

    static ServiceCall Call(string service, EntityId entityId) =>
        new("update", service, new { entity_id = entityId });
}

public sealed class VacuumService
{
    public ServiceCall TurnOn(EntityId entityId) => Call("turn_on", entityId);
    public ServiceCall TurnOff(EntityId entityId) => Call("turn_off", entityId);
    public ServiceCall Toggle(EntityId entityId) => Call("toggle", entityId);
    public ServiceCall Stop(EntityId entityId) => Call("stop", entityId);
    public ServiceCall Locate(EntityId entityId) => Call("locate", entityId);
    public ServiceCall StartPause(EntityId entityId) => Call("start_pause", entityId);
    public ServiceCall Start(EntityId entityId) => Call("start", entityId);
    public ServiceCall Pause(EntityId entityId) => Call("pause", entityId);
    public ServiceCall ReturnToBase(EntityId entityId) => Call("return_to_base", entityId);
    public ServiceCall CleanSpot(EntityId entityId) => Call("clean_spot", entityId);

    public ServiceCall SendCommand<TParams>(EntityId entityId, string command, TParams? parameters = default) =>
        new("vacuum", "send_command", new { entity_id = entityId, command, @params = parameters });

    public ServiceCall SetFanSpeed(EntityId entityId, string fanSpeed) =>
        new("vacuum", "set_fan_speed", new { entity_id = entityId, fan_speed = fanSpeed });

    static ServiceCall Call(string service, EntityId entityId) =>
        new("vacuum", service, new { entity_id = entityId });
}

public sealed class ValveService
{
    public ServiceCall Open(EntityId entityId) => Call("open_valve", entityId);
    public ServiceCall Close(EntityId entityId) => Call("close_valve", entityId);
    public ServiceCall Toggle(EntityId entityId) => Call("toggle", entityId);

    public ServiceCall SetPosition(EntityId entityId, int position) =>
        new("valve", "set_valve_position", new { entity_id = entityId, position });

    public ServiceCall Stop(EntityId entityId) => Call("stop_valve", entityId);

    static ServiceCall Call(string service, EntityId entityId) =>
        new("valve", service, new { entity_id = entityId });
}

public sealed class WaterHeaterService
{
    public ServiceCall SetAwayMode(EntityId entityId, bool awayMode) =>
        new("water_heater", "set_away_mode", new { entity_id = entityId, away_mode = awayMode });

    public ServiceCall SetTemperature(EntityId entityId, double temperature, string? operationMode = null) =>
        new("water_heater", "set_temperature", new
        {
            entity_id = entityId,
            temperature,
            operation_mode = operationMode,
        });

    public ServiceCall SetOperationMode(EntityId entityId, string operationMode) =>
        new("water_heater", "set_operation_mode", new
        {
            entity_id = entityId,
            operation_mode = operationMode,
        });

    public ServiceCall TurnOn(EntityId entityId) => new("water_heater", "turn_on", new { entity_id = entityId });
    public ServiceCall TurnOff(EntityId entityId) => new("water_heater", "turn_off", new { entity_id = entityId });
}

public sealed class WeatherService
{
    public ServiceCall<Dictionary<EntityId, WeatherForecastResult>> GetForecasts(EntityId entityId,
        WeatherForecastType type) =>
        new("weather", "get_forecasts", new { entity_id = entityId, type });
}

public sealed class FrontendService
{
    public ServiceCall SetTheme(string name, ThemeMode? mode = null) =>
        new("frontend", "set_theme", new { name, mode });

    public ServiceCall ReloadThemes() => new("frontend", "reload_themes", null);
}

public sealed class GroupService
{
    public ServiceCall Reload() => new("group", "reload", null);

    public ServiceCall Set(string objectId, EntityId[]? entities = null, string? name = null,
        string? icon = null, EntityId[]? addEntities = null,
        EntityId[]? removeEntities = null, bool? all = null) =>
        new("group", "set", new
        {
            object_id = objectId,
            name,
            icon,
            entities,
            add_entities = addEntities,
            remove_entities = removeEntities,
            all,
        });

    public ServiceCall Remove(string objectId) => new("group", "remove", new { object_id = objectId });
}

public sealed class HomeAssistantService
{
    public ServiceCall CheckConfig() => new("homeassistant", "check_config", null);
    public ServiceCall ReloadCoreConfig() => new("homeassistant", "reload_core_config", null);
    public ServiceCall Restart() => new("homeassistant", "restart", null);
    public ServiceCall Stop() => new("homeassistant", "stop", null);
    public ServiceCall ReloadCustomTemplates() => new("homeassistant", "reload_custom_templates", null);
    public ServiceCall SavePersistentStates() => new("homeassistant", "save_persistent_states", null);
    public ServiceCall ReloadAll() => new("homeassistant", "reload_all", null);

    public ServiceCall SetLocation(double latitude, double longitude, double? elevation = null) =>
        new("homeassistant", "set_location", new { latitude, longitude, elevation });

    public ServiceCall TurnOn(params EntityId[] entityIds) => Call("turn_on", entityIds);
    public ServiceCall TurnOff(params EntityId[] entityIds) => Call("turn_off", entityIds);
    public ServiceCall Toggle(params EntityId[] entityIds) => Call("toggle", entityIds);

    public ServiceCall UpdateEntity(params EntityId[] entityIds) =>
        new("homeassistant", "update_entity", new { entity_id = entityIds });

    public ServiceCall ReloadConfigEntry(string? entryId = null) =>
        new("homeassistant", "reload_config_entry", new { entry_id = entryId });

    static ServiceCall Call(string service, EntityId[] entityIds) =>
        new("homeassistant", service, new { entity_id = entityIds });
}

public sealed class LogbookService
{
    public ServiceCall Log(string name, string message, EntityId? entityId = null, string? domain = null) =>
        new("logbook", "log", new { name, message, entity_id = entityId, domain });
}

public sealed class LoggerService
{
    public ServiceCall SetDefaultLevel(string level) =>
        new("logger", "set_default_level", new { level });

    public ServiceCall SetLevel(IReadOnlyDictionary<string, string> levels) =>
        new("logger", "set_level", levels);
}

public sealed class PersistentNotificationService
{
    public ServiceCall Create(string message, string? title = null, string? notificationId = null) =>
        new("persistent_notification", "create", new
        {
            message,
            title,
            notification_id = notificationId,
        });

    public ServiceCall Dismiss(string notificationId) =>
        new("persistent_notification", "dismiss", new { notification_id = notificationId });

    public ServiceCall DismissAll() => new("persistent_notification", "dismiss_all", null);
}

public sealed class RecorderService
{
    public ServiceCall Purge(int? keepDays = null, bool repack = false, bool applyFilter = false) =>
        new("recorder", "purge", new { keep_days = keepDays, repack, apply_filter = applyFilter });

    public ServiceCall PurgeEntities(EntityId[]? entityIds = null,
        string[]? domains = null, string[]? entityGlobs = null, int keepDays = 0) =>
        new("recorder", "purge_entities", new
        {
            entity_id = entityIds,
            domains,
            entity_globs = entityGlobs,
            keep_days = keepDays,
        });

    public ServiceCall Disable() => new("recorder", "disable", null);
    public ServiceCall Enable() => new("recorder", "enable", null);

    public ServiceCall<RecorderStatisticsResult> GetStatistics(DateTimeOffset startTime,
        string[] statisticIds, RecorderStatisticPeriod period,
        RecorderStatisticType[] types, DateTimeOffset? endTime = null,
        IReadOnlyDictionary<string, string>? units = null) =>
        new("recorder", "get_statistics", new
        {
            start_time = startTime,
            end_time = endTime,
            statistic_ids = statisticIds,
            period = period == RecorderStatisticPeriod.FiveMinute
                ? "5minute"
                : JsonNamingPolicy.SnakeCaseLower.ConvertName(period.ToString()),
            types,
            units,
        });
}

public sealed class ScheduleService
{
    public ServiceCall Reload() => new("schedule", "reload", null);

    public ServiceCall<Dictionary<EntityId, Dictionary<string, ScheduleTimeRange<TData>[]>>>
        GetSchedule<TData>(EntityId entityId) =>
        new("schedule", "get_schedule", new { entity_id = entityId });
}

public sealed class ShoppingListService
{
    public ServiceCall AddItem(string name) => Call("add_item", name);
    public ServiceCall RemoveItem(string name) => Call("remove_item", name);
    public ServiceCall CompleteItem(string name) => Call("complete_item", name);
    public ServiceCall IncompleteItem(string name) => Call("incomplete_item", name);
    public ServiceCall CompleteAll() => new("shopping_list", "complete_all", null);
    public ServiceCall IncompleteAll() => new("shopping_list", "incomplete_all", null);
    public ServiceCall ClearCompletedItems() => new("shopping_list", "clear_completed_items", null);
    public ServiceCall Sort(bool reverse = false) => new("shopping_list", "sort", new { reverse });

    static ServiceCall Call(string service, string name) =>
        new("shopping_list", service, new { name });
}

public sealed class SystemLogService
{
    public ServiceCall Clear() => new("system_log", "clear", null);

    public ServiceCall Write(string message, string level = "error", string? logger = null) =>
        new("system_log", "write", new { message, level, logger });
}

public sealed class UtilityMeterService
{
    public ServiceCall Reset(EntityId entityId) =>
        new("utility_meter", "reset", new { entity_id = entityId });

    public ServiceCall Calibrate(EntityId entityId, double value) =>
        new("utility_meter", "calibrate", new { entity_id = entityId, value });
}

public sealed class ReloadOnlyService
{
    readonly string _domain;

    internal ReloadOnlyService(string domain) => _domain = domain;

    public ServiceCall Reload() => new(_domain, "reload", null);
}

public sealed class FfmpegService
{
    public ServiceCall Start(EntityId entityId) => Call("start", entityId);
    public ServiceCall Stop(EntityId entityId) => Call("stop", entityId);
    public ServiceCall Restart(EntityId entityId) => Call("restart", entityId);

    static ServiceCall Call(string service, EntityId entityId) =>
        new("ffmpeg", service, new { entity_id = entityId });
}

public sealed class GoogleAssistantService
{
    public ServiceCall RequestSync(string agentUserId) =>
        new("google_assistant", "request_sync", new { agent_user_id = agentUserId });
}

public sealed class LovelaceService
{
    public ServiceCall ReloadResources() => new("lovelace", "reload_resources", null);
}

public sealed class CloudService
{
    public ServiceCall RemoteConnect() => new("cloud", "remote_connect", null);
    public ServiceCall RemoteDisconnect() => new("cloud", "remote_disconnect", null);
}
