using System.Globalization;
using Aiso.Domain;
using Aiso.Domain.Entities;
using Aiso.Domain.Enums;
using Aiso.Infrastructure.Persistence;
using Aiso.Infrastructure.Scraping;
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
    RoleMappingProvider roleMappings)
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
                .Select(a => new { a.OverallScore, a.OnPageScore, a.PromptVisibilityScore, a.Status, a.CreatedAtUtc })
                .ToListAsync(ct);
            var latest = lastTwo.FirstOrDefault();
            summaries.Add(new TargetSummary(t.Id, t.Code, t.DisplayName, t.BaseUrl, t.ChannelKind,
                latest?.OverallScore, latest?.OnPageScore, latest?.PromptVisibilityScore,
                lastTwo.Count > 1 ? lastTwo[1].OverallScore : null, latest?.Status, latest?.CreatedAtUtc, t.Prompts));
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

        var latestDocs = logs.Where(l => l.Status != AuditStatus.Failed)
            .GroupBy(l => l.Target)
            .Select(g => (Target: g.Key, Doc: AuditMetricsDocument.TryParse(g.Last().MetricsJson)))
            .Where(x => x.Doc is not null)
            .ToList();

        var signalNames = latestDocs.SelectMany(d => d.Doc!.Signals.Select(s => s.Name)).Distinct().ToList();
        var matrix = new SignalMatrix(signalNames,
            latestDocs.Select(d => (d.Target, (IReadOnlyDictionary<string, ScoreSignal>)d.Doc!.Signals.ToDictionary(s => s.Name))).ToList());

        var languages = logs.Where(l => l.Status != AuditStatus.Failed)
            .SelectMany(l => (AuditMetricsDocument.TryParse(l.MetricsJson)?.Prompts ?? []).Select(p => (l.Target, p)))
            .GroupBy(x => (x.Target, x.p.LanguageCode))
            .Select(g => new LanguageVisibility(g.Key.Target, g.Key.LanguageCode, g.Count(), g.Count(x => x.p.MentionsBrand)))
            .OrderBy(l => l.Target).ThenBy(l => l.LanguageCode)
            .ToList();

        var recent = logs.AsEnumerable().Reverse().Take(25)
            .Select(l => new AuditRow(l.CreatedAtUtc, l.Target, l.Status, l.OverallScore, l.HttpStatusCode, l.DurationMs, l.ErrorMessage))
            .ToList();

        return new AnalyticsData(trends, matrix, languages, recent);
    }

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
