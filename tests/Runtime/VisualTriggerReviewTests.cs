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
            diagnostics.GenerateHassEntities(["binary_sensor.motion"]);
            async Task<AutomationAction> Configure(JsonObject config)
            {
                var graph = new JsonObject { ["version"] = 4, ["name"] = "Trigger review", ["nodes"] = new JsonArray(new JsonObject { ["id"] = "t", ["kind"] = "trigger", ["config"] = config }), ["edges"] = new JsonArray() };
                var preview = new VisualGenerator(diagnostics).Preview(graph);
                if (!preview.Valid) throw new Exception(string.Join("; ", preview.Diagnostics));
                var compiled = await new CodeCompiler(diagnostics).CompileSingleFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cs"), preview.Source, true);
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
        } finally { HassPath.HassConfiguration = previousPath; if (Directory.Exists(temporaryPath)) Directory.Delete(temporaryPath, true); }
    }
}
