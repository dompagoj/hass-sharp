using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HassSharp;

/// <summary>The kind of a card's field.</summary>
internal enum UiFieldType { Text, Select, Number, Toggle }

/// <summary>A field as a card was built with it: immutable, and holding no array the device can reach.</summary>
/// <param name="Text">A text field's value or a select field's option; null when none.</param>
/// <param name="Number">A number field's value; null when none.</param>
/// <param name="On">An on-off field's value.</param>
/// <param name="Minimum">A number field's least value; NaN when none.</param>
/// <param name="Maximum">A number field's greatest value; NaN when none.</param>
internal sealed record DrawnField(string Key, string Label, UiFieldType Type, string? Text, double? Number, bool On,
    string[]? Options, string[]? Suggestions, double Minimum, double Maximum, bool Required);

/// <summary>An action of a drawn card, with its id made of its label.</summary>
internal sealed record DrawnAction(string Id, string Label, UiActionStyle Style, string? Confirm, Func<UiValues, Task> Run);

/// <summary>A card as drawn: its image by hash, its text, fields and actions, checked.</summary>
internal sealed record DrawnCard(string Id, string? Image, string? Title, string? Detail, DrawnField[] Fields, DrawnAction[] Actions);

/// <summary>A list as drawn, with the page data the panel reads of it.</summary>
internal sealed record DrawnList(string Id, bool Attention, DrawnCard[] Cards, JsonObject Json);

/// <summary>
/// What a host keeps of one C# node's page: its lists in the order they were first drawn, the handlers of their
/// actions, and the JPEG of every image a current card shows, by its SHA-256, each once and only while a card shows it.
/// <see cref="Ui"/> is what the node's code draws with. Every member is safe from any thread; <see cref="Changed"/> is
/// raised outside the page's lock.
/// </summary>
internal sealed class DevicePage
{
    internal const int MaxLists = 20, MaxCards = 200, MaxFields = 8, MaxActions = 4, MaxText = 500, MaxImageBytes = 1024 * 1024;

    static readonly Regex IdPattern = new(@"^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}\z", RegexOptions.CultureInvariant);
    static readonly Regex KeyPattern = new(@"^[a-z][a-z0-9_]{0,39}\z", RegexOptions.CultureInvariant);
    static readonly Regex OtherCharacters = new("[^a-z0-9]+", RegexOptions.CultureInvariant);

    readonly string nodeKey;
    readonly string title;
    // Guards everything below; Changed is raised after it is let go.
    readonly Lock gate = new();
    // The lists in the order of their first drawing.
    readonly List<DrawnList> lists = [];
    // The JPEG of each image a current card shows, by hash; replaced whole on each change, never changed in place.
    Dictionary<string, byte[]> images = new(StringComparer.Ordinal);
    bool used;
    bool closed;

    /// <param name="nodeKey">The key of the C# node whose page this is.</param>
    /// <param name="title">What the panel heads the page with.</param>
    internal DevicePage(string nodeKey, string title)
    {
        this.nodeKey = nodeKey;
        this.title = title;
        Ui = new SharpUi(this);
    }

    /// <summary>What the node's code draws the page with.</summary>
    internal SharpUi Ui { get; }

    /// <summary>Whether a list was drawn at least once, also when every list was removed since.</summary>
    internal bool Used { get { lock (gate) return used; } }

    /// <summary>The cards of the lists drawn with attention.</summary>
    internal int Attention { get { lock (gate) return CountAttention(); } }

    /// <summary>Raised after each <see cref="SharpUi.List"/> or <see cref="SharpUi.Remove"/> that changed what the page shows, outside the page's lock.</summary>
    internal event Action? Changed;

    /// <summary>The page data the panel reads, without its revision: a new object each call, the caller's to change.</summary>
    internal JsonObject Snapshot()
    {
        lock (gate)
            return new JsonObject
            {
                ["nodeKey"] = nodeKey,
                ["title"] = title,
                ["attention"] = CountAttention(),
                ["lists"] = new JsonArray([.. lists.Select(list => list.Json.DeepClone())]),
            };
    }

    /// <summary>The JPEG of an image a current card shows.</summary>
    /// <param name="hash">The SHA-256 of its bytes in lower-case hexadecimal, as the page data gives it.</param>
    /// <returns>The bytes; null when no current card shows it.</returns>
    internal byte[]? Image(string hash)
    {
        if (hash == null) return null;
        lock (gate) return images.GetValueOrDefault(hash);
    }

