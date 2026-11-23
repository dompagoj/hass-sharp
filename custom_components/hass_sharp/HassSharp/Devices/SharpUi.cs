namespace HassSharp;

/// <summary>
/// A device's page in the HassSharp panel: lists of cards, each with an image, a title and a detail line, fields and
/// actions whose handlers run in the device's code when the user presses them. The device draws a list with
/// <see cref="List"/> whenever its data changes, and again in <c>Start()</c>: nothing of the page is stored. A page has at
/// most 20 lists, a list at most 200 cards, and a card at most 8 fields and 4 actions; a text takes at most 500
/// characters and an image at most 1 MiB as JPEG. A list's and a card's id is 1 to 64 letters, digits, <c>_</c>,
/// <c>.</c>, <c>:</c> and <c>-</c>, starting with a letter or a digit. Safe to use from any thread.
/// </summary>
public sealed class SharpUi
{
    readonly DevicePage page;

    internal SharpUi(DevicePage page) => this.page = page;

    /// <summary>
    /// Draws a list, or replaces the one with that id, which keeps its place: lists show in the order they were first
    /// drawn. The page takes what it needs of the cards now, so changing or reusing them afterwards changes nothing.
    /// </summary>
    /// <param name="id">The list's id, unique on the page.</param>
    /// <param name="title">Its heading.</param>
    /// <param name="cards">Its cards, each with an id unique in the list.</param>
    /// <param name="attention">Whether its cards count towards the number of things waiting for the user.</param>
    /// <param name="empty">What the list shows when it has no cards; nothing when null.</param>
    /// <exception cref="ArgumentException">The page is not one, as the message says; the page is as it was.</exception>
    /// <exception cref="ObjectDisposedException">The definition has stopped.</exception>
    public void List(string id, string title, IEnumerable<UiCard> cards, bool attention = false, string? empty = null) =>
        page.Draw(id, title, cards, attention, empty);

    /// <summary>Removes a list.</summary>
    /// <returns>Whether there was a list with that id.</returns>
    /// <exception cref="ObjectDisposedException">The definition has stopped.</exception>
    public bool Remove(string id) => page.Remove(id);
}

/// <summary>
/// A card of a list on a device's page, built in calls that each return the card: an image, a title and a detail line,
/// fields the user fills in and actions the user presses. Nothing is checked until the card is drawn with
/// <see cref="SharpUi.List"/>, which takes what it needs of it then.
/// </summary>
/// <param name="id">The card's id, unique in its list: what a press names, so the same thing keeps the same id when redrawn.</param>
public sealed class UiCard(string id)
{
    readonly List<DrawnField> fields = [];
    readonly List<(string Label, Func<UiValues, Task> Run, string? Confirm, UiActionStyle Style)> actions = [];
    bool hasImage;
    byte[]? jpeg;
    string? title, detail;

    /// <summary>Shows a picture, encoded to JPEG now with its drawings drawn in; replaces an earlier image.</summary>
    public UiCard Image(SharpImage image)
    {
        hasImage = true;
        jpeg = image?.ToJpeg();
        return this;
    }

    /// <summary>Shows a JPEG, at most 1 MiB; replaces an earlier image.</summary>
    public UiCard Image(byte[] jpeg)
    {
        hasImage = true;
        this.jpeg = jpeg;
        return this;
    }

    /// <summary>Shows a title and, under it, a detail line; replaces earlier text.</summary>
    public UiCard Text(string title, string? detail = null)
    {
        this.title = title;
        this.detail = detail;
        return this;
    }

    /// <summary>Adds a text field.</summary>
    /// <param name="key">What its value is read by: 1 to 40 lower-case letters, digits and <c>_</c>, starting with a letter; unique on the card.</param>
    /// <param name="label">What it is called.</param>
    /// <param name="value">What it shows until the user changes it.</param>
    /// <param name="required">Whether a press needs it filled in.</param>
    /// <param name="suggestions">Values the panel offers as the user types.</param>
    public UiCard Field(string key, string label, string? value = null, bool required = false, IEnumerable<string>? suggestions = null)
    {
        fields.Add(new(key, label, UiFieldType.Text, value, null, false, null, suggestions?.ToArray(), double.NaN, double.NaN, required));
        return this;
    }

