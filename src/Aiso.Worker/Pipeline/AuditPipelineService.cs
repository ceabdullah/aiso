using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using Aiso.Domain.Entities;
using Aiso.Domain.Enums;
using Aiso.Infrastructure.Engines;
using Aiso.Infrastructure.Notifications;
using Aiso.Infrastructure.Persistence;
using Aiso.Infrastructure.Reporting;
using Aiso.Infrastructure.Runtime;
using Aiso.Infrastructure.Scraping;
using Aiso.Infrastructure.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aiso.Worker.Pipeline;

public sealed record PipelineRunSummary(Guid RunId, string RunFolder, int TargetsAudited, int PlansExported, bool AlertSent);

/// <summary>
/// One end-to-end AISO run:
/// scrape (C#) → probe prompts (LLM gateway) → persist audit + snapshot to data/ →
/// claude.exe analyses data/ → Markdown export to reports/ → alert if below threshold.
/// </summary>
public sealed class AuditPipelineService(
    IDbContextFactory<AisoDbContext> contextFactory,
    NativeHtmlScraperService scraper,
    AnswerEngineProbe probe,
    IClaudeCliOrchestrator claude,
    AisoMarkdownExporter exporter,
    IAisoNotificationService notifications,
    IOptions<WorkspaceOptions> workspace,
    IOptions<ClaudeCliOptions> cliOptions,
    ILogger<AuditPipelineService> logger)
{
    public static readonly ActivitySource ActivitySource = new("Aiso.Pipeline");
    private static readonly Meter Meter = new("Aiso.Pipeline");
    private static readonly Counter<int> AuditsCounter = Meter.CreateCounter<int>("aiso.audits");
    private static readonly Histogram<double> ScoreHistogram = Meter.CreateHistogram<double>("aiso.overall_score");

    /// <summary>Weighting of the overall score. Prompt visibility is the outcome that matters most for GEO.</summary>
    public const decimal OnPageWeight = 0.4m;
    public const decimal PromptWeight = 0.6m;

    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<PipelineRunSummary> RunAsync(CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity("AISO pipeline run");
        var runId = Guid.CreateVersion7();
        var started = DateTime.UtcNow;
        var ws = workspace.Value;
        var runFolderName = string.Create(CultureInfo.InvariantCulture, $"{started:yyyyMMdd-HHmmss}_{runId.ToString("N")[..8]}");
        var runFolder = Path.Combine(ws.DataPath, runFolderName);
        Directory.CreateDirectory(runFolder);
        activity?.SetTag("aiso.run_id", runId);

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var targets = await db.TargetProfiles
            .Include(t => t.TestPrompts.Where(p => p.IsActive))
            .Where(t => t.IsActive)
            .OrderBy(t => t.Code)
            .ToListAsync(ct);

        var engines = probe.GetActiveEngines();
        logger.LogInformation("Pipeline run {RunId} started for {Count} target(s), {EngineCount} answer engine(s) [{Engines}] → {Folder}.",
            runId, targets.Count, engines.Count, string.Join(", ", engines.Select(e => e.Definition.DisplayName)), runFolder);

        var audits = new List<(TargetProfile Target, LiveSearchAuditLog Log)>();
        foreach (var target in targets)
        {
            audits.Add((target, await AuditTargetAsync(db, target, runId, runFolder, ct)));
        }

        await db.SaveChangesAsync(ct);

        var plansExported = 0;
        var alertRows = new List<VisibilityAlertRow>();
        foreach (var (target, log) in audits)
        {
            var plan = log.Status == AuditStatus.Failed
                ? null
                : await GeneratePlanAsync(db, target, log, ws.ToRelative(runFolder), ct);
            if (plan?.Status == RemediationStatus.Published)
            {
                plansExported++;
            }

            var previous = await db.LiveSearchAuditLogs.AsNoTracking()
                .Where(a => a.TargetProfileId == target.Id && a.RunId != runId && a.Status != AuditStatus.Failed)
                .OrderByDescending(a => a.CreatedAtUtc)
                .Select(a => (decimal?)a.OverallScore)
                .FirstOrDefaultAsync(ct);

            alertRows.Add(new VisibilityAlertRow(target.DisplayName, target.BaseUrl, log.OverallScore, log.OnPageScore,
                log.PromptVisibilityScore, previous, plan?.ReportFilePath));
        }

        await db.SaveChangesAsync(ct);

        var alertSent = await notifications.SendVisibilityAlertAsync(alertRows, ct);
        await WriteJsonAsync(Path.Combine(runFolder, "run-manifest.json"), new
        {
            runId, startedAtUtc = started, finishedAtUtc = DateTime.UtcNow, targets = alertRows, plansExported, alertSent,
        }, ct);

        logger.LogInformation("Pipeline run {RunId} finished: {Plans} plan(s) exported, alert sent: {Alert}.", runId, plansExported, alertSent);
        return new PipelineRunSummary(runId, runFolder, audits.Count, plansExported, alertSent);
    }

    private async Task<LiveSearchAuditLog> AuditTargetAsync(AisoDbContext db, TargetProfile target, Guid runId, string runFolder, CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity("Audit target");
        activity?.SetTag("aiso.target", target.Code);
        var sw = Stopwatch.StartNew();
        var log = new LiveSearchAuditLog { TargetProfileId = target.Id, RunId = runId };
        db.LiveSearchAuditLogs.Add(log);

        try
        {
            var scrape = await scraper.ScrapeAsync(new Uri(target.BaseUrl), target.ChannelKind, ct);
            var scrapePath = Path.Combine(runFolder, $"{target.Code}.scrape.json");
            await WriteJsonAsync(scrapePath, new { target = Describe(target), scrape }, ct);

            IReadOnlyList<EngineProbeOutcome> probes = [];
            if (target.TestPrompts.Count > 0)
            {
                probes = await probe.ProbeAsync(target, target.TestPrompts.ToList(), ct);
                if (probes.Count == 0)
                {
                    logger.LogWarning("No answer engine is configured; skipping {Count} prompt(s) for {Target}.", target.TestPrompts.Count, target.Code);
                }
            }

            await WriteJsonAsync(Path.Combine(runFolder, $"{target.Code}.prompts.json"), new
            {
                target = Describe(target),
                ownedDomains = target.GetOwnedDomains(),
                probes = probes.Select(p => new
                {
                    engine = p.EngineName, p.LanguageCode, prompt = p.PromptText, answer = p.Answer,
                    p.MentionsBrand, p.CitesOwnedDomain, p.MatchedKeywords, citations = p.Citations, p.Error,
                }),
            }, ct);

            foreach (var p in probes)
            {
                log.ProbeResults.Add(new EngineProbeResult
                {
                    PromptId = p.PromptId,
                    EngineKey = p.EngineKey,
                    EngineName = p.EngineName,
                    LanguageCode = p.LanguageCode,
                    PromptText = p.PromptText,
                    MentionsBrand = p.MentionsBrand,
                    CitesOwnedDomain = p.CitesOwnedDomain,
                    MatchedKeywords = p.MatchedKeywords.Count == 0 ? null : string.Join(", ", p.MatchedKeywords),
                    CitationsJson = p.Citations.Count == 0 ? null : JsonSerializer.Serialize(p.Citations),
                    AnswerExcerpt = Truncate(p.Answer),
                    ErrorMessage = Truncate(p.Error),
                    LatencyMs = p.LatencyMs,
                });
            }

            var evaluated = probes.Where(p => p.Succeeded).ToList();
            log.HttpStatusCode = scrape.HttpStatusCode;
            log.OnPageScore = scrape.OnPageScore;
            log.PromptsEvaluated = evaluated.Count;
            log.PromptsMentioningBrand = evaluated.Count(p => p.MentionsBrand);
            log.PromptsCitingDomain = evaluated.Count(p => p.CitesOwnedDomain);
            log.PromptVisibilityScore = evaluated.Count == 0 ? 0 : Math.Round(100m * evaluated.Count(p => p.IsVisible) / evaluated.Count, 2);
            log.OverallScore = evaluated.Count == 0
                ? log.OnPageScore
                : Math.Round(OnPageWeight * log.OnPageScore + PromptWeight * log.PromptVisibilityScore, 2);
            log.SnapshotPath = workspace.Value.ToRelative(scrapePath);
            log.MetricsJson = JsonSerializer.Serialize(new AuditMetricsDocument(scrape.Metrics, scrape.Signals), SnapshotJson);
            log.ErrorMessage = Truncate(scrape.Error ?? (probes.Count > evaluated.Count ? $"{probes.Count - evaluated.Count} engine answer(s) failed." : null));
            log.Status = !scrape.Success && evaluated.Count == 0 ? AuditStatus.Failed
                : !scrape.Success || probes.Count > evaluated.Count ? AuditStatus.PartiallySucceeded
                : AuditStatus.Succeeded;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Audit failed for target {Target}.", target.Code);
            log.Status = AuditStatus.Failed;
            log.ErrorMessage = Truncate(ex.Message);
        }

        log.DurationMs = sw.ElapsedMilliseconds;
        AuditsCounter.Add(1, new KeyValuePair<string, object?>("status", log.Status.ToString()));
        ScoreHistogram.Record((double)log.OverallScore, new KeyValuePair<string, object?>("target", target.Code));
        return log;
    }

    private async Task<AisoRemediationPlan?> GeneratePlanAsync(AisoDbContext db, TargetProfile target, LiveSearchAuditLog log, string runFolderRelative, CancellationToken ct)
    {
        if (!cliOptions.Value.Enabled)
        {
            logger.LogInformation("Claude CLI runtime disabled; no remediation plan for {Target}.", target.Code);
            return null;
        }

        var language = target.PrimaryLanguageCode;
        var plan = new AisoRemediationPlan
        {
            TargetProfileId = target.Id,
            AuditLogId = log.Id,
            LanguageCode = language,
            Title = $"{target.DisplayName} — AISO remediation plan",
        };
        db.AisoRemediationPlans.Add(plan);

        try
        {
            var result = await claude.RunAsync(AgentPromptBuilder.Build(target, runFolderRelative, language), ct);
            plan.RawAgentOutput = result.ResultText;
            plan.AgentSessionId = result.SessionId;

            if (!result.Success)
            {
                plan.Status = RemediationStatus.Failed;
                plan.RawAgentOutput = $"{result.ResultText}\n\n[stderr]\n{result.StandardError}";
                return plan;
            }

            var engineScores = log.ProbeResults.Where(r => r.ErrorMessage is null)
                .GroupBy(r => r.EngineName)
                .Select(g => new EngineScore(g.Key,
                    Math.Round(100m * g.Count(r => r.IsVisible) / g.Count(), 1),
                    Math.Round(100m * g.Count(r => r.CitesOwnedDomain) / g.Count(), 1)))
                .OrderBy(e => e.Engine, StringComparer.Ordinal)
                .ToList();

            var report = await exporter.ExportAsync(new ReportContext(
                target.Code, target.DisplayName, target.BaseUrl, language,
                log.OverallScore, log.OnPageScore, log.PromptVisibilityScore, DateTime.UtcNow, result.SessionId, engineScores),
                result.ResultText, ct);

            plan.MarkdownContent = report.Markdown;
            plan.ReportFilePath = report.RelativePath;
            plan.ItemCount = report.ItemCount;
            plan.Status = report.ItemCount > 0 ? RemediationStatus.Published : RemediationStatus.Draft;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Remediation plan generation failed for {Target}.", target.Code);
            plan.Status = RemediationStatus.Failed;
            plan.RawAgentOutput = ex.Message;
        }

        return plan;
    }

    private static object Describe(TargetProfile t) => new
    {
        t.Code, t.DisplayName, t.BaseUrl, ChannelKind = t.ChannelKind.ToString(), t.PrimaryLanguageCode, BrandKeywords = t.GetBrandKeywords(),
    };

    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, SnapshotJson, ct);
    }

    private static string? Truncate(string? value) => value is { Length: > 1990 } ? value[..1990] : value;
}