    /// <summary>The action a press names, on the page as it is now, with the fields of its card as drawn now.</summary>
    /// <returns>Null when the page has no such list, the list no such card or the card no such action.</returns>
    internal UiPress? Find(string list, string card, string action)
    {
        lock (gate)
        {
            var drawn = lists.Find(each => each.Id == list)?.Cards.FirstOrDefault(each => each.Id == card);
            return drawn?.Actions.FirstOrDefault(each => each.Id == action) is { } found ? new UiPress(found.Label, found.Run, drawn.Fields) : null;
        }
    }

    /// <summary>Drops every list, handler and image: the page shows nothing, and drawing on it throws <see cref="ObjectDisposedException"/>.</summary>
    internal void Close()
    {
        lock (gate)
        {
            closed = true;
            lists.Clear();
            images = new(StringComparer.Ordinal);
        }
    }

    /// <summary>What <see cref="SharpUi.List"/> does: checks the list whole, then draws it in one step.</summary>
    internal void Draw(string id, string listTitle, IEnumerable<UiCard> cards, bool attention, string? empty)
    {
        lock (gate)
            if (closed) throw Stopped();
        CheckId(id, "list");
        CheckText(listTitle, "A list's title", required: true);
        CheckText(empty, "A list's empty text");
        if (cards == null) throw new ArgumentException($"List '{id}' needs its cards: pass none for an empty list");
        var given = cards.Take(MaxCards + 1).ToArray();
        if (given.Length > MaxCards) throw new ArgumentException($"List '{id}' has more than {MaxCards} cards: a list has at most {MaxCards}");

        var drawn = new DrawnCard[given.Length];
        var drawnImages = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < given.Length; i++)
        {
            if (given[i] == null) throw new ArgumentException($"List '{id}' has a card that is null");
            var (card, jpeg) = given[i].Draw();
            if (!ids.Add(card.Id)) throw new ArgumentException($"Two cards of list '{id}' have the id '{card.Id}'");
            if (jpeg != null) drawnImages.TryAdd(card.Image!, jpeg);
            drawn[i] = card;
        }
        var list = new DrawnList(id, attention, drawn, ListJson(id, listTitle, attention, empty, drawn));

