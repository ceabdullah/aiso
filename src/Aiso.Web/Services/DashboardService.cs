using System.Globalization;
using System.Text.Json;
using Aiso.Infrastructure.Engines;
using Aiso.Domain;
using Aiso.Domain.Entities;
using Aiso.Domain.Enums;
using Aiso.Infrastructure.Persistence;
using Aiso.Infrastructure.Recommendations;
using Aiso.Infrastructure.Scraping;
using Aiso.Infrastructure.Security;
using Aiso.Infrastructure.Settings;
using Aiso.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Aiso.Web.Services;

/// <summary>
/// Read/write facade for the dashboard. Uses a short-lived DbContext per operation, which is the
/// correct pattern for Blazor Server where a circuit can live for hours.
/// </summary>
public sealed class DashboardService(
    IDbContextFactory<AisoDbContext> contextFactory,
    ISystemSettingsService settings,
    RoleMappingProvider roleMappings,
    EngineApiKeyProtector keyProtector,
    ILogger<DashboardService> logger)
{
    public static readonly string[] SupportedLanguages = ["tr-TR", "en-US", "de-DE", "ar-SA", "ru-RU"];

    public async Task<DashboardSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var targets = await db.TargetProfiles.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.Code)
            .Select(t => new { t.Id, t.Code, t.DisplayName, t.BaseUrl, t.ChannelKind, Prompts = t.TestPrompts.Count(p => p.IsActive) })
            .ToListAsync(ct);

        var summaries = new List<TargetSummary>();
        foreach (var t in targets)
        {
            var lastTwo = await db.LiveSearchAuditLogs.AsNoTracking()
                .Where(a => a.TargetProfileId == t.Id && a.Status != AuditStatus.Failed)
                .OrderByDescending(a => a.CreatedAtUtc).Take(2)
                .Select(a => new { a.Id, a.OverallScore, a.OnPageScore, a.PromptVisibilityScore, a.Status, a.CreatedAtUtc })
                .ToListAsync(ct);
            var latest = lastTwo.FirstOrDefault();

            var engineRows = latest is null ? [] : await db.EngineProbeResults.AsNoTracking()
                .Where(r => r.AuditLogId == latest.Id && r.ErrorMessage == null)
                .Select(r => new { r.EngineName, r.MentionsBrand, r.CitesOwnedDomain })
                .ToListAsync(ct);
            var engines = engineRows.GroupBy(r => r.EngineName)
                .Select(g => new EngineRate(g.Key, g.Count(), g.Count(r => r.MentionsBrand || r.CitesOwnedDomain), g.Count(r => r.CitesOwnedDomain)))
                .OrderBy(e => e.Engine, StringComparer.Ordinal)
                .ToList();
            summaries.Add(new TargetSummary(t.Id, t.Code, t.DisplayName, t.BaseUrl, t.ChannelKind,
                latest?.OverallScore, latest?.OnPageScore, latest?.PromptVisibilityScore,
                lastTwo.Count > 1 ? lastTwo[1].OverallScore : null, latest?.Status, latest?.CreatedAtUtc, t.Prompts, engines));
        }

        var since = DateTime.UtcNow.AddDays(-30);
        return new DashboardSummary(
            summaries,
            await settings.GetIntAsync(SettingKeys.VisibilityAlertThreshold, ct),
            await db.LiveSearchAuditLogs.MaxAsync(a => (DateTime?)a.CreatedAtUtc, ct),
            !string.IsNullOrWhiteSpace(await settings.GetStringAsync(SettingKeys.AuditRunRequestedAtUtc, ct)),
            await db.AisoRemediationPlans.CountAsync(p => p.Status == RemediationStatus.Published && p.CreatedAtUtc >= since, ct),
            await db.LiveSearchAuditLogs.CountAsync(ct));
    }

    public async Task<AnalyticsData> GetAnalyticsAsync(int days, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var since = DateTime.UtcNow.AddDays(-Math.Clamp(days, 1, 3650));

        var logs = await db.LiveSearchAuditLogs.AsNoTracking()
            .Where(a => a.CreatedAtUtc >= since)
            .OrderBy(a => a.CreatedAtUtc)
            .Select(a => new
            {
                a.CreatedAtUtc, a.TargetProfileId, Target = a.TargetProfile!.DisplayName, Code = a.TargetProfile.Code,
                a.Status, a.OverallScore, a.OnPageScore, a.PromptVisibilityScore, a.HttpStatusCode, a.DurationMs, a.ErrorMessage, a.MetricsJson,
            })
            .ToListAsync(ct);

        var trends = logs.Where(l => l.Status != AuditStatus.Failed)
            .GroupBy(l => new { l.Code, l.Target })
            .Select(g => new TargetTrend(g.Key.Code, g.Key.Target,
                g.Select(l => new TrendPoint(l.CreatedAtUtc, l.OverallScore, l.OnPageScore, l.PromptVisibilityScore)).ToList()))
            .ToList();

        // Latest audit per channel that actually has page signals (a run whose page fetch failed has none).
        var latestDocs = logs.Where(l => l.Status != AuditStatus.Failed)
            .GroupBy(l => l.Target)
            .Select(g => (Target: g.Key, Doc: g.Select(l => AuditMetricsDocument.TryParse(l.MetricsJson))
                .LastOrDefault(d => d is { Signals.Count: > 0 })))
            .Where(x => x.Doc is not null)
            .ToList();

        var signalNames = latestDocs.SelectMany(d => d.Doc!.Signals.Select(s => s.Name)).Distinct().ToList();
        var matrix = new SignalMatrix(signalNames,
            latestDocs.Select(d => (d.Target, (IReadOnlyDictionary<string, ScoreSignal>)d.Doc!.Signals.ToDictionary(s => s.Name))).ToList());

        var probes = await db.EngineProbeResults.AsNoTracking()
            .Where(r => r.AuditLog!.CreatedAtUtc >= since && r.ErrorMessage == null)
            .Select(r => new
            {
                Target = r.AuditLog!.TargetProfile!.DisplayName,
                r.AuditLog.TargetProfile.OwnedDomains,
                r.EngineName, r.LanguageCode, r.MentionsBrand, r.CitesOwnedDomain, r.CitationsJson,
            })
            .ToListAsync(ct);

        var languages = probes
            .GroupBy(p => (p.Target, p.LanguageCode))
            .Select(g => new LanguageVisibility(g.Key.Target, g.Key.LanguageCode, g.Count(), g.Count(p => p.MentionsBrand || p.CitesOwnedDomain)))
            .OrderBy(l => l.Target).ThenBy(l => l.LanguageCode)
            .ToList();

        var engines = probes
            .GroupBy(p => (p.Target, p.EngineName))
            .Select(g => new EngineVisibility(g.Key.Target, g.Key.EngineName, g.Count(),
                g.Count(p => p.MentionsBrand), g.Count(p => p.CitesOwnedDomain), g.Count(p => p.MentionsBrand || p.CitesOwnedDomain)))
            .OrderBy(e => e.Target).ThenBy(e => e.Engine)
            .ToList();

        var domains = new Dictionary<string, (int Count, HashSet<string> Engines, bool Owned)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in probes.Where(p => p.CitationsJson is not null))
        {
            var owned = (p.OwnedDomains ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var citation in ParseCitations(p.CitationsJson))
            {
                if (OwnedDomainMatcher.HostOf(citation) is not { } host)
                {
                    continue;
                }

                var isOwned = OwnedDomainMatcher.FindOwned([citation], owned).Count > 0;
                var entry = domains.TryGetValue(host, out var e) ? e : (Count: 0, Engines: new HashSet<string>(StringComparer.Ordinal), Owned: false);
                entry.Engines.Add(p.EngineName);
                domains[host] = (entry.Count + 1, entry.Engines, entry.Owned || isOwned);
            }
        }

        var topDomains = domains
            .OrderByDescending(d => d.Value.Count).ThenBy(d => d.Key, StringComparer.Ordinal)
            .Take(15)
            .Select(d => new CitedDomain(d.Key, d.Value.Count, d.Value.Engines.Order(StringComparer.Ordinal).ToList(), d.Value.Owned))
            .ToList();

        var recent = logs.AsEnumerable().Reverse().Take(25)
            .Select(l => new AuditRow(l.CreatedAtUtc, l.Target, l.Status, l.OverallScore, l.HttpStatusCode, l.DurationMs, l.ErrorMessage))
            .ToList();

        return new AnalyticsData(trends, matrix, languages, recent, engines, topDomains);
    }

    /// <summary>How far back the suggestion feed looks to tell fixed problems from ones that never existed.</summary>
    public const int SuggestionHistoryDays = 90;

    public async Task<SuggestionFeed> GetSuggestionFeedAsync(CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var since = DateTime.UtcNow.AddDays(-SuggestionHistoryDays);

        var targets = await db.TargetProfiles.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.Code)
            .Select(t => new
            {
                t.Id, t.Code, t.DisplayName, t.BaseUrl, t.ChannelKind, t.OwnedDomains, t.PrimaryLanguageCode,
                Questions = t.TestPrompts.Where(p => p.IsActive && p.LanguageCode == t.PrimaryLanguageCode)
                    .OrderBy(p => p.CreatedAtUtc).Select(p => p.PromptText).ToList(),
            })
            .ToListAsync(ct);

        var audits = await db.LiveSearchAuditLogs.AsNoTracking()
            .Where(a => a.CreatedAtUtc >= since && a.Status != AuditStatus.Failed)
            .Select(a => new { a.Id, a.TargetProfileId, a.CreatedAtUtc, a.MetricsJson })
            .ToListAsync(ct);

        var probes = (await db.EngineProbeResults.AsNoTracking()
            .Where(r => r.AuditLog!.CreatedAtUtc >= since && r.AuditLog.Status != AuditStatus.Failed && r.ErrorMessage == null)
            .Select(r => new { r.AuditLogId, r.PromptId, r.PromptText, r.LanguageCode, r.EngineKey, r.EngineName, r.MentionsBrand, r.CitesOwnedDomain, r.CitationsJson })
            .ToListAsync(ct)).ToLookup(r => r.AuditLogId);

        var channels = new List<FeedChannel>();
        var items = new List<FeedItem>();
        foreach (var t in targets)
        {
            var owned = (t.OwnedDomains ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var history = audits.Where(a => a.TargetProfileId == t.Id)
                .Select(a => new AuditSnapshot(a.CreatedAtUtc, AuditMetricsDocument.TryParse(a.MetricsJson),
                    probes[a.Id].Select(p => new ProbeSnapshot(p.PromptId, p.PromptText, p.LanguageCode, p.EngineKey, p.EngineName,
                        p.MentionsBrand, p.CitesOwnedDomain, CitedHostsNotOwned(p.CitationsJson, owned))).ToList()))
                .ToList();

            var channel = new FeedChannel(t.Id, t.Code, t.DisplayName, t.BaseUrl, t.ChannelKind,
                history.Count == 0 ? null : history.Max(h => h.AtUtc),
                history.Any(h => h.Probes.Count > 0),
                new KitContext(t.DisplayName, t.BaseUrl, t.PrimaryLanguageCode, t.Questions));
            channels.Add(channel);
            items.AddRange(VisibilityAdvisor.Analyze(history, owned).Select(s => new FeedItem(channel, s)));
        }

        return new SuggestionFeed(DateTime.UtcNow, channels, items);
    }

    private static List<string> CitedHostsNotOwned(string? citationsJson, IReadOnlyList<string> owned) =>
        ParseCitations(citationsJson)
            .Where(c => OwnedDomainMatcher.FindOwned([c], owned).Count == 0)
            .Select(OwnedDomainMatcher.HostOf)
            .OfType<string>()
            .ToList();

    private static List<EngineCitation> ParseCitations(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<EngineCitation>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task<bool> IsRunRequestedAsync(CancellationToken ct = default) =>
        !string.IsNullOrWhiteSpace(await settings.GetStringAsync(SettingKeys.AuditRunRequestedAtUtc, ct));

    public async Task RequestRunAsync(string? user, CancellationToken ct = default) =>
        await settings.SetAsync(SettingKeys.AuditRunRequestedAtUtc,
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), user, ct);

    // ---- Test prompts -------------------------------------------------------------------------

    public async Task<IReadOnlyList<(Guid Id, string Name)>> GetTargetsAsync(CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rows = await db.TargetProfiles.AsNoTracking().OrderBy(t => t.Code).Select(t => new { t.Id, t.DisplayName }).ToListAsync(ct);
        return rows.Select(r => (r.Id, r.DisplayName)).ToList();
    }

    public async Task<List<PromptEditModel>> GetPromptsAsync(Guid targetId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.AutomatedTestPrompts.AsNoTracking()
            .Where(p => p.TargetProfileId == targetId)
            .OrderBy(p => p.LanguageCode).ThenBy(p => p.CreatedAtUtc)
            .Select(p => new PromptEditModel
            {
                Id = p.Id, TargetProfileId = p.TargetProfileId, PromptText = p.PromptText,
                LanguageCode = p.LanguageCode, Category = p.Category, IsActive = p.IsActive,
            })
            .ToListAsync(ct);
    }

    public async Task SavePromptsAsync(Guid targetId, IEnumerable<PromptEditModel> models, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var existing = await db.AutomatedTestPrompts.Where(p => p.TargetProfileId == targetId).ToDictionaryAsync(p => p.Id, ct);

        foreach (var m in models)
        {
            if (m.Id is { } id && existing.TryGetValue(id, out var entity))
            {
                if (m.IsDeleted)
                {
                    db.AutomatedTestPrompts.Remove(entity);
                    continue;
                }

                entity.PromptText = m.PromptText.Trim();
                entity.LanguageCode = m.LanguageCode;
                entity.Category = string.IsNullOrWhiteSpace(m.Category) ? null : m.Category.Trim();
                entity.IsActive = m.IsActive;
            }
            else if (!m.IsDeleted && !string.IsNullOrWhiteSpace(m.PromptText))
            {
                db.AutomatedTestPrompts.Add(new AutomatedTestPrompt
                {
                    TargetProfileId = targetId,
                    PromptText = m.PromptText.Trim(),
                    LanguageCode = m.LanguageCode,
                    Category = string.IsNullOrWhiteSpace(m.Category) ? null : m.Category.Trim(),
                    IsActive = m.IsActive,
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    // ---- Security mappings --------------------------------------------------------------------

    public async Task<List<RoleMappingEditModel>> GetRoleMappingsAsync(CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.SecurityRoleMappings.AsNoTracking().OrderBy(m => m.Role).ThenBy(m => m.AdGroupCn)
            .Select(m => new RoleMappingEditModel { Id = m.Id, AdGroupCn = m.AdGroupCn, Role = m.Role, IsEnabled = m.IsEnabled, Notes = m.Notes })
            .ToListAsync(ct);
    }

    public async Task SaveRoleMappingsAsync(IEnumerable<RoleMappingEditModel> models, CancellationToken ct = default)
    {
        var list = models.ToList();
        var live = list.Where(m => !m.IsDeleted && !string.IsNullOrWhiteSpace(m.AdGroupCn)).ToList();

        // Lock-out guard: there must always be at least one enabled administrator mapping.
        if (!live.Any(m => m.IsEnabled && m.Role == AisoRole.Administrator))
        {
            throw new InvalidOperationException("At least one enabled Administrator mapping is required.");
        }

        var duplicate = live.GroupBy(m => (Cn: m.AdGroupCn.Trim().ToUpperInvariant(), m.Role)).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Duplicate mapping: {duplicate.Key.Cn} → {duplicate.Key.Role}.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var existing = await db.SecurityRoleMappings.ToDictionaryAsync(m => m.Id, ct);

        foreach (var m in list)
        {
            var cn = AdGroupNameParser.ToCommonName(m.AdGroupCn) ?? string.Empty;
            if (m.Id is { } id && existing.TryGetValue(id, out var entity))
            {
                if (m.IsDeleted || cn.Length == 0)
                {
                    db.SecurityRoleMappings.Remove(entity);
                    continue;
                }

                entity.AdGroupCn = cn;
                entity.Role = m.Role;
                entity.IsEnabled = m.IsEnabled;
                entity.Notes = m.Notes;
            }
            else if (!m.IsDeleted && cn.Length > 0)
            {
                db.SecurityRoleMappings.Add(new SecurityRoleMapping { AdGroupCn = cn, Role = m.Role, IsEnabled = m.IsEnabled, Notes = m.Notes });
            }
        }

        await db.SaveChangesAsync(ct);
        roleMappings.Invalidate();
    }

    // ---- AI engines -------------------------------------------------------------------------

    public async Task<List<EngineEditModel>> GetEnginesAsync(CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var engines = await db.AnswerEngineSettings.AsNoTracking().OrderBy(e => e.DisplayName)
            .Select(e => new EngineEditModel
            {
                Id = e.Id, EngineKey = e.EngineKey, DisplayName = e.DisplayName, Kind = e.Kind, IsEnabled = e.IsEnabled,
                DefaultModel = e.DefaultModel, ModelOverride = e.ModelOverride, MaxSearchesOverride = e.MaxSearchesOverride,
                KeyConfigured = e.KeyConfigured, LastSyncedAtUtc = e.LastSyncedAtUtc, UpdatedBy = e.UpdatedBy, UpdatedAtUtc = e.UpdatedAtUtc,
                HasUiKey = e.ApiKeyCiphertext != null, ApiKeyHint = e.ApiKeyHint,
                ApiKeyUpdatedAtUtc = e.ApiKeyUpdatedAtUtc, ApiKeyUpdatedBy = e.ApiKeyUpdatedBy,
            })
            .ToListAsync(ct);

        var since = DateTime.UtcNow.AddDays(-30);
        var usage = await db.EngineProbeResults.AsNoTracking()
            .Where(r => r.CreatedAtUtc >= since)
            .GroupBy(r => r.EngineKey)
            .Select(g => new
            {
                Key = g.Key,
                Answers = g.Count(),
                Errors = g.Count(r => r.ErrorMessage != null),
                AvgLatency = g.Average(r => (double)r.LatencyMs),
                LastUsed = g.Max(r => r.CreatedAtUtc),
            })
            .ToListAsync(ct);

        var activePrompts = await db.AutomatedTestPrompts.CountAsync(p => p.IsActive && p.TargetProfile!.IsActive, ct);
        var intervalHours = Math.Clamp(await settings.GetIntAsync(SettingKeys.AuditIntervalHours, ct), 1, 24 * 30);
        var runsPerMonth = (int)Math.Ceiling(30 * 24d / intervalHours);

        foreach (var engine in engines)
        {
            var u = usage.FirstOrDefault(x => string.Equals(x.Key, engine.EngineKey, StringComparison.OrdinalIgnoreCase));
            engine.Answers30d = u?.Answers ?? 0;
            engine.Errors30d = u?.Errors ?? 0;
            engine.AvgLatencyMs = (long)Math.Round(u?.AvgLatency ?? 0);
            engine.LastUsedUtc = u?.LastUsed;
            engine.EstimatedAnswersPerMonth = engine.IsEnabled && engine.HasAnyKey ? activePrompts * runsPerMonth : 0;
        }

        return engines;
    }

    public async Task SaveEnginesAsync(IEnumerable<EngineEditModel> models, string? user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(models);
        var list = models.ToList();
        foreach (var m in list)
        {
            if (m.MaxSearchesOverride is < 1 or > 20)
            {
                throw new InvalidOperationException($"{m.DisplayName}: searches per answer must be between 1 and 20.");
            }

            if (m.ModelOverride is { Length: > 256 })
            {
                throw new InvalidOperationException($"{m.DisplayName}: model name is too long.");
            }

            if (!string.IsNullOrWhiteSpace(m.NewApiKey) && EngineApiKeyProtector.Validate(m.NewApiKey, out _) is { } error)
            {
                throw new InvalidOperationException($"{m.DisplayName}: {error}");
            }
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rows = await db.AnswerEngineSettings.ToDictionaryAsync(e => e.Id, ct);
        foreach (var m in list)
        {
            if (!rows.TryGetValue(m.Id, out var row))
            {
                continue;
            }

            var now = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(m.NewApiKey))
            {
                EngineApiKeyProtector.Validate(m.NewApiKey, out var apiKey);
                row.ApiKeyCiphertext = keyProtector.Protect(apiKey);
                row.ApiKeyHint = EngineApiKeyProtector.Hint(apiKey);
                row.ApiKeyUpdatedAtUtc = now;
                row.ApiKeyUpdatedBy = user;
                logger.LogWarning("API key for engine {Engine} was set by {User}.", row.EngineKey, user); // never the key itself
            }
            else if (m.RemoveApiKey && row.ApiKeyCiphertext is not null)
            {
                row.ApiKeyCiphertext = null;
                row.ApiKeyHint = null;
                row.ApiKeyUpdatedAtUtc = now;
                row.ApiKeyUpdatedBy = user;
                logger.LogWarning("API key for engine {Engine} was removed by {User}.", row.EngineKey, user);
            }

            var model = string.IsNullOrWhiteSpace(m.ModelOverride) || m.ModelOverride.Trim() == row.DefaultModel ? null : m.ModelOverride.Trim();
            if (row.IsEnabled == m.IsEnabled && row.ModelOverride == model && row.MaxSearchesOverride == m.MaxSearchesOverride)
            {
                continue;
            }

            row.IsEnabled = m.IsEnabled;
            row.ModelOverride = model;
            row.MaxSearchesOverride = m.MaxSearchesOverride;
            row.UpdatedBy = user;
        }

        await db.SaveChangesAsync(ct);
    }

    // ---- Reports ------------------------------------------------------------------------------

    public async Task<IReadOnlyList<ReportListItem>> GetReportsAsync(CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.AisoRemediationPlans.AsNoTracking()
            .OrderByDescending(p => p.CreatedAtUtc).Take(200)
            .Select(p => new ReportListItem(p.Id, p.Title, p.TargetProfile!.DisplayName, p.LanguageCode, p.Status, p.ItemCount, p.ReportFilePath, p.CreatedAtUtc))
            .ToListAsync(ct);
    }

    public async Task<(string FileName, string Markdown)?> GetReportContentAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var plan = await db.AisoRemediationPlans.AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new { p.ReportFilePath, p.MarkdownContent, p.RawAgentOutput })
            .FirstOrDefaultAsync(ct);

        if (plan is null)
        {
            return null;
        }

        var fileName = Path.GetFileName(plan.ReportFilePath) is { Length: > 0 } f ? f : $"{id:N}_AISO_remediation_plan.md";
        return (fileName, plan.MarkdownContent ?? plan.RawAgentOutput ?? string.Empty);
    }
}
