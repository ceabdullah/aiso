using Aiso.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aiso.Worker.Maintenance;

public sealed record PurgeResult(int AuditLogsDeleted, int PlansDetached, int SnapshotFoldersDeleted, DateTime CutoffUtc);

/// <summary>Set-based retention purge. Separated from the worker loop so it can be unit-tested on SQLite.</summary>
public static class RetentionPurger
{
    public const int MinRetentionDays = 1;
    public const int MaxRetentionDays = 3650;

    public static async Task<PurgeResult> PurgeAsync(AisoDbContext db, int retentionDays, DateTime nowUtc, string? dataPath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var days = Math.Clamp(retentionDays, MinRetentionDays, MaxRetentionDays);
        var cutoff = nowUtc.AddDays(-days);

        var strategy = db.Database.CreateExecutionStrategy();
        var (deleted, detached) = await strategy.ExecuteAsync(async token =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);

            // Remediation plans are deliverables and outlive their audit log: detach first so the
            // FK (no DB-level cascade, to stay multi-cascade-path safe on SQL Server) never blocks the delete.
            var detachedCount = await db.AisoRemediationPlans
                .Where(p => p.AuditLogId != null && db.LiveSearchAuditLogs.Any(a => a.Id == p.AuditLogId && a.CreatedAtUtc < cutoff))
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.AuditLogId, (Guid?)null), token);

            var deletedCount = await db.LiveSearchAuditLogs
                .Where(a => a.CreatedAtUtc < cutoff)
                .ExecuteDeleteAsync(token);

            await tx.CommitAsync(token);
            return (deletedCount, detachedCount);
        }, ct);

        var folders = PurgeSnapshotFolders(dataPath, cutoff);
        return new PurgeResult(deleted, detached, folders, cutoff);
    }

    private static int PurgeSnapshotFolders(string? dataPath, DateTime cutoffUtc)
    {
        if (string.IsNullOrWhiteSpace(dataPath) || !Directory.Exists(dataPath))
        {
            return 0;
        }

        var count = 0;
        foreach (var dir in new DirectoryInfo(dataPath).EnumerateDirectories())
        {
            if (dir.CreationTimeUtc < cutoffUtc && dir.LastWriteTimeUtc < cutoffUtc)
            {
                dir.Delete(recursive: true);
                count++;
            }
        }

        return count;
    }
}
