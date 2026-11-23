using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HassSharp;

/// <summary>
/// A device's page on its own, with no host: its snapshot has the exact shape the panel reads; a list keeps the place
/// of its first drawing, and a redraw that changes nothing raises no change; a bad page is refused and leaves the page
/// as it was; an action's id comes from its label; only the images of the current cards are kept, each once by its
/// hash; a press finds the handler of the card as last drawn; the values a press sent are checked against the card's
/// fields, unless its action does not validate, which takes them as drawn; and a closed page keeps nothing.
/// </summary>
static class DevicePageTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    static T Throws<T>(Action action, string what) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        catch (Exception error) { throw new Exception($"{what}: threw {error.GetType().Name} instead of {typeof(T).Name}: {error.Message}"); }
        throw new Exception($"{what}: did not throw {typeof(T).Name}");
    }

    static Func<UiValues, Task> Nothing => _ => Task.CompletedTask;

    // JPEG bytes as far as a page checks them: the marker, then anything.
    static byte[] Jpeg(int length, int seed = 0)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        return bytes;
    }

    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    static string Text(DevicePage page) => page.Snapshot().ToJsonString();

    static string[] ListIds(DevicePage page) => page.Snapshot()["lists"]!.AsArray().Select(list => (string)list!["id"]!).ToArray();

    [RuntimeSuite("--device-page", Order = 43)]
    public static async Task Run()
    {
        APageSnapshotsItsLists();
        ReplacingKeepsThePlace();
        BadPagesAreRefused();
        ActionIdsComeFromLabels();
        OnlyCurrentImagesAreKept();
        await FindGivesTheCurrentHandler();
        ValuesAreChecked();
        AnActionCanSkipTheChecks();
        await PagesAreSafeFromAnyThread();
        AClosedPageKeepsNothing();
    }

    /// <summary>Two lists, one counting towards attention, snapshot to the panel's shape, property by property and in order.</summary>
    static void APageSnapshotsItsLists()
    {
        var page = new DevicePage("node_1", "Front door");
        Check(!page.Used && page.Attention == 0, "a new page is used or has attention");
        Check(page.Snapshot().ToJsonString() == """{"nodeKey":"node_1","title":"Front door","attention":0,"lists":[]}""", "a new page: " + Text(page));

        using var picture = SharpImage.Blank(16, 8, SharpColor.Blue);
        var hash = Hash(picture.ToJpeg());
        page.Ui.List("unknown", "Unknown faces", [
            new UiCard("u1")
                .Image(picture)
                .Text("Unknown 1", "3 samples")
                .Field("name", "Name", required: true, suggestions: ["Ivan", "Ana"])
                .Field("note", "Note", "seen twice")
                .Select("into", "Merge into", ["Ivan", "Ana"], "Ana")
                .Number("age", "Age", 30, minimum: 0, maximum: 120)
                .Number("score", "Score")
                .Toggle("notify", "Notify", true)
                .Action("Save", Nothing, style: UiActionStyle.Primary)
                .Action("Delete", Nothing, confirm: "Delete it?", style: UiActionStyle.Danger, validate: false),
            new UiCard("u2").Text("Unknown 2"),
        ], attention: true, empty: "No unknown faces");
        page.Ui.List("people", "People", [new UiCard("p:1").Action("Open", Nothing)]);

        var expected = """
            {"nodeKey":"node_1","title":"Front door","attention":2,"lists":[
              {"id":"unknown","title":"Unknown faces","attention":true,"empty":"No unknown faces","cards":[
                {"id":"u1","image":"HASH","title":"Unknown 1","detail":"3 samples","fields":[
                  {"key":"name","label":"Name","type":"text","value":null,"suggestions":["Ivan","Ana"],"required":true},
                  {"key":"note","label":"Note","type":"text","value":"seen twice","required":false},
                  {"key":"into","label":"Merge into","type":"select","value":"Ana","options":["Ivan","Ana"],"required":false},
                  {"key":"age","label":"Age","type":"number","value":30,"minimum":0,"maximum":120,"required":false},
                  {"key":"score","label":"Score","type":"number","value":null,"required":false},
                  {"key":"notify","label":"Notify","type":"toggle","value":true,"required":false}],
                 "actions":[
                  {"id":"save","label":"Save","style":"primary","confirm":null,"validate":true},
                  {"id":"delete","label":"Delete","style":"danger","confirm":"Delete it?","validate":false}]},
                {"id":"u2","image":null,"title":"Unknown 2","detail":null,"fields":[],"actions":[]}]},
              {"id":"people","title":"People","attention":false,"empty":null,"cards":[
                {"id":"p:1","image":null,"title":null,"detail":null,"fields":[],
                 "actions":[{"id":"open","label":"Open","style":"default","confirm":null,"validate":true}]}]}]}
            """.Replace("HASH", hash);
        var want = JsonNode.Parse(expected)!.ToJsonString();
        var got = Text(page);
        Check(got == want, $"the snapshot:\n  got  {got}\n  want {want}");
        Check(page.Attention == 2, $"attention is {page.Attention}");
        Check(page.Used, "a page with lists is not used");
        Check(page.Image(hash) is { } bytes && bytes.SequenceEqual(picture.ToJpeg()), "the card's image is not kept by its hash");
        // A snapshot is the caller's own: changing it changes nothing of the page.
        page.Snapshot()["lists"]!.AsArray().Clear();
        Check(Text(page) == want, "changing a snapshot changed the page");
        Console.WriteLine("PASS device page: a snapshot has the panel's shape, with the attention lists' cards counted");
    }

    /// <summary>A replaced list keeps its place; Remove answers whether there was one; each change is raised once.</summary>
    static void ReplacingKeepsThePlace()
    {
        var page = new DevicePage("node", "Device");
        var changes = 0;
        page.Changed += () => changes++;
        page.Ui.List("a", "A", [new UiCard("1").Text("one")]);
        page.Ui.List("b", "B", [new UiCard("1").Text("one")], attention: true);
        page.Ui.List("a", "A again", [new UiCard("2").Text("two"), new UiCard("3")]);
        Check(ListIds(page).SequenceEqual(["a", "b"]), "the order after replacing a: " + string.Join(", ", ListIds(page)));
        Check(page.Snapshot()["lists"]![0]!["title"]!.GetValue<string>() == "A again", "the replaced list: " + Text(page));
        Check(changes == 3, $"{changes} changes after three drawings");
        // Drawn again exactly as it is, with a new handler, the page has not changed.
        page.Ui.List("b", "B", [new UiCard("1").Text("one")], attention: true);
        Check(changes == 3, $"{changes} changes after a drawing that changed nothing");
        Check(page.Attention == 1, $"attention is {page.Attention}");

        Check(page.Ui.Remove("a"), "removing a list there was");
        Check(changes == 4, $"{changes} changes after a remove");
        Check(!page.Ui.Remove("a"), "removing a list twice");
        Check(!page.Ui.Remove("never"), "removing a list never drawn");
        Check(changes == 4, $"{changes} changes after removes of no list");
        Check(ListIds(page).SequenceEqual(["b"]), "after removing a: " + string.Join(", ", ListIds(page)));
        page.Ui.List("a", "A", []);
        Check(ListIds(page).SequenceEqual(["b", "a"]), "a list drawn again after its removal goes last: " + string.Join(", ", ListIds(page)));
        Check(changes == 5 && page.Used, $"{changes} changes at the end");
        page.Ui.Remove("a");
        page.Ui.Remove("b");
        Check(page.Used && page.Attention == 0, "a page whose lists were all removed stopped being used, or has attention");
        Console.WriteLine("PASS device page: a replaced list keeps its place, and each change of the page is raised once");
    }

    /// <summary>Every bad page throws ArgumentException in List and leaves the page as it was.</summary>
    static void BadPagesAreRefused()
    {
        var page = new DevicePage("node", "Device");
        var ui = page.Ui;
        ui.List("kept", "Kept", [new UiCard("1").Text("one").Image(Jpeg(64))]);
        var changes = 0;
        page.Changed += () => changes++;
        var before = Text(page);
        var tooLong = new string('x', 501);
        UiCard Card() => new("c");

        var cases = new (string What, Action Draw)[]
        {
            ("an empty list id", () => ui.List("", "T", [])),
            ("a list id starting with a dash", () => ui.List("-a", "T", [])),
            ("a list id with a space", () => ui.List("a b", "T", [])),
            ("a list id of 65 characters", () => ui.List(new string('a', 65), "T", [])),
            ("a null list id", () => ui.List(null!, "T", [])),
            ("a card id with a slash", () => ui.List("l", "T", [new UiCard("a/b")])),
            ("an empty card id", () => ui.List("l", "T", [new UiCard("")])),
            ("a field key with a capital", () => ui.List("l", "T", [Card().Field("Name", "Name")])),
            ("a field key starting with a digit", () => ui.List("l", "T", [Card().Toggle("1a", "On")])),
            ("a field key of 41 characters", () => ui.List("l", "T", [Card().Field(new string('a', 41), "Name")])),
            ("two cards with one id", () => ui.List("l", "T", [Card(), Card()])),
            ("two fields with one key", () => ui.List("l", "T", [Card().Field("name", "Name").Toggle("name", "On")])),
            ("a list title too long", () => ui.List("l", tooLong, [])),
            ("an empty text too long", () => ui.List("l", "T", [], empty: tooLong)),
            ("a card title too long", () => ui.List("l", "T", [Card().Text(tooLong)])),
            ("a card detail too long", () => ui.List("l", "T", [Card().Text("t", tooLong)])),
            ("a field label too long", () => ui.List("l", "T", [Card().Field("name", tooLong)])),
            ("a field value too long", () => ui.List("l", "T", [Card().Field("name", "Name", tooLong)])),
            ("a suggestion too long", () => ui.List("l", "T", [Card().Field("name", "Name", suggestions: [tooLong])])),
            ("an option too long", () => ui.List("l", "T", [Card().Select("pick", "Pick", ["a", tooLong])])),
            ("an action label too long", () => ui.List("l", "T", [Card().Action(tooLong, Nothing)])),
            ("a confirm text too long", () => ui.List("l", "T", [Card().Action("Go", Nothing, confirm: tooLong)])),
            ("201 cards", () => ui.List("l", "T", Enumerable.Range(0, 201).Select(i => new UiCard($"c{i}")))),
            ("9 fields", () => ui.List("l", "T", [Enumerable.Range(0, 9).Aggregate(Card(), (card, i) => card.Toggle($"f{i}", "F"))])),
            ("5 actions", () => ui.List("l", "T", [Enumerable.Range(0, 5).Aggregate(Card(), (card, i) => card.Action($"A{i}", Nothing))])),
            ("an image over 1 MiB", () => ui.List("l", "T", [Card().Image(Jpeg(1024 * 1024 + 1))])),
            ("image bytes without the JPEG marker", () => ui.List("l", "T", [Card().Image([0x89, 0x50, 0x4E, 0x47])])),
            ("empty image bytes", () => ui.List("l", "T", [Card().Image(Array.Empty<byte>())])),
            ("null image bytes", () => ui.List("l", "T", [Card().Image((byte[])null!)])),
            ("a null image", () => ui.List("l", "T", [Card().Image((SharpImage)null!)])),
            ("a select with no options", () => ui.List("l", "T", [Card().Select("pick", "Pick", [])])),
            ("a select whose value is not an option", () => ui.List("l", "T", [Card().Select("pick", "Pick", ["a", "b"], "c")])),
            ("a select with an option twice", () => ui.List("l", "T", [Card().Select("pick", "Pick", ["a", "a"])])),
            ("a number below its minimum", () => ui.List("l", "T", [Card().Number("n", "N", -1, minimum: 0)])),
            ("a number above its maximum", () => ui.List("l", "T", [Card().Number("n", "N", 11, maximum: 10)])),
            ("a number whose minimum is above its maximum", () => ui.List("l", "T", [Card().Number("n", "N", minimum: 5, maximum: 1)])),
            ("a number that is not finite", () => ui.List("l", "T", [Card().Number("n", "N", double.PositiveInfinity)])),
            ("a minimum that is not finite", () => ui.List("l", "T", [Card().Number("n", "N", minimum: double.NegativeInfinity)])),
            ("an action without a handler", () => ui.List("l", "T", [Card().Action("Go", null!)])),
            ("an action style that is none", () => ui.List("l", "T", [Card().Action("Go", Nothing, style: (UiActionStyle)7)])),
            ("an action label of no letter or digit", () => ui.List("l", "T", [Card().Action("…", Nothing)])),
            ("an empty list title", () => ui.List("l", "", [])),
            ("an empty field label", () => ui.List("l", "T", [Card().Field("name", "")])),
            ("null cards", () => ui.List("l", "T", null!)),
            ("a null card", () => ui.List("l", "T", [null!])),
            ("a replacement of the kept list that is bad", () => ui.List("kept", "Kept", [Card(), Card()])),
        };
        foreach (var (what, draw) in cases)
        {
            var error = Throws<ArgumentException>(draw, what);
            Check(error.GetType() == typeof(ArgumentException), $"{what}: threw {error.GetType().Name}");
            Check(error.ParamName == null && !string.IsNullOrWhiteSpace(error.Message), $"{what}: the message names a parameter: {error.Message}");
            Check(Text(page) == before, $"{what}: the page changed to {Text(page)}");
        }
        Check(changes == 0, $"{changes} changes raised by refused pages");

        // The limits themselves are allowed.
        ui.List("l", new string('x', 500), [
            Enumerable.Range(0, 8).Aggregate(new UiCard(new string('c', 64)).Image(Jpeg(1024 * 1024)), (card, i) => card.Toggle($"f{i}", "F")),
            Enumerable.Range(0, 4).Aggregate(new UiCard("a"), (card, i) => card.Action($"A{i}", Nothing)),
            .. Enumerable.Range(0, 198).Select(i => new UiCard($"c{i}")),
        ]);
        Check(page.Snapshot()["lists"]![1]!["cards"]!.AsArray().Count == 200, "200 cards were not drawn");

        // A 21st list is refused; a 20th, and a replacement of any of them, are not.
        var full = new DevicePage("node", "Device");
        for (var i = 0; i < 20; i++) full.Ui.List($"l{i}", "T", []);
        var twenty = Text(full);
        Throws<ArgumentException>(() => full.Ui.List("l20", "T", []), "a 21st list");
        Check(Text(full) == twenty, "a 21st list changed the page");
        full.Ui.List("l7", "Seven", []);
        Check(full.Snapshot()["lists"]![7]!["title"]!.GetValue<string>() == "Seven", "a full page's list was not replaced");
        full.Ui.Remove("l0");
        full.Ui.List("l20", "T", []);
        Console.WriteLine($"PASS device page: {cases.Length + 1} bad pages are refused in List and leave the page as it was");
    }

    /// <summary>An action's id is its label lower-cased with runs of other characters made one '_'; two of one card are refused.</summary>
    static void ActionIdsComeFromLabels()
    {
        var page = new DevicePage("node", "Device");
        page.Ui.List("l", "T", [new UiCard("c")
            .Action("Save", Nothing)
            .Action("Merge into…", Nothing)
            .Action("  Add -- NEW 2 ", Nothing)
            .Action("Löschen", Nothing)]);
        var ids = page.Snapshot()["lists"]![0]!["cards"]![0]!["actions"]!.AsArray().Select(action => (string)action!["id"]!).ToArray();
        Check(ids.SequenceEqual(["save", "merge_into", "add_new_2", "l_schen"]), "the ids: " + string.Join(", ", ids));
        Check(page.Find("l", "c", "merge_into") is { Label: "Merge into…" }, "Find by an id from a label");
        var before = Text(page);
        Throws<ArgumentException>(() => page.Ui.List("l", "T", [new UiCard("c").Action("Save", Nothing).Action("Save", Nothing)]), "Save twice");
        Throws<ArgumentException>(() => page.Ui.List("l", "T", [new UiCard("c").Action("Save", Nothing).Action("SAVE!", Nothing)]), "Save and SAVE!");
        Check(Text(page) == before, "a refused page changed it");
        // Two cards may each have their own Save.
        page.Ui.List("l", "T", [new UiCard("a").Action("Save", Nothing), new UiCard("b").Action("Save", Nothing)]);
        Console.WriteLine("PASS device page: an action's id comes from its label, and two actions of one card with one id are refused");
    }

    /// <summary>Images are kept by their hash only while a current card shows them; equal bytes are one image.</summary>
    static void OnlyCurrentImagesAreKept()
    {
        var page = new DevicePage("node", "Device");
        var hashes = new List<string>();
        for (var i = 0; i < 1000; i++)
        {
            var bytes = Jpeg(256, i);
            page.Ui.List("l", "T", [new UiCard("c").Image(bytes)]);
            hashes.Add(Hash(bytes));
            Check((string?)page.Snapshot()["lists"]![0]!["cards"]![0]!["image"] == hashes[^1], $"drawing {i}: the card's hash");
        }
        Check(page.Image(hashes[^1]) is { } last && Hash(last) == hashes[^1], "the last image is not kept");
        Check(hashes.Take(999).All(hash => page.Image(hash) == null), "an earlier image is still kept");

        // Two cards with the same bytes share one hash; the page took its own copy of them.
        var shared = Jpeg(512, 5000);
        var sharedHash = Hash(shared);
        page.Ui.List("two", "Two", [new UiCard("a").Image(shared), new UiCard("b").Image((byte[])shared.Clone())]);
        var cards = page.Snapshot()["lists"]![1]!["cards"]!.AsArray();
        Check((string?)cards[0]!["image"] == sharedHash && (string?)cards[1]!["image"] == sharedHash, "two cards of equal bytes: " + cards.ToJsonString());
        shared[100] ^= 0xFF;
        var kept = page.Image(sharedHash);
        Check(kept != null && Hash(kept) == sharedHash, "changing the bytes drawn changed the page's image");
        // Replacing one card keeps the image the other still shows; removing both lets it go.
        page.Ui.List("two", "Two", [new UiCard("a").Image(Jpeg(64, 1)), new UiCard("b").Image((byte[])kept!.Clone())]);
        Check(page.Image(sharedHash) != null, "an image another card still shows was dropped");
        page.Ui.Remove("two");
        Check(page.Image(sharedHash) == null && page.Image(Hash(Jpeg(64, 1))) == null, "the images of a removed list are kept");
        Check(page.Image(hashes[^1]) != null, "removing one list dropped another list's image");
        Check(page.Image("not a hash") == null && page.Image(null!) == null, "an unknown hash");

        // A card changed or reused after List does not change the page.
        var card = new UiCard("c").Text("one");
        page.Ui.List("l", "T", [card]);
        var before = Text(page);
        card.Text("two").Field("name", "Name").Action("Go", Nothing).Image(Jpeg(64, 2));
        Check(Text(page) == before && page.Find("l", "c", "go") == null, "changing a card after List changed the page");
        Check(page.Image(hashes[^1]) == null, "the image of a replaced card is kept");
        Console.WriteLine("PASS device page: only the images of the current cards are kept, each once by its SHA-256");
    }

    /// <summary>Find answers the handler of the card as last drawn, and null for anything the page does not have.</summary>
    static async Task FindGivesTheCurrentHandler()
    {
        var page = new DevicePage("node", "Device");
        var ran = new List<string>();
        page.Ui.List("l", "T", [new UiCard("1").Action("Go", _ => { ran.Add("first"); return Task.CompletedTask; })]);
        page.Ui.List("l", "T", [new UiCard("1").Action("Go", _ => { ran.Add("second"); return Task.CompletedTask; }), new UiCard("2").Action("Stay", Nothing)]);
        var press = page.Find("l", "1", "go");
        Check(press is { Label: "Go" }, "no press for the drawn action");
        await press!.Run(press.Values([]));
        Check(ran.SequenceEqual(["second"]), "ran: " + string.Join(", ", ran));
        Check(page.Find("other", "1", "go") == null, "an unknown list");
        Check(page.Find("l", "9", "go") == null, "an unknown card");
        Check(page.Find("l", "1", "stay") == null, "another card's action");
        Check(page.Find("l", "1", "Go") == null, "an action by its label");
        Check(page.Find(null!, null!, null!) == null, "nulls");
        page.Ui.List("l", "T", [new UiCard("2").Action("Stay", Nothing)]);
        Check(page.Find("l", "1", "go") == null, "a card a later List no longer has");
        Check(page.Find("l", "2", "stay") != null, "the card that stayed");
        Console.WriteLine("PASS device page: Find answers the handler of the card as last drawn, and nothing for a card or action gone");
    }

    /// <summary>What a press sent is checked against the card's fields; a field it did not send reads as drawn.</summary>
    static void ValuesAreChecked()
    {
        var page = new DevicePage("node", "Device");
        page.Ui.List("l", "T", [new UiCard("c")
            .Field("name", "Name", required: true)
            .Field("nick", "Nick", "Bobby")
            .Field("note", "Note")
            .Number("age", "Age", 30, minimum: 0, maximum: 120)
            .Number("weight", "Weight", required: true)
            .Select("kind", "Kind", ["cat", "dog"], "cat")
            .Toggle("on", "On", true)
            .Action("Save", Nothing)]);
        var press = page.Find("l", "c", "save")!;
        JsonObject Sent(string json) => JsonNode.Parse(json)!.AsObject();
        string Refused(string json, string what) => Throws<UiRefusal>(() => press.Values(Sent(json)), what).Message;

        Check(Refused("""{"weight":1}""", "no name") == "Name is required.", "the missing name");
        Check(Refused("""{"name":"","weight":1}""", "an empty name") == "Name is required.", "the empty name");
        Check(Refused("""{"name":"  ","weight":1}""", "a blank name") == "Name is required.", "the blank name");
        Check(Refused("""{"name":null,"weight":1}""", "a null name") == "Name is required.", "the null name");
        Check(Refused("""{"name":"Ivan"}""", "no weight") == "Weight is required.", "the missing weight");
        var below = Refused("""{"name":"Ivan","weight":1,"age":-1}""", "an age below 0");
        Check(below == "Age must be at least 0.", "below: " + below);
        var above = Refused("""{"name":"Ivan","weight":1,"age":120.5}""", "an age above 120");
        Check(above == "Age must be at most 120.", "above: " + above);
        Check(Refused("""{"name":"Ivan","weight":1,"age":"5"}""", "an age as text") == "Age must be a number.", "the age as text");
        Check(Refused("""{"name":"Ivan","weight":1,"age":1e400}""", "an age past a double") == "Age must be a number.", "the huge age");
        var option = Refused("""{"name":"Ivan","weight":1,"kind":"bird"}""", "an unknown option");
        Check(option == "Kind must be one of its options.", "option: " + option);
        Check(Refused("""{"name":"Ivan","weight":1,"kind":7}""", "an option as a number") == "Kind must be one of its options.", "the option as a number");
        Check(Refused("""{"name":"Ivan","weight":1,"on":"true"}""", "a toggle as text") == "On must be on or off.", "the toggle as text");
        Check(Refused("""{"name":"Ivan","weight":1,"on":null}""", "a null toggle") == "On must be on or off.", "the null toggle");
        Check(Refused("""{"name":123,"weight":1}""", "a name as a number") == "Name must be text.", "the name as a number");
        Check(Refused("""{"name":["Ivan"],"weight":1}""", "a name as an array") == "Name must be text.", "the name as an array");
        var oversize = Refused($$"""{"name":"{{new string('x', 501)}}","weight":1}""", "an oversize name");
        Check(oversize == "Name takes at most 500 characters.", "oversize: " + oversize);

        var values = press.Values(Sent("""{"name":"Ivan","note":"","weight":72.5,"unknown":{"a":1},"Name":5}"""));
        Check(values["name"] == "Ivan", "name: " + values["name"]);
        Check(values["note"] == "", "an optional empty text: " + values["note"]);
        Check(values["nick"] == "Bobby", "a text not sent: " + values["nick"]);
        Check(values.Number("age") == 30, "a number not sent: " + values.Number("age"));
        Check(values.Number("weight") == 72.5, "weight: " + values.Number("weight"));
        Check(values["kind"] == "cat", "a select not sent: " + values["kind"]);
        Check(values.Toggle("on"), "a toggle not sent");
        Throws<KeyNotFoundException>(() => _ = values["missing"], "a key the card has not");
        Throws<KeyNotFoundException>(() => _ = values["age"], "a number read as text");
        Throws<KeyNotFoundException>(() => values.Number("name"), "a text read as a number");
        Throws<KeyNotFoundException>(() => values.Toggle("kind"), "a select read as a toggle");

        var sent = press.Values(Sent("""{"name":"Ana","nick":"","weight":0,"age":null,"kind":"dog","on":false}"""));
        Check(sent["nick"] == "" && sent.Number("age") == null && sent.Number("weight") == 0 && sent["kind"] == "dog" && !sent.Toggle("on"),
            "the values sent: " + $"{sent["nick"]}, {sent.Number("age")}, {sent.Number("weight")}, {sent["kind"]}, {sent.Toggle("on")}");
        // An optional select may be left without a value, and a text drawn without one reads as empty.
        var empty = press.Values(Sent("""{"name":"Ana","weight":1,"kind":""}"""));
        Check(empty["kind"] == "" && empty["note"] == "", $"an unset select: '{empty["kind"]}', note '{empty["note"]}'");
        Console.WriteLine("PASS device page: what a press sent is checked against the card's fields, and a field not sent reads as drawn");
    }

    /// <summary>
    /// An action drawn with <c>validate: false</c> takes the card's values as drawn, whatever the press sent, and refuses
    /// nothing; the card's other actions still check; and a redraw that changes only that is a change of the page.
    /// </summary>
    static void AnActionCanSkipTheChecks()
    {
        var page = new DevicePage("node", "Device");
        var changes = 0;
        page.Changed += () => changes++;
        UiCard Card(bool validate) => new UiCard("c")
            .Field("name", "Name", required: true)
            .Field("nick", "Nick", "Bobby")
            .Number("count", "Count", 5, minimum: 0, maximum: 10)
            .Number("weight", "Weight", required: true)
            .Select("kind", "Kind", ["cat", "dog"], required: true)
            .Select("pet", "Pet", ["cat", "dog"], "dog")
            .Toggle("on", "On", true)
            .Action("Save", Nothing)
            .Action("Dismiss", Nothing, validate: validate);
        page.Ui.List("l", "T", [Card(validate: false)]);
        JsonObject Sent(string json) => JsonNode.Parse(json)!.AsObject();

        foreach (var json in new[] { """{"name":"","count":999,"extra":1}""", "{}", """{"name":5,"nick":"Bob","count":"x","kind":"bird","on":null,"weight":-1}""" })
        {
            var values = page.Find("l", "c", "dismiss")!.Values(Sent(json));
            Check(values["name"] == "" && values["nick"] == "Bobby" && values.Number("count") == 5 && values.Number("weight") == null
                && values["kind"] == "" && values["pet"] == "dog" && values.Toggle("on"),
                $"{json}: the values as drawn: '{values["name"]}', '{values["nick"]}', {values.Number("count")}, {values.Number("weight")}, '{values["kind"]}', '{values["pet"]}', {values.Toggle("on")}");
        }
        var refused = Throws<UiRefusal>(() => page.Find("l", "c", "save")!.Values(Sent("""{"name":"","count":999,"extra":1}""")), "the card's validating action");
        Check(refused.Message == "Name is required.", "the validating action: " + refused.Message);

        Check(changes == 1, $"{changes} changes after the first drawing");
        page.Ui.List("l", "T", [Card(validate: false)]);
        Check(changes == 1, $"{changes} changes after a drawing that changed nothing");
        page.Ui.List("l", "T", [Card(validate: true)]);
        Check(changes == 2, $"{changes} changes after a drawing that changed only validate");
        Check(page.Snapshot()["lists"]![0]!["cards"]![0]!["actions"]![1]!["validate"]!.GetValue<bool>(), "the redrawn action validates: " + Text(page));
        Throws<UiRefusal>(() => page.Find("l", "c", "dismiss")!.Values(Sent("""{"name":""}""")), "the redrawn action that validates");
        Console.WriteLine("PASS device page: an action that does not validate takes the card's values as drawn and refuses nothing, and a change of validate is a change of the page");
    }

    /// <summary>Drawing, removing, reading and pressing from many threads at once leaves a page that answers consistently.</summary>
    static async Task PagesAreSafeFromAnyThread()
    {
        var page = new DevicePage("node", "Device");
        var changes = 0;
        page.Changed += () => Interlocked.Increment(ref changes);
        var workers = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                var id = $"l{(worker + i) % 10}";
                page.Ui.List(id, "T", [new UiCard($"c{i}").Image(Jpeg(64, worker * 1000 + i)).Action("Go", Nothing)], attention: i % 2 == 0);
                if (i % 7 == 0) page.Ui.Remove(id);
                _ = page.Snapshot();
                _ = page.Find(id, $"c{i}", "go");
                _ = page.Attention;
            }
        })).ToArray();
        await Task.WhenAll(workers);
        var lists = page.Snapshot()["lists"]!.AsArray();
        Check(lists.Count <= 10, $"{lists.Count} lists");
        var hashes = lists.SelectMany(list => list!["cards"]!.AsArray()).Select(card => (string)card!["image"]!).ToArray();
        Check(hashes.All(hash => page.Image(hash) != null), "a current card's image is not kept");
        Check(changes > 0, "no change was raised");
        Console.WriteLine("PASS device page: drawing, removing, reading and finding from many threads at once leave a consistent page");
    }

    /// <summary>A closed page has no lists, handlers or images, and refuses to draw.</summary>
    static void AClosedPageKeepsNothing()
    {
        var page = new DevicePage("node", "Device");
        var bytes = Jpeg(128);
        page.Ui.List("l", "T", [new UiCard("c").Image(bytes).Action("Go", Nothing)], attention: true);
        Check(page.Find("l", "c", "go") != null && page.Image(Hash(bytes)) != null, "the page before closing");
        page.Close();
        Check(page.Find("l", "c", "go") == null, "a closed page finds a handler");
        Check(page.Image(Hash(bytes)) == null, "a closed page keeps an image");
        Check(Text(page) == """{"nodeKey":"node","title":"Device","attention":0,"lists":[]}""", "a closed page's snapshot: " + Text(page));
        Check(page.Attention == 0, $"a closed page's attention is {page.Attention}");
        Throws<ObjectDisposedException>(() => page.Ui.List("l", "T", []), "List on a closed page");
        Throws<ObjectDisposedException>(() => page.Ui.Remove("l"), "Remove on a closed page");
        page.Close();
        Console.WriteLine("PASS device page: a closed page keeps no list, handler or image, and List and Remove throw");
    }
}