    /// <summary>Adds a field the user picks one of its options in.</summary>
    /// <param name="key">What its value is read by, as for <see cref="Field"/>.</param>
    /// <param name="label">What it is called.</param>
    /// <param name="options">What may be picked, each once.</param>
    /// <param name="value">The option picked until the user changes it; none when null.</param>
    /// <param name="required">Whether a press needs an option picked.</param>
    public UiCard Select(string key, string label, IEnumerable<string> options, string? value = null, bool required = false)
    {
        fields.Add(new(key, label, UiFieldType.Select, value, null, false, options?.ToArray(), null, double.NaN, double.NaN, required));
        return this;
    }

    /// <summary>Adds a number field.</summary>
    /// <param name="key">What its value is read by, as for <see cref="Field"/>.</param>
    /// <param name="label">What it is called.</param>
    /// <param name="value">What it shows until the user changes it; empty when null.</param>
    /// <param name="minimum">The least value a press may send; none when NaN.</param>
    /// <param name="maximum">The greatest value a press may send; none when NaN.</param>
    /// <param name="required">Whether a press needs a number.</param>
    public UiCard Number(string key, string label, double? value = null, double minimum = double.NaN, double maximum = double.NaN, bool required = false)
    {
        fields.Add(new(key, label, UiFieldType.Number, null, value, false, null, null, minimum, maximum, required));
        return this;
    }

    /// <summary>Adds an on-off field.</summary>
    /// <param name="key">What its value is read by, as for <see cref="Field"/>.</param>
    /// <param name="label">What it is called.</param>
    /// <param name="value">Whether it is on until the user changes it.</param>
    public UiCard Toggle(string key, string label, bool value = false)
    {
        fields.Add(new(key, label, UiFieldType.Toggle, null, null, value, null, null, double.NaN, double.NaN, false));
        return this;
    }

    /// <summary>
    /// Adds a button. A press runs its handler with the card's values; a handler that throws <see cref="UiRefusal"/> shows
    /// its message on the card. Its id, which a press names, is its label in lower case with each run of other characters
    /// than letters and digits made one <c>_</c>, trimmed of <c>_</c>: <c>Merge into…</c> is <c>merge_into</c>.
    /// </summary>
    /// <param name="label">What the button says; unique on the card as an id.</param>
    /// <param name="run">The handler.</param>
    /// <param name="confirm">What the panel asks before it presses; nothing when null.</param>
    /// <param name="style">How the button looks.</param>
    public UiCard Action(string label, Func<UiValues, Task> run, string? confirm = null, UiActionStyle style = UiActionStyle.Default)
    {
        actions.Add((label, run, confirm, style));
        return this;
    }

