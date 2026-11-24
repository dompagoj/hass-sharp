using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HassSharp;

/// <summary>The kind of a card's field.</summary>
internal enum UiFieldType { Text, Select, Number, Toggle, Pick }

/// <summary>A field as a card was built with it: immutable, and holding no array the device can reach.</summary>
/// <param name="Text">A text field's value, a select field's option or the id of a pick field's option; null when none.</param>
/// <param name="Number">A number field's value; null when none.</param>
/// <param name="On">An on-off field's value.</param>
/// <param name="Minimum">A number field's least value; NaN when none.</param>
/// <param name="Maximum">A number field's greatest value; NaN when none.</param>
internal sealed record DrawnField(string Key, string Label, UiFieldType Type, string? Text, double? Number, bool On,
    string[]? Options, string[]? Suggestions, double Minimum, double Maximum, bool Required)
{
    /// <summary>The id of the set a pick field chooses from; null for the other fields.</summary>
    internal string? Set { get; init; }

    /// <summary>The ids of the options a pick field does not offer; null when none were given.</summary>
    internal string[]? Except { get; init; }

    /// <summary>A pick field's form of a new one; null when it has none.</summary>
    internal DrawnForm? Add { get; init; }
}

/// <summary>A pick field's form of a new one, as the card was built with it.</summary>
internal sealed record DrawnForm(string Title, DrawnField[] Fields);

/// <summary>An option of a set as drawn: its image by hash, and its text, checked.</summary>
internal sealed record DrawnOption(string Id, string? Image, string Title, string? Detail);

/// <summary>A set of options as drawn, with the ids a press is checked against and the page data the panel reads of it.</summary>
internal sealed record DrawnSet(string Id, DrawnOption[] Options, FrozenSet<string> Ids, JsonObject Json);

/// <summary>An action of a drawn card, with its id made of its label.</summary>
/// <param name="Validate">Whether a press of it is checked against the card's fields; when not, it takes them as drawn.</param>
internal sealed record DrawnAction(string Id, string Label, UiActionStyle Style, string? Confirm, bool Validate, Func<UiValues, Task> Run);

/// <summary>A card as drawn: its image by hash, its text, fields and actions, checked.</summary>
internal sealed record DrawnCard(string Id, string? Image, string? Title, string? Detail, DrawnField[] Fields, DrawnAction[] Actions);

/// <summary>A list as drawn, with the page data the panel reads of it.</summary>
internal sealed record DrawnList(string Id, bool Attention, DrawnCard[] Cards, JsonObject Json);

