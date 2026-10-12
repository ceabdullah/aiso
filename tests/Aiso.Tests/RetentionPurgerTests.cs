using Aiso.Domain.Entities;
using Aiso.Infrastructure.Persistence;
using Aiso.Worker.Maintenance;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Aiso.Tests;

/// <summary>Exercises the real ExecuteUpdate/ExecuteDelete SQL against an in-memory relational database.</summary>
public sealed class RetentionPurgerTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public RetentionPurgerTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    private AisoDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AisoDbContext>().UseSqlite(_connection).Options;
        var db = new AisoDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public async Task PurgeAsync_deletes_expired_logs_and_detaches_their_plans()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc);
        await using var db = CreateContext();

        var target = new TargetProfile { Code = "t", DisplayName = "T", BaseUrl = "https://t" };
        var oldLog = new LiveSearchAuditLog { TargetProfile = target, RunId = Guid.NewGuid() };
        var freshLog = new LiveSearchAuditLog { TargetProfile = target, RunId = Guid.NewGuid() };
        var plan = new AisoRemediationPlan { TargetProfile = target, AuditLog = oldLog, Title = "p", LanguageCode = "tr-TR" };
        db.AddRange(target, oldLog, freshLog, plan);
        await db.SaveChangesAsync(ct);

        // SaveChanges stamps CreatedAtUtc = now; backdate the old row directly.
        await db.LiveSearchAuditLogs.Where(a => a.Id == oldLog.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.CreatedAtUtc, now.AddDays(-31)), ct);
        await db.LiveSearchAuditLogs.Where(a => a.Id == freshLog.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.CreatedAtUtc, now.AddDays(-5)), ct);

        var result = await RetentionPurger.PurgeAsync(db, 30, now, dataPath: null, ct);

        Assert.Equal(1, result.AuditLogsDeleted);
        Assert.Equal(1, result.PlansDetached);
        db.ChangeTracker.Clear();
        Assert.Equal([freshLog.Id], await db.LiveSearchAuditLogs.Select(a => a.Id).ToListAsync(ct));
        var survivingPlan = await db.AisoRemediationPlans.SingleAsync(ct);
        Assert.Null(survivingPlan.AuditLogId);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-10, 1)]
    [InlineData(99999, 3650)]
    public async Task PurgeAsync_clamps_retention_days(int configured, int effective)
    {
        await using var db = CreateContext();
        var now = new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc);
        var result = await RetentionPurger.PurgeAsync(db, configured, now, null, TestContext.Current.CancellationToken);
        Assert.Equal(now.AddDays(-effective), result.CutoffUtc);
    }
}
