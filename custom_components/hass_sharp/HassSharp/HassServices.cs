using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace HassSharp;

/// <summary>
/// The one way a service call reaches Home Assistant: the Call service step and <see cref="NodeContext.CallService"/>
/// (and with it every generated <c>context.Services</c> method) end here.
/// </summary>
static class HassServices
{
    // Only for JSON nodes and framework types: each load context's typed service calls write their values with a copy of it
    // (ServiceData.Options), so that its caches never hold an automation's own types. An entity enum is written as Home
    // Assistant's text (the converter holds nothing itself), any other enum by its snake_case name.
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new VisualRuntime.EntityTextJsonConverter(), new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>
    /// Puts the target into the payload (a string is an entity_id, an object is flattened) and splits the service id,
    /// for the Call service step and a custom device's <c>CallService</c> alike. A target key that a field already set
    /// is an error, as the validator reports, never a silent overwrite.
    /// </summary>
    /// <param name="targetLabel">What an error calls a target that is neither an entity id nor an object.</param>
    internal static (string Domain, string Name) PrepareCall(string service, JsonObject payload, JsonNode? target, string targetLabel)
    {
        if (target != null)
        {
            // A target object holds only the five target keys (the validators say the same of a target value); a field
            // with one of them is a validation error, so the collision check below is only a defence.
            void Set(string name, JsonNode? value)
            {
                if (payload.ContainsKey(name)) throw new InvalidOperationException($"{service} sets {name} both as a field and in the target");
                payload[name] = value;
            }
            if (target is JsonValue entity && entity.TryGetValue<string>(out var entityId)) Set("entity_id", entityId);
            else if (target is JsonObject properties)
                foreach (var (key, value) in properties)
                {
                    if (!VisualGraphValidator.TargetKeys.Contains(key)) throw new InvalidOperationException(VisualGraphValidator.TargetKeyMessage(key));
                    Set(key, value?.DeepClone());
                }
            else throw new InvalidOperationException($"{targetLabel} requires an entity id or a target object.");
        }
        if (service.Split('.') is not [{ Length: > 0 } domain, { Length: > 0 } name]) throw new InvalidOperationException($"Service {service} is not a service id such as light.turn_on.");
        return (domain, name);
    }

    /// <summary>
    /// The data as the debug log writes it: redacted as traces are (<see cref="TraceJson.Redact"/>), and each
    /// <paramref name="hidden"/> key's value written as <c>***</c> whatever its name.
    /// </summary>
    static string Logged(JsonObject data, IReadOnlyCollection<string>? hidden)
    {
        var shown = TraceJson.Redact(data)!;
        if (hidden is { Count: > 0 } && shown is JsonObject fields)
            foreach (var key in hidden) if (fields.ContainsKey(key)) fields[key] = TraceJson.Redacted;
        return shown.ToJsonString(JsonOptions);
    }

    /// <summary>Completes when Home Assistant has run the service; faults with its error.</summary>
    /// <param name="hidden">Keys of <paramref name="data"/> whose values are secret (a secret setting's, or worked out from one).</param>
    internal static Task CallServiceAsync(string domain, string service, JsonObject data, IReadOnlyCollection<string>? hidden = null)
    {
        var json = JsonSerializer.Serialize(data, JsonOptions);
        // Redacted as traces are: the debug log is no place for a lock's code or a token.
        if (Logger.Level <= PyLogLevel.Debug) Logger.Debug($"Calling service (async): {domain}.{service} with data: {Logged(data, hidden)}");

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PyInterop.CallService(
            domain,
            service,
            json,
            () => tcs.TrySetResult(),
            error => tcs.TrySetException(new InvalidOperationException(error))
        );

        return tcs.Task;
    }

    /// <summary>Asks for the service's response; completes with it once Home Assistant has run the service.</summary>
    /// <param name="hidden">Keys of <paramref name="data"/> whose values are secret (a secret setting's, or worked out from one).</param>
    internal static Task<TResult?> CallServiceAsync<TResult>(string domain, string service, JsonObject data, IReadOnlyCollection<string>? hidden = null)
    {
        var json = JsonSerializer.Serialize(data, JsonOptions);
        // Redacted as traces are: the debug log is no place for a lock's code or a token.
        if (Logger.Level <= PyLogLevel.Debug) Logger.Debug($"Calling service for response (async): {domain}.{service} with data: {Logged(data, hidden)}");

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
                        : JsonSerializer.Deserialize<TResult>(responseJson, JsonOptions);
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
}
