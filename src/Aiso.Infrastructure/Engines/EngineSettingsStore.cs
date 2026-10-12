using Aiso.Domain.Entities;
using Aiso.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aiso.Infrastructure.Engines;

/// <summary>Admin overrides for one engine, read by the probe before each run.</summary>
public sealed record EngineOverride(bool IsEnabled, string? Model, int? MaxSearches);

public interface IEngineSettingsStore
{
    /// <summary>Registers the configured engines and reports which have an API key. Never overwrites admin choices.</summary>
    Task SyncAsync(IReadOnlyDictionary<string, AnswerEngineDefinition> engines, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, EngineOverride>> GetOverridesAsync(CancellationToken cancellationToken);
}

/// <summary>Database-backed store (table <c>AISO_ANSWER_ENGINE</c>) shared by the worker and the dashboard.</summary>
public sealed class EfEngineSettingsStore(IDbContextFactory<AisoDbContext> contextFactory, TimeProvider timeProvider) : IEngineSettingsStore
{
    public async Task SyncAsync(IReadOnlyDictionary<string, AnswerEngineDefinition> engines, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(engines);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.AnswerEngineSettings.ToDictionaryAsync(e => e.EngineKey, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        foreach (var (key, definition) in engines)
        {
            if (!rows.TryGetValue(key, out var row))
            {
                row = new AnswerEngineSetting
                {
                    EngineKey = key,
                    DisplayName = key,
                    Kind = definition.Kind.ToString(),
                    IsEnabled = definition.Enabled,
                    UpdatedBy = "worker",
                };
                db.AnswerEngineSettings.Add(row);
            }

            // Configuration-owned facts only; IsEnabled and the overrides belong to the administrator.
            row.DisplayName = string.IsNullOrWhiteSpace(definition.DisplayName) ? key : definition.DisplayName;
            row.Kind = definition.Kind.ToString();
            row.DefaultModel = string.IsNullOrWhiteSpace(definition.Model) ? null : definition.Model;
            row.KeyConfigured = !string.IsNullOrWhiteSpace(definition.ApiKey);
            row.LastSyncedAtUtc = now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, EngineOverride>> GetOverridesAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.AnswerEngineSettings.AsNoTracking()
            .Select(e => new { e.EngineKey, e.IsEnabled, e.ModelOverride, e.MaxSearchesOverride })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(
            r => r.EngineKey,
            r => new EngineOverride(r.IsEnabled, string.IsNullOrWhiteSpace(r.ModelOverride) ? null : r.ModelOverride.Trim(), r.MaxSearchesOverride),
            StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>No database: configuration alone decides. Used by unit tests.</summary>
public sealed class ConfigurationOnlyEngineSettingsStore : IEngineSettingsStore
{
    public static readonly ConfigurationOnlyEngineSettingsStore Instance = new();

    public Task SyncAsync(IReadOnlyDictionary<string, AnswerEngineDefinition> engines, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyDictionary<string, EngineOverride>> GetOverridesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, EngineOverride>>(new Dictionary<string, EngineOverride>());
}
