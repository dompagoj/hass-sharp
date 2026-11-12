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
    public string? Details { get; set; }
    public DateTime? PlannedUtc { get; set; }
    public DateTime ReceivedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    public double? DurationMs { get; set; }
    public string Status { get; set; } = "Pending";
    public string? Error { get; set; }
}
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

/// <summary>One connection per operation, serialized writes; errors propagate to callers.</summary>
public sealed class RunHistoryStore
{
    readonly Lock gate = new();
    readonly string path;
    readonly int retention;
    public RunHistoryStore(string path, int retention = 10000)
    {
        if (retention < 1) throw new ArgumentOutOfRangeException(nameof(retention));
        SqliteNative.Initialize();
        this.path = path;
        this.retention = retention;
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
    public void Save(InvocationRecord run)
    {
        lock (gate)
        {
            run.Details = Bound(run.Details); run.Error = Bound(run.Error);
            using var db = new HistoryContext(path);
            if (db.Runs.Any(r => r.RunId == run.RunId)) db.Runs.Update(run); else db.Runs.Add(run);
            db.SaveChanges();
            // Active runs are never removed. Retain the most recent completed attempts globally.
            var obsolete = db.Runs.Where(r => r.EndUtc != null).OrderByDescending(r => r.ReceivedUtc)
                .ThenByDescending(r => r.RunId).Skip(retention).Select(r => r.RunId).ToArray();
            if (obsolete.Length > 0) db.Runs.Where(r => obsolete.Contains(r.RunId)).ExecuteDelete();
        }
    }
    public List<InvocationRecord> Read(string automationId, int offset, int limit)
    {
        if (offset < 0 || limit < 1 || limit > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (gate)
        {
            using var db = new HistoryContext(path);
            return db.Runs.AsNoTracking().Where(r => r.AutomationId == automationId)
                .OrderByDescending(r => r.ReceivedUtc).ThenByDescending(r => r.RunId).Skip(offset).Take(limit).ToList();
        }
    }
    static string? Bound(string? value) => value?.Length > 2048 ? value[..2048] : value;
}
