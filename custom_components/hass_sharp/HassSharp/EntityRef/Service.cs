using System.Text.Json;

namespace HassSharp;

public readonly ref struct ServiceCall
{
    private readonly string _domain;
    private readonly string _service;
    private readonly object? _data;

    public ServiceCall(string domain, string service, object? data)
    {
        _domain = domain;
        _service = service;
        _data = data;
    }

    public void Run() => HassServices.CallService(_domain, _service, _data);

    public Task RunAsync() => HassServices.CallServiceAsync(_domain, _service, _data);
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

    static JsonSerializerOptions _jsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
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
}

public class InputBooleanService
{
    public ServiceCall TurnOn(EntityId entityId) => new("input_boolean", "turn_on", new { entity_id = entityId });
    public ServiceCall TurnOff(EntityId entityId) => new("input_boolean", "turn_off", new { entity_id = entityId });
    public ServiceCall Toggle(EntityId entityId) => new("input_boolean", "toggle", new { entity_id = entityId });
}

public class InputNumberService
{
    public ServiceCall SetValue(EntityId entityId, double value) =>
        new("input_number", "set_value", new { entity_id = entityId, value });
}

public class InputSelectService
{
    public ServiceCall SelectOption(EntityId entityId, string option) =>
        new("input_select", "select_option", new { entity_id = entityId, option });
}

public class CoverService
{
    public ServiceCall OpenCover(EntityId entityId) => new("cover", "open_cover", new { entity_id = entityId });
    public ServiceCall CloseCover(EntityId entityId) => new("cover", "close_cover", new { entity_id = entityId });
    public ServiceCall StopCover(EntityId entityId) => new("cover", "stop_cover", new { entity_id = entityId });

    public ServiceCall SetCoverPosition(EntityId entityId, int position) =>
        new("cover", "set_cover_position", new { entity_id = entityId, position });
}

public class ClimateService
{
    public ServiceCall SetTemperature(EntityId entityId, double temperature) =>
        new("climate", "set_temperature", new { entity_id = entityId, temperature });

    public ServiceCall SetHvacMode(EntityId entityId, string hvacMode) => new("climate", "set_hvac_mode",
        new { entity_id = entityId, hvac_mode = hvacMode });
}

public class FanService
{
    public ServiceCall TurnOn(EntityId entityId) => new("fan", "turn_on", new { entity_id = entityId });
    public ServiceCall TurnOff(EntityId entityId) => new("fan", "turn_off", new { entity_id = entityId });

    public ServiceCall SetPercentage(EntityId entityId, int percentage) =>
        new("fan", "set_percentage", new { entity_id = entityId, percentage });
}

public class SceneService
{
    public ServiceCall TurnOn(EntityId entityId) => new("scene", "turn_on", new { entity_id = entityId });
}
