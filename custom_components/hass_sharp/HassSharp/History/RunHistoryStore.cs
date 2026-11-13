using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HassSharp;

public sealed class InvocationRecord
{
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");
    public string AutomationId { get; set; } = "";
    public string Revision { get; set; } = "";
    public string Action { get; set; } = "";
    public string Cause { get; set; } = "";
    public string? VisualId { get; set; }
    public long? VisualRevision { get; set; }
    public string? TriggerNodeId { get; set; }
    public string? TriggerLabel { get; set; }
    /// <summary>Trigger summary JSON built by <see cref="TriggerSummary"/>: always valid, at most 2048 chars.</summary>
    public string? Trigger { get; set; }
    /// <summary>Set by <see cref="RunHistoryStore.SaveTrace"/> only; saving the record never changes it.</summary>
    public bool HasTrace { get; set; }
    public DateTime? PlannedUtc { get; set; }
    public DateTime ReceivedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    public double? DurationMs { get; set; }
    public string Status { get; set; } = "Pending";
    public string? Error { get; set; }
}
/// <summary>The graph and pruned catalog a visual revision ran, keyed by the run <c>Revision</c> (D10).</summary>
public sealed record GraphSnapshot(string Revision, string VisualId, long VisualRevision, string Graph, string Catalog);
public sealed record RunTraceRecord(string RunId, int Format, string Json, int EventCount, bool Truncated, DateTime UpdatedUtc);
internal sealed class AutomationIdentity
{
    public string Path { get; set; } = "";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
}
internal sealed class HistoryContext(string path) : DbContext
{
    public DbSet<InvocationRecord> Runs => Set<InvocationRecord>();
    public DbSet<AutomationIdentity> Identities => Set<AutomationIdentity>();
    protected override void OnConfiguring(DbContextOptionsBuilder options) =>
        options.UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
    protected override void OnModelCreating(ModelBuilder model)
    {
        var utc = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
            value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        model.Entity<InvocationRecord>().Property(r => r.ReceivedUtc).HasConversion(utc);
        model.Entity<InvocationRecord>().Property(r => r.PlannedUtc).HasConversion(utc);
        model.Entity<InvocationRecord>().Property(r => r.StartUtc).HasConversion(utc);
        model.Entity<InvocationRecord>().Property(r => r.EndUtc).HasConversion(utc);
        model.Entity<InvocationRecord>().HasKey(r => r.RunId);
        model.Entity<InvocationRecord>().HasIndex(r => new { r.AutomationId, r.ReceivedUtc, r.RunId });
        model.Entity<InvocationRecord>().HasIndex(r => new { r.ReceivedUtc, r.RunId }).HasDatabaseName("IX_Runs_Completed").HasFilter("EndUtc IS NOT NULL");
        model.Entity<AutomationIdentity>().HasKey(r => r.Path);
    }
}
[DbContext(typeof(HistoryContext))]
[Migration("202611120001_InitialHistory")]
internal sealed class InitialHistory : Migration
{
    protected override void Up(MigrationBuilder m)
    {
        m.Sql("""
            CREATE TABLE "Identities" ("Path" TEXT NOT NULL PRIMARY KEY, "Id" TEXT NOT NULL);
            CREATE TABLE "Runs" (
              "RunId" TEXT NOT NULL PRIMARY KEY, "AutomationId" TEXT NOT NULL, "Revision" TEXT NOT NULL,
              "Action" TEXT NOT NULL, "Cause" TEXT NOT NULL, "Details" TEXT NULL,
              "PlannedUtc" TEXT NULL, "ReceivedUtc" TEXT NOT NULL, "StartUtc" TEXT NULL, "EndUtc" TEXT NULL,
              "DurationMs" REAL NULL, "Status" TEXT NOT NULL, "Error" TEXT NULL);
            CREATE INDEX "IX_Runs_AutomationId_ReceivedUtc_RunId" ON "Runs" ("AutomationId", "ReceivedUtc", "RunId");
            """);
    }
    protected override void Down(MigrationBuilder m) { m.DropTable("Runs"); m.DropTable("Identities"); }
}

