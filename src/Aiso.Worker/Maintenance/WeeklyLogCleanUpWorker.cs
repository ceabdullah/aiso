using Aiso.Domain;
using Aiso.Infrastructure.Persistence;
using Aiso.Infrastructure.Settings;
using Aiso.Infrastructure.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aiso.Worker.Maintenance;

/// <summary>
/// Data-retention maintenance. Every <c>CleanupIntervalDays</c> (default 7) it reads
/// <c>DataRetentionDays</c> (default 30) from the database — so a slider change in the dashboard
/// applies on the next sweep — and bulk-deletes expired audit logs with <c>ExecuteDeleteAsync</c>.
/// </summary>
public sealed class WeeklyLogCleanUpWorker(
    IDbContextFactory<AisoDbContext> contextFactory,
    ISystemSettingsService settings,
    IOptions<WorkspaceOptions> workspace,
    TimeProvider timeProvider,
    ILogger<WeeklyLogCleanUpWorker> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the rest of the node settle (and avoid contending with the first audit run).
        await Task.Delay(StartupDelay, timeProvider, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await SweepOnceAsync(stoppingToken);

            var intervalDays = Math.Clamp(await SafeGetIntAsync(SettingKeys.CleanupIntervalDays, 7, stoppingToken), 1, 90);
            logger.LogInformation("Next retention sweep in {Days} day(s).", intervalDays);
            await Task.Delay(TimeSpan.FromDays(intervalDays), timeProvider, stoppingToken);
        }
    }

    internal async Task SweepOnceAsync(CancellationToken ct)
    {
        try
        {
            var retentionDays = await settings.GetIntAsync(SettingKeys.DataRetentionDays, ct);
            await using var db = await contextFactory.CreateDbContextAsync(ct);

            var result = await RetentionPurger.PurgeAsync(db, retentionDays, timeProvider.GetUtcNow().UtcDateTime, workspace.Value.DataPath, ct);

            logger.LogInformation(
                "Retention sweep complete (retention {Days}d, cutoff {Cutoff:u}): {Logs} audit log(s) deleted, {Plans} plan(s) detached, {Folders} snapshot folder(s) removed.",
                retentionDays, result.CutoffUtc, result.AuditLogsDeleted, result.PlansDetached, result.SnapshotFoldersDeleted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed sweep must not kill the worker; it retries on the next interval.
            logger.LogError(ex, "Retention sweep failed.");
        }
    }

    private async Task<int> SafeGetIntAsync(string key, int fallback, CancellationToken ct)
    {
        try
        {
            return await settings.GetIntAsync(key, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read setting {Key}; using {Fallback}.", key, fallback);
            return fallback;
        }
    }
}
