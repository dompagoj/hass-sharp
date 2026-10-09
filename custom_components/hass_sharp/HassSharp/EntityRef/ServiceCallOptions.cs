using System.Text.Json;
using System.Text.Json.Serialization;

namespace HassSharp;

public enum MediaPlayerEnqueue
{
    Play,
    Next,
    Add,
    Replace,
}

public enum MediaPlayerRepeatMode
{
    Off,
    One,
    All,
}

public enum RemoteCommandType
{
    Ir,
    Rf,
}

public enum ThemeMode
{
    Light,
    Dark,
}

public enum TodoItemStatus
{
    NeedsAction,
    Completed,
}

public enum WeatherForecastType
{
    Daily,
    Hourly,
    TwiceDaily,
}

public enum RecorderStatisticPeriod
{
    FiveMinute,
    Hour,
    Day,
    Week,
    Month,
}

public enum RecorderStatisticType
{
    Change,
    LastReset,
    Max,
    Mean,
    Min,
    State,
    Sum,
}

public class LightOnOpts
{
    public string[] EntityId { get; set; } = null!;
    public double? Transition { get; init; }
    public int[]? RgbColor { get; init; }
    public int? BrightnessPct { get; init; }
    public double? BrightnessStepPct { get; init; }
    public int? ColorTempKelvin { get; init; }
    public string? Effect { get; init; }
    public int[]? RgbwColor { get; init; }
    public int[]? RgbwwColor { get; init; }
    public string? ColorName { get; init; }
    public double[]? HsColor { get; init; }
    public double[]? XyColor { get; init; }
    public int? ColorTemp { get; init; }
    public int? Brightness { get; init; }
    public int? BrightnessStep { get; init; }
    public bool? White { get; init; }
    public string? Profile { get; init; }
    public string? Flash { get; init; }
}

public class LightOffOpts
{
    public string[] EntityId { get; set; } = null!;
    public double? Transition { get; init; }
    public string? Flash { get; init; }
}

public sealed class MediaBrowseItem
{
    public required string Title { get; init; }
    public required string MediaClass { get; init; }
    public required string MediaContentType { get; init; }
    public required string MediaContentId { get; init; }
    public string? ChildrenMediaClass { get; init; }
    public bool CanPlay { get; init; }
    public bool CanExpand { get; init; }
    public bool CanSearch { get; init; }
    public string? Thumbnail { get; init; }
    public int NotShown { get; init; }
    public MediaBrowseItem[] Children { get; init; } = [];
}

public sealed class MediaSearchResult
{
    public MediaBrowseItem[] Result { get; init; } = [];
}

public sealed class CalendarEventsResult
{
    public CalendarEventResult[] Events { get; init; } = [];
}

public sealed class CalendarEventResult
{
    public required string Start { get; init; }
    public required string End { get; init; }
    public required string Summary { get; init; }
    public string? Description { get; init; }
    public string? Location { get; init; }
    public string? Uid { get; init; }
    public string? RecurrenceId { get; init; }
    public string? Rrule { get; init; }
}

public sealed class TodoItemsResult
{
    public TodoItemResult[] Items { get; init; } = [];
}

public sealed class TodoItemResult
{
    public string? Summary { get; init; }
    public string? Uid { get; init; }
    public TodoItemStatus? Status { get; init; }
    public string? Due { get; init; }
    public string? Description { get; init; }
    public string? Completed { get; init; }
}

public sealed class WeatherForecastResult
{
    public WeatherForecast[] Forecast { get; init; } = [];
}

public sealed class WeatherForecast
{
    public required string Datetime { get; init; }
    public string? Condition { get; init; }
    public double? Humidity { get; init; }
    public int? PrecipitationProbability { get; init; }
    public int? CloudCoverage { get; init; }
    public double? Precipitation { get; init; }
    public double? Pressure { get; init; }
    public double? Temperature { get; init; }
    public double? Templow { get; init; }
    public double? ApparentTemperature { get; init; }
    public double? WindGustSpeed { get; init; }
    public double? WindSpeed { get; init; }
    public double? DewPoint { get; init; }
    public double? UvIndex { get; init; }
    public bool? IsDaytime { get; init; }
    public WeatherWindBearing? WindBearing { get; init; }
}