[DbContext(typeof(HistoryContext))]
[Migration("202611120002_ScheduleOccurrences")]
internal sealed class ScheduleOccurrences : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("CREATE TABLE ScheduleOccurrences (Id TEXT NOT NULL PRIMARY KEY)");
    protected override void Down(MigrationBuilder m) => m.DropTable("ScheduleOccurrences");
}

[DbContext(typeof(HistoryContext))]
[Migration("202611120003_CompletedRetentionIndex")]
internal sealed class CompletedRetentionIndex : Migration
{
    protected override void Up(MigrationBuilder m) =>
        m.Sql("CREATE INDEX IX_Runs_Completed ON Runs (ReceivedUtc, RunId) WHERE EndUtc IS NOT NULL");
    protected override void Down(MigrationBuilder m) => m.Sql("DROP INDEX IX_Runs_Completed");
}

[DbContext(typeof(HistoryContext))]
[Migration("202611130004_RunTracing")]
internal sealed class RunTracing : Migration
{
    protected override void Up(MigrationBuilder m)
    {
        m.Sql("""
            ALTER TABLE Runs ADD COLUMN "VisualId" TEXT NULL;
            ALTER TABLE Runs ADD COLUMN "VisualRevision" INTEGER NULL;
            ALTER TABLE Runs ADD COLUMN "TriggerNodeId" TEXT NULL;
            ALTER TABLE Runs ADD COLUMN "TriggerLabel" TEXT NULL;
            ALTER TABLE Runs ADD COLUMN "Trigger" TEXT NULL;
            ALTER TABLE Runs ADD COLUMN "HasTrace" INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE Runs DROP COLUMN "Details";
            CREATE TABLE "RunTraces" ("RunId" TEXT NOT NULL PRIMARY KEY, "Format" INTEGER NOT NULL, "Json" TEXT NOT NULL,
              "EventCount" INTEGER NOT NULL, "Truncated" INTEGER NOT NULL, "UpdatedUtc" TEXT NOT NULL);
            CREATE TABLE "GraphSnapshots" ("Revision" TEXT NOT NULL PRIMARY KEY, "VisualId" TEXT NOT NULL,
              "VisualRevision" INTEGER NOT NULL, "Graph" TEXT NOT NULL, "Catalog" TEXT NOT NULL, "CreatedUtc" TEXT NOT NULL);
            """);
    }
    protected override void Down(MigrationBuilder m) => m.Sql("""
        DROP TABLE "GraphSnapshots";
        DROP TABLE "RunTraces";
        ALTER TABLE Runs ADD COLUMN "Details" TEXT NULL;
        ALTER TABLE Runs DROP COLUMN "HasTrace";
        ALTER TABLE Runs DROP COLUMN "Trigger";
        ALTER TABLE Runs DROP COLUMN "TriggerLabel";
        ALTER TABLE Runs DROP COLUMN "TriggerNodeId";
        ALTER TABLE Runs DROP COLUMN "VisualRevision";
        ALTER TABLE Runs DROP COLUMN "VisualId";
        """);
}

