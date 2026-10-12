using System.Globalization;
using Aiso.Domain;
using Aiso.Infrastructure.Persistence;
using Aiso.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;

namespace Aiso.Worker.Pipeline;

/// <summary>
/// Scheduler for <see cref="AuditPipelineService"/>. Polls once a minute and runs when either
/// <c>AuditIntervalHours</c> has elapsed since the last run (derived from the database, so it
/// survives restarts) or the dashboard has set <c>AuditRunRequestedAtUtc</c>.
/// </summary>
public sealed class AuditPipelineWorker(
    IServiceScopeFactory scopeFactory,
    IDbContextFactory<AisoDbContext> contextFactory,
    ISystemSettingsService settings,
    TimeProvider timeProvider,
    ILogger<AuditPipelineWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval, timeProvider);
        do
        {
            try
            {
                var (due, reason) = await IsRunDueAsync(stoppingToken);
                if (due)
                {
                    logger.LogInformation("Starting audit pipeline ({Reason}).", reason);
                    await settings.SetAsync(SettingKeys.AuditRunRequestedAtUtc, string.Empty, "worker", stoppingToken);

                    await using var scope = scopeFactory.CreateAsyncScope();
                    var pipeline = scope.ServiceProvider.GetRequiredService<AuditPipelineService>();
                    await pipeline.RunAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Audit pipeline iteration failed; will retry on the next poll.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task<(bool Due, string Reason)> IsRunDueAsync(CancellationToken ct)
    {
        var requested = await settings.GetStringAsync(SettingKeys.AuditRunRequestedAtUtc, ct);
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return (true, $"manual request at {requested}");
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var last = await db.LiveSearchAuditLogs.MaxAsync(a => (DateTime?)a.CreatedAtUtc, ct);
        if (last is null)
        {
            return (true, "first run");
        }

        var interval = TimeSpan.FromHours(Math.Clamp(await settings.GetIntAsync(SettingKeys.AuditIntervalHours, ct), 1, 24 * 30));
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return now - last.Value >= interval
            ? (true, string.Create(CultureInfo.InvariantCulture, $"interval {interval.TotalHours}h elapsed"))
            : (false, string.Empty);
    }
}
