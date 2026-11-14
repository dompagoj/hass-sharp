using System.Globalization;
using System.Text.Json.Nodes;
using HassSharp;

public static class VisualTriggerReviewTests
{
    [RuntimeSuite("--visual-trigger-review", Order = 12)]
    public static async Task Run(string? only = null)
    {
        var previousPath = HassPath.HassConfiguration;
        var temporaryPath = Path.Combine(Path.GetTempPath(), "visual-trigger-review-" + Guid.NewGuid());
        HassPath.HassConfiguration = temporaryPath;
        try
        {
            var diagnostics = new DiagnosticsProvider();
            async Task<AutomationAction> Configure(JsonObject config)
            {
                var graph = new JsonObject { ["version"] = 5, ["name"] = "Trigger review", ["nodes"] = new JsonArray(new JsonObject { ["id"] = "t", ["kind"] = "trigger", ["config"] = config }), ["edges"] = new JsonArray() };
                var preview = new VisualGenerator(diagnostics).Preview(graph);
                if (!preview.Valid) throw new Exception(string.Join("; ", preview.Diagnostics));
                var compiled = await new CodeCompiler(diagnostics).CompileSingleFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cs"), preview.Source);
                try { return ((Automation)Activator.CreateInstance(compiled.Assembly.GetType("VisualAutomation")!)!).Configure().Actions.Single(); }
                finally { compiled.Unload(); }
            }
            if (only is null or "entity")
            {
                foreach (var explicitType in new[] { false, true })
                {
                    var config = new JsonObject { ["entity"] = "binary_sensor.motion" };
                    if (explicitType) config["type"] = "entity";
                    var action = await Configure(config);
                    if (action.Entities?.SingleOrDefault()?.EntityId != "binary_sensor.motion") throw new Exception("Omitted and explicit entity trigger types must Configure the motion subscription");
                }
                Console.WriteLine("PASS compiled Configure entity default and explicit type");
            }
            if (only is null or "once")
            {
                var previousZone = Environment.GetEnvironmentVariable("TZ");
                try
                {
                    foreach (var zone in new[] { "UTC", "Europe/Zagreb", "America/New_York" })
                    {
                        Environment.SetEnvironmentVariable("TZ", zone); TimeZoneInfo.ClearCachedData();
                        foreach (var (at, utc) in new[] { ("2050-01-01T12:30Z", "2050-01-01T12:30:00Z"), ("2050-01-01T12:30+01:00", "2050-01-01T11:30:00Z"), ("2050-01-01T12:30-05:30", "2050-01-01T18:00:00Z"), ("2030-01-01T10:00:00Z", "2030-01-01T10:00:00Z"), ("2030-01-01T10:00:00+01:00", "2030-01-01T09:00:00Z"), ("2030-01-01T10:00:00-05:30", "2030-01-01T15:30:00Z"), ("2032-02-29T10:00:00.1234567Z", "2032-02-29T10:00:00.1234567Z") })
                        {
                            var action = await Configure(new JsonObject { ["type"] = "once", ["at"] = at });
                            if (action.Schedules?.Single().AtUtc != DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture)) throw new Exception($"Once instant changed in {zone}: {at}");
                        }
                    }
                }
                finally { Environment.SetEnvironmentVariable("TZ", previousZone); TimeZoneInfo.ClearCachedData(); }
                Console.WriteLine("PASS compiled Configure once instants in UTC, Zagreb and New York");
            }
            if (only is null or "daily")
            {
                VisualPreview Daily(string time, string zone) => new VisualGenerator(diagnostics).Preview(new JsonObject
                {
                    ["version"] = 5, ["name"] = "Daily review",
                    ["nodes"] = new JsonArray(new JsonObject { ["id"] = "t", ["kind"] = "trigger", ["config"] = new JsonObject { ["type"] = "daily", ["time"] = time, ["timeZone"] = zone } }),
                    ["edges"] = new JsonArray(),
                });
                foreach (var (time, hour, minute) in new[] { ("00:00", 0, 0), ("23:59", 23, 59), ("07:30", 7, 30) })
                {
                    var action = await Configure(new JsonObject { ["type"] = "daily", ["time"] = time, ["timeZone"] = "Europe/Zagreb" });
                    if (action.Schedules?.Single() is not { } schedule || schedule.LocalTime != $"{hour:00}:{minute:00}:00" || schedule.TimeZone != "Europe/Zagreb") throw new Exception($"Daily {time} must Configure that time of day");
                }
                // A time both validators refuse never reaches the generator's exact parse: the input says why, nothing throws.
                foreach (var time in new[] { "07:30:00", "7:30", "24:00", "07:30\n", "noon", "" })
                {
                    var preview = Daily(time, "UTC");
                    var expected = time == "" ? "Time of day is required" : "Time of day must be HH:MM in 24-hour time, from 00:00 to 23:59";
                    if (preview.Valid || !preview.Diagnostics.Any(d => d.Severity == 3 && d.NodeId == "t" && d.InputId == "time" && d.Message.Contains(expected)))
                        throw new Exception($"Daily time {JsonValue.Create(time).ToJsonString()} must be refused on its input: " + string.Join("; ", preview.Diagnostics.Select(d => d.Message)));
                }
                // A time zone is this machine's to know: an unknown one is refused on its input by the generator, not thrown.
                var unknown = Daily("07:30", "Nowhere/Atlantis");
                if (unknown.Valid || unknown.Diagnostics.SingleOrDefault(d => d.Severity == 3) is not { NodeId: "t", InputId: "timeZone" } zoneError
                    || zoneError.Message != "Time zone Nowhere/Atlantis is not known; use a name such as Europe/Zagreb or UTC")
                    throw new Exception("An unknown daily time zone must be one error on its input: " + string.Join("; ", unknown.Diagnostics.Select(d => d.Message)));
                Console.WriteLine("PASS daily trigger times as HH:MM and unknown time zones are refused on their inputs, never thrown");
            }
        } finally { HassPath.HassConfiguration = previousPath; if (Directory.Exists(temporaryPath)) Directory.Delete(temporaryPath, true); }
    }
}