/// <summary>
/// What a host keeps of one C# node's page: its lists and its sets of options, each in the order they were first drawn,
/// the handlers of the lists' actions, and the JPEG of every image a current card or option shows, by its SHA-256, each
/// once and only while a card or an option shows it.
/// <see cref="Ui"/> is what the node's code draws with. Every member is safe from any thread; <see cref="Changed"/> is
/// raised outside the page's lock.
/// </summary>
internal sealed partial class DevicePage
{
    internal const int MaxLists = 20, MaxCards = 200, MaxFields = 8, MaxActions = 4, MaxText = 500, MaxImageBytes = 1024 * 1024,
        MaxSets = 8, MaxOptions = 500, MaxFormFields = 4;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,39}\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex OtherCharacters();

    readonly string nodeKey;
    readonly string title;
    // Guards everything below; Changed is raised after it is let go.
    readonly Lock gate = new();
    // The lists in the order of their first drawing.
    readonly List<DrawnList> lists = [];
    // The sets of options in the order of their first drawing; a set a current card picks from is always here.
    readonly List<DrawnSet> sets = [];
    // The JPEG of each image a current card or option shows, by hash; replaced whole on each change, never changed in place.
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

    /// <summary>
    /// Raised after each <see cref="SharpUi.List"/>, <see cref="SharpUi.Remove"/>, <see cref="SharpUi.Options"/> or
    /// <see cref="SharpUi.RemoveOptions"/> that changed what the page shows, outside the page's lock.
    /// </summary>
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
                ["lists"] = new JsonArray([.. lists.Select(ListSnapshot)]),
                ["options"] = new JsonArray([.. sets.Select(set => set.Json.DeepClone())]),
            };
    }

    /// <summary>The JPEG of an image a current card or option shows.</summary>
    /// <param name="hash">The SHA-256 of its bytes in lower-case hexadecimal, as the page data gives it.</param>
    /// <returns>The bytes; null when no current card or option shows it.</returns>
    internal byte[]? Image(string hash)
    {
        if (hash == null) return null;
        lock (gate) return images.GetValueOrDefault(hash);
    }

    /// <summary>
    /// The action a press names, on the page as it is now, with the fields of its card as drawn now and the option ids of
    /// the sets its pick fields choose from as they are now.
    /// </summary>
    /// <returns>Null when the page has no such list, the list no such card or the card no such action.</returns>
    internal UiPress? Find(string list, string card, string action)
    {
        lock (gate)
        {
            var drawn = lists.Find(each => each.Id == list)?.Cards.FirstOrDefault(each => each.Id == card);
            if (drawn?.Actions.FirstOrDefault(each => each.Id == action) is not { } found) return null;
            var options = new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal);
            foreach (var field in drawn.Fields)
                if (field.Type == UiFieldType.Pick) options[field.Set!] = SetOf(field).Ids;
            return new UiPress(found.Label, found.Run, drawn.Fields, found.Validate, options);
        }
    }

    /// <summary>Drops every list, set, handler and image: the page shows nothing, and drawing on it throws <see cref="ObjectDisposedException"/>.</summary>
    internal void Close()
    {
        lock (gate)
        {
            closed = true;
            lists.Clear();
            sets.Clear();
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
            // Against the sets as they are now, under the lock that removing a set takes: a card never picks from a set the page has not.
            foreach (var card in drawn)
            foreach (var field in card.Fields)
                if (field.Type == UiFieldType.Pick) CheckPick(card, field);
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

    /// <summary>What <see cref="SharpUi.Options"/> does: checks the set whole, then draws it in one step.</summary>
    internal void DrawOptions(string id, IEnumerable<UiOption> options)
    {
        lock (gate)
            if (closed) throw Stopped();
        CheckId(id, "set");
        if (options == null) throw new ArgumentException($"Set '{id}' needs its options: pass none for an empty set");
        var given = options.Take(MaxOptions + 1).ToArray();
        if (given.Length > MaxOptions) throw new ArgumentException($"Set '{id}' has more than {MaxOptions} options: a set has at most {MaxOptions}");

        var drawn = new DrawnOption[given.Length];
        var drawnImages = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < given.Length; i++)
        {
            if (given[i] == null) throw new ArgumentException($"Set '{id}' has an option that is null");
            var (option, jpeg) = given[i].Draw();
            if (!ids.Add(option.Id)) throw new ArgumentException($"Two options of set '{id}' have the id '{option.Id}'");
            if (jpeg != null) drawnImages.TryAdd(option.Image!, jpeg);
            drawn[i] = option;
        }
        var set = new DrawnSet(id, drawn, ids.ToFrozenSet(StringComparer.Ordinal), SetJson(id, drawn));

        bool changed;
        lock (gate)
        {
            if (closed) throw Stopped();
            var at = sets.FindIndex(each => each.Id == id);
            if (at < 0 && sets.Count >= MaxSets) throw new ArgumentException($"The page has {MaxSets} sets of options, the most it may have: remove one before drawing set '{id}'");
            changed = at < 0 || !JsonNode.DeepEquals(sets[at].Json, set.Json);
            if (at < 0) sets.Add(set);
            else sets[at] = set;
            images = CurrentImages(drawnImages);
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>What <see cref="SharpUi.RemoveOptions"/> does.</summary>
    internal bool RemoveOptions(string id)
    {
        lock (gate)
        {
            if (closed) throw Stopped();
            var at = sets.FindIndex(each => each.Id == id);
            if (at < 0) return false;
            foreach (var list in lists)
            foreach (var card in list.Cards)
                if (card.Fields.FirstOrDefault(field => field.Type == UiFieldType.Pick && field.Set == id) is { } field)
                    throw new ArgumentException($"Set '{id}' is the one pick field '{field.Key}' of card '{card.Id}' in list '{list.Id}' chooses from: redraw or remove the list first");
            sets.RemoveAt(at);
            images = CurrentImages([]);
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>The SHA-256 of an image's bytes, in lower-case hexadecimal.</summary>
    internal static string Hash(byte[] jpeg) => Convert.ToHexStringLower(SHA256.HashData(jpeg));

    /// <summary>An action's id: its label in lower case, with each run of other characters than letters and digits made one <c>_</c>, trimmed of <c>_</c>.</summary>
    internal static string ActionId(string label) => OtherCharacters().Replace(label.ToLowerInvariant(), "_").Trim('_');

    /// <exception cref="ArgumentException">The id is not a list's, a card's, a set's or an option's.</exception>
    internal static void CheckId(string id, string of)
    {
        if (id == null || !IdPattern().IsMatch(id))
            throw new ArgumentException($"{(id == null ? "A null id" : $"'{id}'")} is not {(of[0] is 'a' or 'e' or 'i' or 'o' or 'u' ? "an" : "a")} {of}'s id: an id is 1 to 64 letters, digits, '_', '.', ':' and '-', starting with a letter or a digit");
    }

    /// <summary>A copy of an image's JPEG, checked.</summary>
    /// <param name="of">What shows it, for the message: <c>card 'x'</c>.</param>
    /// <exception cref="ArgumentException">The bytes are null, more than 1 MiB, or not a JPEG's.</exception>
    internal static byte[] TakeJpeg(byte[]? jpeg, string of)
    {
        if (jpeg == null) throw new ArgumentException($"The image of {of} cannot be null: leave the image out instead");
        if (jpeg.Length > MaxImageBytes) throw new ArgumentException($"The image of {of} takes {jpeg.Length} bytes: an image takes at most 1 MiB (1,048,576 bytes)");
        if (jpeg.Length < 2 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) throw new ArgumentException($"The image of {of} is not a JPEG: its bytes do not start with FF D8");
        return (byte[])jpeg.Clone();
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
        if (field.Key == null || !KeyPattern().IsMatch(field.Key))
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
        if (field.Type == UiFieldType.Pick)
        {
            if (field.Set == null || !IdPattern().IsMatch(field.Set))
                throw new ArgumentException($"Pick field '{field.Key}' needs the id of a set of options to choose from, as Ui.Options draws it");
            if (field.Except != null)
                foreach (var option in field.Except)
                {
                    if (option == null || !IdPattern().IsMatch(option)) throw new ArgumentException($"Pick field '{field.Key}' has an option it does not offer whose id is not an option's");
                    if (option == field.Text) throw new ArgumentException($"The value of pick field '{field.Key}' is one of the options it does not offer");
                }
            if (field.Add is { } form)
            {
                CheckText(form.Title, $"The title of the new form of pick field '{field.Key}'", required: true);
                if (form.Fields.Length > MaxFormFields) throw new ArgumentException($"The new form of pick field '{field.Key}' has {form.Fields.Length} fields: a form has at most {MaxFormFields}");
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var each in form.Fields)
                {
                    CheckField(each);
                    if (!keys.Add(each.Key)) throw new ArgumentException($"Two fields of the new form of pick field '{field.Key}' have the key '{each.Key}'");
                }
            }
        }
    }

    // A pick field of a card being drawn, against the sets as they are now; under the gate.
    void CheckPick(DrawnCard card, DrawnField field)
    {
        if (sets.Find(set => set.Id == field.Set) is not { } set)
            throw new ArgumentException($"Pick field '{field.Key}' of card '{card.Id}' chooses from set '{field.Set}', which the page has not: draw it with Ui.Options first");
        if (field.Text != null && !set.Ids.Contains(field.Text))
            throw new ArgumentException($"The value of pick field '{field.Key}' of card '{card.Id}' is not an option of set '{field.Set}' now");
    }

    // The set a current card's pick field chooses from; under the gate.
    DrawnSet SetOf(DrawnField pick) => sets.Find(set => set.Id == pick.Set)!;

    // A list's page data, each pick field whose option has gone from its set since showing none; under the gate.
    JsonNode ListSnapshot(DrawnList list)
    {
        var json = list.Json.DeepClone();
        for (var c = 0; c < list.Cards.Length; c++)
        {
            var fields = list.Cards[c].Fields;
            for (var f = 0; f < fields.Length; f++)
                if (fields[f] is { Type: UiFieldType.Pick, Text: { } value } pick && !SetOf(pick).Ids.Contains(value))
                    json["cards"]![c]!["fields"]![f]!["value"] = null;
        }
        return json;
    }

    static ObjectDisposedException Stopped() => new(null, "The definition has stopped: its page draws nothing more");

    // Under the gate.
    int CountAttention() => lists.Where(list => list.Attention).Sum(list => list.Cards.Length);

    // The images the current cards and options show, each from what the page kept or else from the list or set being
    // drawn; under the gate.
    Dictionary<string, byte[]> CurrentImages(Dictionary<string, byte[]> drawn)
    {
        var current = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        void Keep(string? hash)
        {
            if (hash != null && !current.ContainsKey(hash)) current[hash] = images.TryGetValue(hash, out var kept) ? kept : drawn[hash];
        }
        foreach (var list in lists)
        foreach (var card in list.Cards)
            Keep(card.Image);
        foreach (var set in sets)
        foreach (var option in set.Options)
            Keep(option.Image);
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
                ["validate"] = action.Validate,
            })]),
        })]),
    };

    static JsonObject SetJson(string id, DrawnOption[] options) => new()
    {
        ["id"] = id,
        ["items"] = new JsonArray([.. options.Select(option => (JsonNode)new JsonObject
        {
            ["id"] = option.Id,
            ["image"] = option.Image,
            ["title"] = option.Title,
            ["detail"] = option.Detail,
        })]),
    };

    static JsonNode FieldJson(DrawnField field)
    {
        var json = new JsonObject
        {
            ["key"] = field.Key,
            ["label"] = field.Label,
            ["type"] = field.Type switch { UiFieldType.Select => "select", UiFieldType.Number => "number", UiFieldType.Toggle => "toggle", UiFieldType.Pick => "pick", _ => "text" },
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
        if (field.Set != null) json["set"] = field.Set;
        if (field.Except != null) json["except"] = new JsonArray([.. field.Except.Select(option => (JsonNode?)option)]);
        if (field.Add is { } form) json["add"] = new JsonObject { ["title"] = form.Title, ["fields"] = new JsonArray([.. form.Fields.Select(FieldJson)]) };
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
    // Whether the action checks what a press sent against the fields.
    readonly bool validate = true;
    // The option ids of each set the card's pick fields choose from, by the set's id, as the sets were when the press was found.
    readonly IReadOnlyDictionary<string, FrozenSet<string>> options = new Dictionary<string, FrozenSet<string>>();

    internal UiPress(string label, Func<UiValues, Task> run, DrawnField[] fields, bool validate, IReadOnlyDictionary<string, FrozenSet<string>> options) : this(label, run)
    {
        this.fields = fields;
        this.validate = validate;
        this.options = options;
    }

    /// <summary>
    /// The values as the handler gets them: for each field of the card, what the press sent for it, or what it showed
    /// when the press sent nothing; keys the card has no field for are ignored. A pick field takes <c>{"picked": id}</c>,
    /// <c>{"new": {key: value}}</c>, whose values are checked against its form's fields as a card's are, or null for none.
    /// For an action that does not validate, what the press sent is ignored and every field reads as drawn, unchecked, a
    /// pick field's option as none once its set no longer has it.
    /// </summary>
    /// <param name="sent">The values the panel sent, by key, as they came from the browser.</param>
    /// <exception cref="UiRefusal">A required field is empty, a number is outside its range, an option is not the select's, or a value is of the wrong kind, as a plain sentence for the user.</exception>
    /// <exception cref="UiPageChanged">A pick field's option, sent or as drawn, is not one of its set's as the set was when the press was found, or is one the field does not offer.</exception>
    internal UiValues Values(JsonObject sent)
    {
        ArgumentNullException.ThrowIfNull(sent);
        return validate ? Checked(fields, sent) : Drawn();
    }

    // What a press sent for the fields, checked: those of the card, or those of a pick field's form.
    UiValues Checked(DrawnField[] of, JsonObject sent)
    {
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        var numbers = new Dictionary<string, double?>(StringComparer.Ordinal);
        var toggles = new Dictionary<string, bool>(StringComparer.Ordinal);
        var picks = new Dictionary<string, UiPick>(StringComparer.Ordinal);
        foreach (var field in of)
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
                case UiFieldType.Pick:
                    picks[field.Key] = Pick(field, given, node);
                    break;
            }
        }
        return new UiValues(texts, numbers, toggles, picks);
    }

    // What a press sent for a pick field, checked: the option it names, or the values of its form's new one; the drawn
    // option when it sent nothing.
    UiPick Pick(DrawnField field, bool given, JsonNode? node)
    {
        var wrong = field.Add == null ? "must be one of its options" : "must be one of its options or a new one";
        string? picked = null;
        UiValues? made = null;
        if (!given) picked = field.Text;
        else if (node is JsonObject sent)
        {
            var hasPicked = sent.TryGetPropertyValue("picked", out var id);
            var hasNew = sent.TryGetPropertyValue("new", out var form);
            if (hasPicked && hasNew) throw Refuse(field, "cannot be both one of its options and a new one");
            if (hasPicked) picked = Kind(id) == JsonValueKind.String ? id!.GetValue<string>() : throw Refuse(field, wrong);
            else if (!hasNew) throw Refuse(field, wrong);
            else if (field.Add == null) throw Refuse(field, "cannot be a new one");
            else made = form is JsonObject values ? Checked(field.Add.Fields, values) : throw Refuse(field, wrong);
        }
        else if (node != null) throw Refuse(field, wrong);
        if (picked != null && !Offers(field, picked)) throw new UiPageChanged();
        if (picked == null && made == null && field.Required) throw Refuse(field, "is required");
        return new(picked, made);
    }

    // Whether the option is one of the pick field's set's as the set was when the press was found, and one it offers.
    bool Offers(DrawnField pick, string option) =>
        options[pick.Set!].Contains(option) && pick.Except?.Contains(option, StringComparer.Ordinal) != true;

    // Each field's value as the card was drawn: a text or an option, or the empty text; a number or null; a toggle; a
    // pick field's option while its set has it, else none, and never a new one.
    UiValues Drawn() => new(
        fields.Where(field => field.Type is UiFieldType.Text or UiFieldType.Select).ToDictionary(field => field.Key, field => field.Text ?? "", StringComparer.Ordinal),
        fields.Where(field => field.Type == UiFieldType.Number).ToDictionary(field => field.Key, field => field.Number, StringComparer.Ordinal),
        fields.Where(field => field.Type == UiFieldType.Toggle).ToDictionary(field => field.Key, field => field.On, StringComparer.Ordinal),
        fields.Where(field => field.Type == UiFieldType.Pick).ToDictionary(field => field.Key,
            field => new UiPick(field.Text is { } option && Offers(field, option) ? option : null, null), StringComparer.Ordinal));

    static JsonValueKind Kind(JsonNode? node) => node?.GetValueKind() ?? JsonValueKind.Null;

    static UiRefusal Refuse(DrawnField field, string what) => new($"{field.Label} {what}.");
}

/// <summary>What a press gave for a pick field: the id of the option chosen, or the values of the new one's form; neither when none.</summary>
internal sealed record UiPick(string? Id, UiValues? New);

/// <summary>
/// Thrown by <see cref="UiPress.Values"/> when a press's pick field names an option its set no longer had when the press
/// was found, or one the field does not offer: the host answers it <c>changed</c>, as a press of a card that has gone,
/// and runs nothing.
/// </summary>
internal sealed class UiPageChanged() : Exception("The press picked an option its card's set no longer has");