        bool changed;
        lock (gate)
        {
            if (closed) throw Stopped();
            var at = lists.FindIndex(each => each.Id == id);
            if (at < 0 && lists.Count >= MaxLists) throw new ArgumentException($"The page has {MaxLists} lists, the most it may have: remove one before drawing list '{id}'");
            changed = at < 0 || !JsonNode.DeepEquals(lists[at].Json, list.Json);
            if (at < 0) lists.Add(list);
            else lists[at] = list;
            used = true;
            images = CurrentImages(drawnImages);
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>What <see cref="SharpUi.Remove"/> does.</summary>
    internal bool Remove(string id)
    {
        lock (gate)
        {
            if (closed) throw Stopped();
            var at = lists.FindIndex(each => each.Id == id);
            if (at < 0) return false;
            lists.RemoveAt(at);
            images = CurrentImages([]);
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>The SHA-256 of an image's bytes, in lower-case hexadecimal.</summary>
    internal static string Hash(byte[] jpeg) => Convert.ToHexStringLower(SHA256.HashData(jpeg));

    /// <summary>An action's id: its label in lower case, with each run of other characters than letters and digits made one <c>_</c>, trimmed of <c>_</c>.</summary>
    internal static string ActionId(string label) => OtherCharacters.Replace(label.ToLowerInvariant(), "_").Trim('_');

    /// <exception cref="ArgumentException">The id is not a list's or a card's.</exception>
    internal static void CheckId(string id, string of)
    {
        if (id == null || !IdPattern.IsMatch(id))
            throw new ArgumentException($"{(id == null ? "A null id" : $"'{id}'")} is not a {of}'s id: an id is 1 to 64 letters, digits, '_', '.', ':' and '-', starting with a letter or a digit");
    }

    /// <exception cref="ArgumentException">The text is longer than a page's text may be, or is required and empty.</exception>
    internal static void CheckText(string? text, string what, bool required = false)
    {
        if (required && string.IsNullOrWhiteSpace(text)) throw new ArgumentException($"{what} cannot be empty");
        if (text != null && text.Length > MaxText) throw new ArgumentException($"{what} takes {text.Length} characters: a page's text takes at most {MaxText}");
    }

    /// <exception cref="ArgumentException">The field is not one.</exception>
    internal static void CheckField(DrawnField field)
    {
        if (field.Key == null || !KeyPattern.IsMatch(field.Key))
            throw new ArgumentException($"{(field.Key == null ? "A null key" : $"'{field.Key}'")} is not a field's key: a key is 1 to 40 lower-case letters, digits and '_', starting with a letter");
        CheckText(field.Label, $"The label of field '{field.Key}'", required: true);
        CheckText(field.Text, $"The value of field '{field.Key}'");
        if (field.Suggestions != null)
            foreach (var suggestion in field.Suggestions)
            {
                if (suggestion == null) throw new ArgumentException($"Field '{field.Key}' has a suggestion that is null");
                CheckText(suggestion, $"A suggestion of field '{field.Key}'");
            }
        if (field.Type == UiFieldType.Select)
        {
            if (field.Options == null || field.Options.Length == 0) throw new ArgumentException($"Select field '{field.Key}' needs at least one option");
            var options = new HashSet<string>(StringComparer.Ordinal);
            foreach (var option in field.Options)
            {
                CheckText(option, $"An option of select field '{field.Key}'", required: true);
                if (!options.Add(option)) throw new ArgumentException($"Select field '{field.Key}' has an option twice");
            }
            if (field.Text != null && !options.Contains(field.Text)) throw new ArgumentException($"The value of select field '{field.Key}' is not one of its options");
        }
        if (field.Type == UiFieldType.Number)
        {
            if (double.IsInfinity(field.Minimum) || double.IsInfinity(field.Maximum))
                throw new ArgumentException($"The minimum and maximum of number field '{field.Key}' must each be a finite number, or NaN for none");
            if (field.Minimum > field.Maximum) throw new ArgumentException($"The minimum of number field '{field.Key}' is above its maximum");
            if (field.Number is { } value)
            {
                if (!double.IsFinite(value)) throw new ArgumentException($"The value of number field '{field.Key}' must be a finite number");
                if (value < field.Minimum || value > field.Maximum) throw new ArgumentException($"The value of number field '{field.Key}' is outside its range");
            }
        }
    }

    static ObjectDisposedException Stopped() => new(null, "The definition has stopped: its page draws nothing more");

    // Under the gate.
    int CountAttention() => lists.Where(list => list.Attention).Sum(list => list.Cards.Length);

    // The images the current cards show, each from what the page kept or else from the list being drawn; under the gate.
    Dictionary<string, byte[]> CurrentImages(Dictionary<string, byte[]> drawn)
    {
        var current = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var list in lists)
        foreach (var card in list.Cards)
            if (card.Image is { } hash && !current.ContainsKey(hash))
                current[hash] = images.TryGetValue(hash, out var kept) ? kept : drawn[hash];
        return current;
    }

    static JsonObject ListJson(string id, string listTitle, bool attention, string? empty, DrawnCard[] cards) => new()
    {
        ["id"] = id,
        ["title"] = listTitle,
        ["attention"] = attention,
        ["empty"] = empty,
        ["cards"] = new JsonArray([.. cards.Select(card => (JsonNode)new JsonObject
        {
            ["id"] = card.Id,
            ["image"] = card.Image,
            ["title"] = card.Title,
            ["detail"] = card.Detail,
            ["fields"] = new JsonArray([.. card.Fields.Select(FieldJson)]),
            ["actions"] = new JsonArray([.. card.Actions.Select(action => (JsonNode)new JsonObject
            {
                ["id"] = action.Id,
                ["label"] = action.Label,
                ["style"] = action.Style switch { UiActionStyle.Primary => "primary", UiActionStyle.Danger => "danger", _ => "default" },
                ["confirm"] = action.Confirm,
            })]),
        })]),
    };

    static JsonNode FieldJson(DrawnField field)
    {
        var json = new JsonObject
        {
            ["key"] = field.Key,
            ["label"] = field.Label,
            ["type"] = field.Type switch { UiFieldType.Select => "select", UiFieldType.Number => "number", UiFieldType.Toggle => "toggle", _ => "text" },
            ["value"] = field.Type switch
            {
                UiFieldType.Number => JsonValue.Create(field.Number),
                UiFieldType.Toggle => JsonValue.Create(field.On),
                _ => JsonValue.Create(field.Text),
            },
        };
        if (field.Options != null) json["options"] = new JsonArray([.. field.Options.Select(option => (JsonNode?)option)]);
        if (field.Suggestions != null) json["suggestions"] = new JsonArray([.. field.Suggestions.Select(suggestion => (JsonNode?)suggestion)]);
        if (!double.IsNaN(field.Minimum)) json["minimum"] = field.Minimum;
        if (!double.IsNaN(field.Maximum)) json["maximum"] = field.Maximum;
        json["required"] = field.Required;
        return json;
    }
}

/// <summary>The action a press named, with what the host needs to check and run it.</summary>
/// <param name="Label">The action's label, for the definition's log.</param>
/// <param name="Run">Its handler.</param>
internal sealed record UiPress(string Label, Func<UiValues, Task> Run)
{
    // The fields of the card as drawn when the press was found.
    readonly DrawnField[] fields = [];

    internal UiPress(string label, Func<UiValues, Task> run, DrawnField[] fields) : this(label, run) => this.fields = fields;

    /// <summary>
    /// The values as the handler gets them: for each field of the card, what the press sent for it, or what it showed
    /// when the press sent nothing; keys the card has no field for are ignored.
    /// </summary>
    /// <param name="sent">The values the panel sent, by key, as they came from the browser.</param>
    /// <exception cref="UiRefusal">A required field is empty, a number is outside its range, an option is not the select's, or a value is of the wrong kind, as a plain sentence for the user.</exception>
    internal UiValues Values(JsonObject sent)
    {
        ArgumentNullException.ThrowIfNull(sent);
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        var numbers = new Dictionary<string, double?>(StringComparer.Ordinal);
        var toggles = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            var given = sent.TryGetPropertyValue(field.Key, out var node);
            switch (field.Type)
            {
                case UiFieldType.Text:
                    var text = !given ? field.Text ?? "" : node == null ? "" : Kind(node) == JsonValueKind.String ? node.GetValue<string>() : throw Refuse(field, "must be text");
                    if (text.Length > DevicePage.MaxText) throw Refuse(field, $"takes at most {DevicePage.MaxText} characters");
                    if (field.Required && string.IsNullOrWhiteSpace(text)) throw Refuse(field, "is required");
                    texts[field.Key] = text;
                    break;
                case UiFieldType.Select:
                    var option = !given ? field.Text ?? "" : node == null ? "" : Kind(node) == JsonValueKind.String ? node.GetValue<string>() : throw Refuse(field, "must be one of its options");
                    if (option.Length > 0 && !field.Options!.Contains(option, StringComparer.Ordinal)) throw Refuse(field, "must be one of its options");
                    if (field.Required && option.Length == 0) throw Refuse(field, "is required");
                    texts[field.Key] = option;
                    break;
                case UiFieldType.Number:
                    double? number = !given ? field.Number : node == null ? null
                        : Kind(node) == JsonValueKind.Number && node.AsValue().TryGetValue<double>(out var read) && double.IsFinite(read) ? read
                        : throw Refuse(field, "must be a number");
                    if (number == null && field.Required) throw Refuse(field, "is required");
                    if (number < field.Minimum) throw Refuse(field, $"must be at least {field.Minimum.ToString(CultureInfo.InvariantCulture)}");
                    if (number > field.Maximum) throw Refuse(field, $"must be at most {field.Maximum.ToString(CultureInfo.InvariantCulture)}");
                    numbers[field.Key] = number;
                    break;
                case UiFieldType.Toggle:
                    toggles[field.Key] = !given ? field.On : Kind(node) switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => throw Refuse(field, "must be on or off"),
                    };
                    break;
            }
        }
        return new UiValues(texts, numbers, toggles);
    }

    static JsonValueKind Kind(JsonNode? node) => node?.GetValueKind() ?? JsonValueKind.Null;

    static UiRefusal Refuse(DrawnField field, string what) => new($"{field.Label} {what}.");
}
