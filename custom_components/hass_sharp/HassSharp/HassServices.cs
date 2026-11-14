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
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>Completes when Home Assistant has run the service; faults with its error.</summary>
    internal static Task CallServiceAsync(string domain, string service, JsonObject data)
    {
        var json = JsonSerializer.Serialize(data, JsonOptions);
        Logger.Debug($"Calling service (async): {domain}.{service} with data: {json}");

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
    internal static Task<TResult?> CallServiceAsync<TResult>(string domain, string service, JsonObject data)
    {
        var json = JsonSerializer.Serialize(data, JsonOptions);
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