/// <summary>One connection per operation, serialized writes; errors propagate to callers.</summary>
public sealed class RunHistoryStore
{
    /// <summary>Completed runs kept, newest first.</summary>
    public const int DefaultRetention = 10000;
    /// <summary>Runs whose trace is kept, newest first; older runs keep their row but lose their trace (C8).</summary>
    public const int TraceRetention = 1000;
    readonly Lock gate = new();
    readonly string path;
    readonly int retention;
    readonly int traceRetention;
    public RunHistoryStore(string path, int retention = DefaultRetention) : this(path, retention, TraceRetention) { }
    internal RunHistoryStore(string path, int retention, int traceRetention)
    {
        if (retention < 1) throw new ArgumentOutOfRangeException(nameof(retention));
        if (traceRetention < 1) throw new ArgumentOutOfRangeException(nameof(traceRetention));
        SqliteNative.Initialize();
        this.path = path;
        this.retention = retention;
        this.traceRetention = traceRetention;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var db = new HistoryContext(path);
        db.Database.Migrate();
        db.Runs.Where(r => r.EndUtc == null).ExecuteUpdate(s => s
            .SetProperty(r => r.Status, "Interrupted").SetProperty(r => r.EndUtc, DateTime.UtcNow)
            .SetProperty(r => r.Error, "Runtime stopped before completion was recorded"));
    }
    public bool ClaimOccurrence(string id)
    {
        lock (gate)
        {
            using var db = new HistoryContext(path);
            return db.Database.ExecuteSqlInterpolated($"INSERT OR IGNORE INTO ScheduleOccurrences (Id) VALUES ({id})") == 1;
        }
    }
    public string Identity(string scriptPath)
    {
        lock (gate)
        {
            using var db = new HistoryContext(path);
            var row = db.Identities.Find(scriptPath);
            if (row != null) return row.Id;
            row = new() { Path = scriptPath };
            db.Identities.Add(row);
            db.SaveChanges();
            return row.Id;
        }
    }
    public void Rename(string oldPath, string newPath)
    {
        lock (gate)
        {
            using var db = new HistoryContext(path);
            var row = db.Identities.Find(oldPath) ?? throw new InvalidOperationException("Unknown automation");
            db.Identities.Remove(row);
            db.Identities.Add(new() { Path = newPath, Id = row.Id });
            db.SaveChanges();
        }
    }
    public void Forget(string scriptPath)
    {
        lock (gate) { using var db = new HistoryContext(path); db.Identities.Where(i => i.Path == scriptPath).ExecuteDelete(); }
    }
    /// <summary>
    /// Inserts or updates the run. A visual run passes its <paramref name="snapshot"/>; it is inserted with
    /// the revision's first run (C8), in the same transaction, so retention never sees the run without it.
    /// </summary>
    public void Save(InvocationRecord run, GraphSnapshot? snapshot = null)
    {
        lock (gate)
        {
            run.Error = Bound(run.Error); run.TriggerLabel = Bound(run.TriggerLabel);
            // The summary is never cut: a cut summary is not JSON.
            if (run.Trigger?.Length > TriggerSummary.MaxLength) run.Trigger = null;
            using var db = new HistoryContext(path);
            using var transaction = db.Database.BeginTransaction();
            if (db.Runs.Any(r => r.RunId == run.RunId))
            {
                db.Runs.Update(run);
                db.Entry(run).Property(r => r.HasTrace).IsModified = false;
            }
            else db.Runs.Add(run);
            db.SaveChanges();
            if (snapshot != null && !knownSnapshots.Contains(snapshot.Revision))
            {
                db.Database.ExecuteSqlInterpolated($"""
                    INSERT OR IGNORE INTO GraphSnapshots (Revision, VisualId, VisualRevision, Graph, Catalog, CreatedUtc)
                    VALUES ({snapshot.Revision}, {snapshot.VisualId}, {snapshot.VisualRevision}, {snapshot.Graph}, {snapshot.Catalog}, {Timestamp(DateTime.UtcNow)})
                    """);
            }
            // Active runs are never removed. Retain the most recent completed attempts globally.
            var obsolete = db.Runs.Where(r => r.EndUtc != null).OrderByDescending(r => r.ReceivedUtc)
                .ThenByDescending(r => r.RunId).Skip(retention).Select(r => r.RunId).ToArray();
            if (obsolete.Length > 0)
            {
                db.Runs.Where(r => obsolete.Contains(r.RunId)).ExecuteDelete();
                foreach (var id in obsolete) db.Database.ExecuteSqlInterpolated($"DELETE FROM RunTraces WHERE RunId = {id}");
                db.Database.ExecuteSqlRaw("DELETE FROM GraphSnapshots WHERE Revision NOT IN (SELECT Revision FROM Runs)");
            }
            // Traces are large: only the newest traced runs keep theirs.
            db.Database.ExecuteSqlInterpolated($"""
                UPDATE Runs SET HasTrace = 0 WHERE RunId IN (SELECT t.RunId FROM RunTraces t JOIN Runs r ON r.RunId = t.RunId
                  ORDER BY r.ReceivedUtc DESC, r.RunId DESC LIMIT -1 OFFSET {traceRetention})
                """);
            db.Database.ExecuteSqlInterpolated($"DELETE FROM RunTraces WHERE RunId IN (SELECT t.RunId FROM RunTraces t JOIN Runs r ON r.RunId = t.RunId WHERE r.HasTrace = 0)");
            transaction.Commit();
            if (obsolete.Length > 0) knownSnapshots.Clear();
            else if (snapshot != null) knownSnapshots.Add(snapshot.Revision);
        }
    }
    public List<InvocationRecord> Read(string automationId, int offset, int limit, IReadOnlyCollection<string>? statuses = null)
    {
        if (offset < 0 || limit < 1 || limit > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (gate)
        {
            using var db = new HistoryContext(path);
            var runs = db.Runs.AsNoTracking().Where(r => r.AutomationId == automationId);
            if (statuses is { Count: > 0 }) runs = runs.Where(r => statuses.Contains(r.Status));
            return runs.OrderByDescending(r => r.ReceivedUtc).ThenByDescending(r => r.RunId).Skip(offset).Take(limit).ToList();
        }
    }
    public InvocationRecord? ReadRun(string runId)
    {
        lock (gate) { using var db = new HistoryContext(path); return db.Runs.AsNoTracking().SingleOrDefault(r => r.RunId == runId); }
    }
    /// <summary>Stores the run's single trace row (replacing the previous one) and marks the run as traced. False when the run is unknown.</summary>
    public bool SaveTrace(string runId, string json, int eventCount, bool truncated)
    {
        lock (gate)
        {
            using var db = new HistoryContext(path);
            using var transaction = db.Database.BeginTransaction();
            if (db.Database.ExecuteSqlInterpolated($"UPDATE Runs SET HasTrace = 1 WHERE RunId = {runId}") == 0) return false;
            db.Database.ExecuteSqlInterpolated($"""
                INSERT INTO RunTraces (RunId, Format, Json, EventCount, Truncated, UpdatedUtc)
                VALUES ({runId}, {TraceFormat}, {json}, {eventCount}, {truncated}, {Timestamp(DateTime.UtcNow)})
                ON CONFLICT (RunId) DO UPDATE SET Format = excluded.Format, Json = excluded.Json,
                  EventCount = excluded.EventCount, Truncated = excluded.Truncated, UpdatedUtc = excluded.UpdatedUtc
                """);
            transaction.Commit();
            return true;
        }
    }
    public RunTraceRecord? ReadTrace(string runId) => Query("SELECT RunId, Format, Json, EventCount, Truncated, UpdatedUtc FROM RunTraces WHERE RunId = $key", runId,
        r => new RunTraceRecord(r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetInt32(3), r.GetInt64(4) != 0, Parse(r.GetString(5))));
    public GraphSnapshot? ReadSnapshot(string revision) => Query("SELECT Revision, VisualId, VisualRevision, Graph, Catalog FROM GraphSnapshots WHERE Revision = $key", revision,
        r => new GraphSnapshot(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4)));

    public const int TraceFormat = 1;
    readonly HashSet<string> knownSnapshots = new(StringComparer.Ordinal);
    T? Query<T>(string sql, string key, Func<System.Data.Common.DbDataReader, T> read) where T : class
    {
        lock (gate)
        {
            using var db = new HistoryContext(path);
            var connection = db.Database.GetDbConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            var parameter = command.CreateParameter(); parameter.ParameterName = "$key"; parameter.Value = key;
            command.Parameters.Add(parameter);
            using var reader = command.ExecuteReader();
            return reader.Read() ? read(reader) : null;
        }
    }
    static string Timestamp(DateTime utc) => utc.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
    static DateTime Parse(string value) => DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
    static string? Bound(string? value) => value?.Length > 2048 ? value[..2048] : value;
}