    /// <summary>The card as drawn now, checked, and a copy of its JPEG.</summary>
    /// <exception cref="ArgumentException">The card is not one.</exception>
    internal (DrawnCard Card, byte[]? Jpeg) Draw()
    {
        DevicePage.CheckId(id, "card");
        DevicePage.CheckText(title, "A card's title");
        DevicePage.CheckText(detail, "A card's detail line");
        if (fields.Count > DevicePage.MaxFields) throw new ArgumentException($"Card '{id}' has {fields.Count} fields: a card has at most {DevicePage.MaxFields}");
        if (actions.Count > DevicePage.MaxActions) throw new ArgumentException($"Card '{id}' has {actions.Count} actions: a card has at most {DevicePage.MaxActions}");

        byte[]? image = null;
        if (hasImage)
        {
            if (jpeg == null) throw new ArgumentException($"The image of card '{id}' cannot be null: leave the image out instead");
            if (jpeg.Length > DevicePage.MaxImageBytes) throw new ArgumentException($"The image of card '{id}' takes {jpeg.Length} bytes: an image takes at most 1 MiB (1,048,576 bytes)");
            if (jpeg.Length < 2 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) throw new ArgumentException($"The image of card '{id}' is not a JPEG: its bytes do not start with FF D8");
            image = (byte[])jpeg.Clone();
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            DevicePage.CheckField(field);
            if (!keys.Add(field.Key)) throw new ArgumentException($"Two fields of card '{id}' have the key '{field.Key}'");
        }

        var drawn = new DrawnAction[actions.Count];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < actions.Count; i++)
        {
            var (label, run, confirm, style) = actions[i];
            DevicePage.CheckText(label, "An action's label", required: true);
            DevicePage.CheckText(confirm, "An action's confirm text");
            if (run == null) throw new ArgumentException($"Action '{label}' of card '{id}' needs a handler to run");
            if (!Enum.IsDefined(style)) throw new ArgumentException($"Action '{label}' of card '{id}' has a style that is none of UiActionStyle's");
            var actionId = DevicePage.ActionId(label);
            if (actionId.Length == 0) throw new ArgumentException($"Action '{label}' of card '{id}' needs a letter or a digit in its label, which its id is made of");
            if (!ids.Add(actionId)) throw new ArgumentException($"Two actions of card '{id}' have the id '{actionId}': an action's id is its label in lower case, with '_' for each run of other characters");
            drawn[i] = new DrawnAction(actionId, label, style, confirm, run);
        }

        return (new DrawnCard(id, image == null ? null : DevicePage.Hash(image), title, detail, [.. fields], drawn), image);
    }
}

/// <summary>How an action's button looks.</summary>
public enum UiActionStyle
{
    /// <summary>A plain button.</summary>
    Default,
    /// <summary>The button of the card's main action.</summary>
    Primary,
    /// <summary>A button that deletes or cannot be undone.</summary>
    Danger,
}

/// <summary>The values of a card's fields when its action was pressed, checked against the fields as drawn: what the user sent, or what the field showed.</summary>
public sealed class UiValues
{
    readonly IReadOnlyDictionary<string, string> texts;
    readonly IReadOnlyDictionary<string, double?> numbers;
    readonly IReadOnlyDictionary<string, bool> toggles;

    internal UiValues(IReadOnlyDictionary<string, string> texts, IReadOnlyDictionary<string, double?> numbers, IReadOnlyDictionary<string, bool> toggles)
    {
        this.texts = texts;
        this.numbers = numbers;
        this.toggles = toggles;
    }

    /// <summary>A text field's value, or the option a select field has picked; empty when there is none.</summary>
    /// <exception cref="KeyNotFoundException">The card has no text or select field with the key.</exception>
    public string this[string key] =>
        key != null && texts.TryGetValue(key, out var value) ? value : throw new KeyNotFoundException($"The card has no text or select field '{key}'");

    /// <summary>A number field's value; null when it has none.</summary>
    /// <exception cref="KeyNotFoundException">The card has no number field with the key.</exception>
    public double? Number(string key) =>
        key != null && numbers.TryGetValue(key, out var value) ? value : throw new KeyNotFoundException($"The card has no number field '{key}'");

    /// <summary>Whether an on-off field is on.</summary>
    /// <exception cref="KeyNotFoundException">The card has no on-off field with the key.</exception>
    public bool Toggle(string key) =>
        key != null && toggles.TryGetValue(key, out var value) ? value : throw new KeyNotFoundException($"The card has no on-off field '{key}'");
}

/// <summary>Thrown by an action's handler to refuse the press: the panel shows the message on the card, as a plain sentence such as "Name is taken."</summary>
/// <param name="message">What the user reads.</param>
public sealed class UiRefusal(string message) : Exception(message);
