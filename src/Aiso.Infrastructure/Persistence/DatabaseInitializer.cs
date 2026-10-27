using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aiso.Infrastructure.Persistence;

/// <summary>
/// Applies pending EF Core migrations and seeds reference data on startup.
/// Runs inside the dedicated Aspire migration node; dependants are gated on its completion
/// (<c>WaitForCompletion</c>) so web and worker never see a half-migrated schema.
/// </summary>
public sealed class DatabaseInitializer(
    IDbContextFactory<AisoDbContext> contextFactory,
    DatabaseSeeder seeder,
    IHostApplicationLifetime lifetime,
    ILogger<DatabaseInitializer> logger) : BackgroundService
{
    public static readonly ActivitySource ActivitySource = new("Aiso.DatabaseInitializer");

    /// <summary>When true (default) the host stops once initialization completes — i.e. a run-to-completion job.</summary>
    public bool StopHostWhenDone { get; init; } = true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var activity = ActivitySource.StartActivity("Migrating database", ActivityKind.Client);
        try
        {
            await InitializeAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            activity?.AddException(ex);
            logger.LogCritical(ex, "Database initialization failed.");
            Environment.ExitCode = 1;
            throw;
        }
        finally
        {
            if (StopHostWhenDone)
            {
                lifetime.StopApplication();
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        logger.LogInformation("Applying {Count} pending migration(s) using provider {Provider}: {Migrations}",
            pending.Count, context.Database.ProviderName, string.Join(", ", pending));

        // Wrapped in the provider's execution strategy so transient failures (container still booting) retry.
        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(ct => context.Database.MigrateAsync(ct), cancellationToken);

        await seeder.SeedAsync(context, cancellationToken);
        logger.LogInformation("Database initialization complete.");
    }
}