[JsonConverter(typeof(WeatherWindBearingJsonConverter))]
public readonly record struct WeatherWindBearing
{
    public double? Degrees { get; }
    public string? Direction { get; }

    public WeatherWindBearing(double degrees) => Degrees = degrees;
    public WeatherWindBearing(string direction) => Direction = direction;
}

public sealed class WeatherWindBearingJsonConverter : JsonConverter<WeatherWindBearing>
{
    public override WeatherWindBearing Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number => new(reader.GetDouble()),
            JsonTokenType.String => new(reader.GetString()!),
            _ => throw new JsonException("Weather wind bearing must be a number or string."),
        };

    public override void Write(Utf8JsonWriter writer, WeatherWindBearing value, JsonSerializerOptions options)
    {
        if (value.Degrees is { } degrees)
            writer.WriteNumberValue(degrees);
        else
            writer.WriteStringValue(value.Direction);
    }
}

public sealed class RecorderStatisticsResult
{
    public IReadOnlyDictionary<string, RecorderStatistic[]> Statistics { get; init; } =
        new Dictionary<string, RecorderStatistic[]>();
}

public sealed class RecorderStatistic
{
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public DateTimeOffset? LastReset { get; init; }
    public double? State { get; init; }
    public double? Sum { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Mean { get; init; }
    public double? Change { get; init; }
}

public sealed class ScheduleTimeRange<TData>
{
    public required TimeOnly From { get; init; }
    public required TimeOnly To { get; init; }
    public TData? Data { get; init; }
}

public sealed class AiTaskDataResult<TData>
{
    public required string ConversationId { get; init; }
    public required TData Data { get; init; }
}

public sealed class AiTaskImageResult
{
    public required byte[] ImageData { get; init; }
    public required string ConversationId { get; init; }
    public required string MimeType { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public string? Model { get; init; }
    public string? RevisedPrompt { get; init; }
}

public sealed class AssistSatelliteAnswerResult<TSlots>
{
    public string? Id { get; init; }
    public required string Sentence { get; init; }
    public TSlots? Slots { get; init; }
}

public sealed class AssistSatelliteExpectedAnswer
{
    public required string Id { get; init; }
    public required string[] Sentences { get; init; }
}

public sealed class ConversationResult<TExtraData, TSpeechSlots>
{
    public required ConversationIntentResponse<TExtraData, TSpeechSlots> Response { get; init; }
    public string? ConversationId { get; init; }
    public bool ContinueConversation { get; init; }
}

public sealed class ConversationIntentResponse<TExtraData, TSpeechSlots>
{
    public IReadOnlyDictionary<string, ConversationSpeech<TExtraData>> Speech { get; init; } =
        new Dictionary<string, ConversationSpeech<TExtraData>>();

    public IReadOnlyDictionary<string, ConversationSpeech<TExtraData>> Reprompt { get; init; } =
        new Dictionary<string, ConversationSpeech<TExtraData>>();

    public IReadOnlyDictionary<string, ConversationCard> Card { get; init; } =
        new Dictionary<string, ConversationCard>();

    public required string Language { get; init; }
    public required string ResponseType { get; init; }
    public required ConversationResponseData Data { get; init; }
    public TSpeechSlots? SpeechSlots { get; init; }
}

public sealed class ConversationSpeech<TExtraData>
{
    public string? Speech { get; init; }
    public string? Reprompt { get; init; }
    public TExtraData? ExtraData { get; init; }
}

public sealed class ConversationCard
{
    public required string Title { get; init; }
    public required string Content { get; init; }
}

public sealed class ConversationResponseData
{
    public string? Code { get; init; }
    public ConversationTarget[] Targets { get; init; } = [];
    public ConversationTarget[] Success { get; init; } = [];
    public ConversationTarget[] Failed { get; init; } = [];
}

public sealed class ConversationTarget
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public string? Id { get; init; }
}
