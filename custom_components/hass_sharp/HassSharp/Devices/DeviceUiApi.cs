using System.Text.Json;
using System.Text.Json.Nodes;

namespace HassSharp;

public partial class HassSharpManager
{
    // The host of the definition while it runs: none while it sets up, once its start failed, or once it stops.
    DefinitionHost? UiHost(string id) => _userScriptManager.Entities.Host(id) is { IsRunning: true } host ? host : null;

    const string UiNotRunning = "The entity definition is not running";

    /// <summary>
    /// The panel's JSON API for the pages of running entity definitions' C# nodes and for their stored data, answered in
    /// the envelope <see cref="EntityRequest"/> uses: <c>read</c> (a definition's pages, or that they are unchanged since
    /// <c>knownRevision</c>), <c>image</c> (a current card's JPEG, base64), <c>act</c> (runs a press, see
    /// <see cref="DefinitionHost.UiAct"/>), <c>counts</c> (the cards waiting for the user on each running definition's
    /// nodes, and the nodes that hold data), <c>stores</c> and <c>delete_store</c> (the shared stores). No operation takes
    /// the authoring gate, reads a definition file or queries run history, so none waits behind a save or a compile:
    /// <c>read</c>, <c>image</c>, <c>act</c> and <c>counts</c> answer from the running hosts and what the data stores
    /// keep in memory. A definition that is off, failed or still starting is not running. Nothing of a request, a page
    /// or a store is logged.
    /// </summary>
    /// <param name="requestJson">The request: <c>operation</c> and that operation's fields.</param>
    /// <returns><c>{version, ok: true, result}</c>, or <c>{version, ok: false, error: {code, detail}}</c>.</returns>
    public async Task<string> DeviceUiRequest(string requestJson)
    {
        try
        {
            var request = ApiRequest.Parse(requestJson);
            var operation = ApiRequest.String(request, "operation");
            // Before any field an operation reads: an unknown one is answered as such, not as a field it lacks.
            if (operation is not ("read" or "image" or "act" or "counts" or "stores" or "delete_store")) throw new ArgumentException("Unknown device page operation");
            object result;
            if (operation == "read")
            {
                var id = ApiRequest.String(request, "definitionId");
                var known = ApiRequest.OptionalString(request, "knownRevision");
                var host = UiHost(id);
                // The revision before the pages: a change between the two shows as a new revision the next time.
                var revision = host?.UiRevision;
                var pages = host == null || revision == known ? null : host.UiPages();
                // A host that began to stop meanwhile has no pages: it is not running.
                if (host?.IsRunning != true) result = new JsonObject { ["running"] = false };
                else if (pages == null) result = new { running = true, revision, unchanged = true };
                else result = new { running = true, revision, pages };
            }
            else if (operation == "image")
            {
                var id = ApiRequest.String(request, "definitionId");
                var nodeKey = ApiRequest.String(request, "nodeKey");
                var hash = ApiRequest.String(request, "hash");
                var jpeg = UiHost(id)?.UiImage(nodeKey, hash);
                if (jpeg == null) return Error("not_found", "The device's page shows no such image");
                result = new { jpeg = Convert.ToBase64String(jpeg) };
            }
            else if (operation == "act")
            {
                var id = ApiRequest.String(request, "definitionId");
                var nodeKey = ApiRequest.String(request, "nodeKey");
                var list = ApiRequest.String(request, "list");
                var card = ApiRequest.String(request, "card");
                var action = ApiRequest.String(request, "action");
                // As the request's text has them, so that a number sent is a number to the card's fields.
                var values = ApiRequest.Object(request, "values");
                if (UiHost(id) is not { } host) return Error("not_running", UiNotRunning);
                UiActResult acted;
                // Also when the definition began to stop before the press's turn came.
                try { acted = await host.UiAct(nodeKey, list, card, action, values); }
                catch (InvalidOperationException ex) { return Error("not_running", ex.Message); }
                result = new { status = acted.Status, message = acted.Message };
            }
            else if (operation == "counts")
            {
                var attention = new SortedDictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
                foreach (var host in _userScriptManager.Entities.Hosts())
                    if (host.IsRunning && host.UiAttention() is { Count: > 0 } waiting) attention[host.DefinitionId] = waiting;
                result = new { attention, stored = _userScriptManager.Data?.Stored() ?? new Dictionary<string, IReadOnlyList<string>>() };
            }
            else if (operation == "stores")
            {
                var shared = _userScriptManager.Data?.Shared() ?? [];
                result = new { shared = shared.Select(store => new { name = store.Name, bytes = store.Bytes, users = store.Users }) };
            }
            else
            {
                var name = ApiRequest.String(request, "name");
                var data = _userScriptManager.Data;
                // Only a name the shared stores list reaches DeleteShared, which checks it again: never a path outside them.
                if (data == null || !data.Shared().Any(store => store.Name == name)) return Error("not_found", "There is no shared store of that name");
                if (!data.DeleteShared(name))
                {
                    var users = data.Shared().FirstOrDefault(store => store.Name == name)?.Users ?? [];
                    var names = users.Select(user => _userScriptManager.Entities.Host(user)?.DefinitionName is { } called && !string.IsNullOrWhiteSpace(called) ? called : user).ToList();
                    return Error("in_use", names.Count > 1 ? $"{name} is in use by {string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}: turn them off first."
                        : $"{name} is in use by {(names.Count == 1 ? names[0] : "a definition")}: turn it off first.");
                }
                result = new { deleted = true };
            }
            return JsonSerializer.Serialize(new { version = 1, ok = true, result }, VisualStore.Json);
        }
        catch (IOException ex) { return Error("storage", ex.Message); }
        catch (UnauthorizedAccessException ex) { return Error("storage", ex.Message); }
        catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException or OverflowException)
        { return Error("invalid_request", ex.Message); }
        // Every field is read through ApiRequest, so a null reference here is HassSharp's own defect, not the request's.
        catch (NullReferenceException ex) { return InternalError("Device page", ex); }
    }
}
