using System.Text.Json;

namespace HassSharp;

public class HassServices
{
    static HassServices? _instance;

    public NotificationService Notification = new();

    public static HassServices Instance()
    {
        if (_instance != null) return _instance;

        _instance = new();
        return _instance;
    }

    public static void CallService<T>(string domain, string service, T? data = default)
    {
        var json = data != null ? JsonSerializer.Serialize(data) : null;
        Logger.Debug($"Calling service: {domain}.{service} with data: {json}");
        PyInterop.CallService(domain, service, json);
    }
}

public class NotificationService
{
    public class MobileNotificationOpts
    {
        public required string Title { get; init; }
        public required string Message { get; init; }
    }

    public void CreateMobileNotification(string deviceName, MobileNotificationOpts opts)
    {
        HassServices.CallService("notify", deviceName, opts);
    }
}

public class LightService
{
    public void TurnOn(EntityId entityId) => TurnOn([entityId]);

    public void TurnOn(params ReadOnlySpan<EntityId> entities)
    {
    }

    public void TurnOff(EntityId entityId) => TurnOff([entityId]);

    public void TurnOff(ReadOnlySpan<EntityId> entityId)
    {
    }
}
