using System.Globalization;
using Aiso.Domain;
using Aiso.Domain.Entities;
using Aiso.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aiso.Infrastructure.Settings;

public interface ISystemSettingsService
{
    Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default);
    Task<int> GetIntAsync(string key, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GlobalSystemSetting>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SetAsync(string key, string value, string? updatedBy, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads settings straight from the database on every call so that dashboard edits take effect
/// in the worker on its next loop without a restart. Falls back to <see cref="SettingKeys.Defaults"/>.
/// </summary>
public sealed class SystemSettingsService(IDbContextFactory<AisoDbContext> contextFactory) : ISystemSettingsService
{
    public async Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var value = await db.GlobalSystemSettings.AsNoTracking()
            .Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(cancellationToken);

        return value ?? (SettingKeys.Defaults.TryGetValue(key, out var d) ? d.Value : string.Empty);
    }

    public async Task<int> GetIntAsync(string key, CancellationToken cancellationToken = default)
    {
        var raw = await GetStringAsync(key, cancellationToken);
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return SettingKeys.Defaults.TryGetValue(key, out var d)
            ? int.Parse(d.Value, CultureInfo.InvariantCulture)
            : throw new FormatException($"Setting '{key}' value '{raw}' is not an integer.");
    }

    public async Task<IReadOnlyList<GlobalSystemSetting>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.GlobalSystemSettings.AsNoTracking().OrderBy(s => s.Key).ToListAsync(cancellationToken);
    }

    public async Task SetAsync(string key, string value, string? updatedBy, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var setting = await db.GlobalSystemSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (setting is null)
        {
            setting = new GlobalSystemSetting
            {
                Key = key,
                Value = value,
                Description = SettingKeys.Defaults.TryGetValue(key, out var d) ? d.Description : null,
            };
            db.GlobalSystemSettings.Add(setting);
        }

        setting.Value = value;
        setting.UpdatedBy = updatedBy;
        await db.SaveChangesAsync(cancellationToken);
    }
}
