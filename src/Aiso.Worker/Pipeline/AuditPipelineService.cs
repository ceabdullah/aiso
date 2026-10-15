using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using Aiso.Domain.Entities;
using Aiso.Domain.Enums;
using Aiso.Infrastructure.AppStore;
using Aiso.Infrastructure.Engines;
using Aiso.Infrastructure.Notifications;
using Aiso.Infrastructure.Persistence;
using Aiso.Infrastructure.Recommendations;
using Aiso.Infrastructure.Reporting;
using Aiso.Infrastructure.Runtime;
using Aiso.Infrastructure.Scraping;
using Aiso.Infrastructure.Settings;
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
    SiteCrawler crawler,
    AppStoreClient appStore,
    IOptions<CompetitorOptions> competitorOptions,
    AnswerEngineProbe probe,
    IClaudeCliOrchestrator claude,
    AisoMarkdownExporter exporter,
    IAisoNotificationService notifications,
    ISystemSettingsService settings,
    IOptions<WorkspaceOptions> workspace,
    ILogger<AuditPipelineService> logger)
{
    public static readonly ActivitySource ActivitySource = new("Aiso.Pipeline");
    private static readonly Meter Meter = new("Aiso.Pipeline");
    private static readonly Counter<int> AuditsCounter = Meter.CreateCounter<int>("aiso.audits");
    private static readonly Histogram<double> ScoreHistogram = Meter.CreateHistogram<double>("aiso.overall_score");

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

        await probe.SyncSettingsAsync(ct);
        var engines = await probe.GetActiveEnginesAsync(ct);
        logger.LogInformation("Pipeline run {RunId} started for {Count} target(s), {EngineCount} answer engine(s) [{Engines}] → {Folder}.",
            runId, targets.Count, engines.Count, string.Join(", ", engines.Select(e => e.Definition.DisplayName)), runFolder);

        using var progress = new RunProgressReporter(settings, logger, targets.Count, targets.Sum(t => t.TestPrompts.Count) * engines.Count);
        try
        {
            return await RunTargetsAsync(db, targets, engines, progress, runId, started, runFolder, ws, ct);
        }
        finally
        {
            await progress.ClearAsync();
        }
    }

    private async Task<PipelineRunSummary> RunTargetsAsync(AisoDbContext db, List<TargetProfile> targets, IReadOnlyList<ActiveEngine> engines,
        RunProgressReporter progress, Guid runId, DateTime started, string runFolder, WorkspaceOptions ws, CancellationToken ct)
    {
        var audits = new List<(TargetProfile Target, LiveSearchAuditLog Log)>();
        foreach (var target in targets)
        {
            audits.Add((target, await AuditTargetAsync(db, target, runId, runFolder, engines, progress, ct)));
            await progress.TargetDoneAsync(ct);
        }

        await db.SaveChangesAsync(ct);

        await progress.StageAsync(claude.IsEnabled ? "Writing remediation plans" : "Saving results", null, ct);
        var plansExported = 0;
        var alertRows = new List<VisibilityAlertRow>();
        foreach (var (target, log) in audits)
        {
            var plan = log.Status == AuditStatus.Failed
                ? null
                : await GeneratePlanAsync(db, target, log, ws.ToRelative(runFolder),
                    DataFiles(runFolder, target.Code), ct);
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

    private async Task<LiveSearchAuditLog> AuditTargetAsync(
        AisoDbContext db, TargetProfile target, Guid runId, string runFolder, IReadOnlyList<ActiveEngine> engines, RunProgressReporter progress, CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity("Audit target");
        activity?.SetTag("aiso.target", target.Code);
        var sw = Stopwatch.StartNew();
        var log = new LiveSearchAuditLog { TargetProfileId = target.Id, RunId = runId };
        db.LiveSearchAuditLogs.Add(log);

        try
        {
            // A brand channel has no single page: it is measured by the engines' answers alone.
            var isBrand = target.ChannelKind == TargetChannelKind.Brand;
            PageScrape home;
            if (isBrand)
            {
                home = new PageScrape(new ScrapeResult(target.BaseUrl, null, true, null, DateTime.UtcNow, 0, null, [], 0m), []);
            }
            else
            {
                await progress.StageAsync("Reading the page", target.DisplayName, ct);
                home = await scraper.ScrapePageAsync(new Uri(target.BaseUrl), target.ChannelKind, ct);
            }

            var scrape = home.Result;
            var scrapePath = Path.Combine(runFolder, $"{target.Code}.scrape.json");
            await WriteJsonAsync(scrapePath, new { target = Describe(target), scrape }, ct);

            // Web channels: crawl the rest of the site. App channels: Apple's public listing, reviews and peer apps.
            SiteCrawl? site = null;
            AppStoreData? app = null;
            if (target.ChannelKind == TargetChannelKind.MobileAppStore)
            {
                await progress.StageAsync("Reading App Store data", target.DisplayName, ct);
                app = await appStore.GetAsync(target.BaseUrl, ct);
                await WriteJsonAsync(Path.Combine(runFolder, $"{target.Code}.app.json"), new { target = Describe(target), app }, ct);
            }
            else if (scrape.Success && !isBrand)
            {
                await progress.StageAsync("Crawling the site", target.DisplayName, ct);
                site = await crawler.CrawlAsync(new Uri(target.BaseUrl), home, target.ChannelKind, ct);
                await WriteJsonAsync(Path.Combine(runFolder, $"{target.Code}.site.json"), new { target = Describe(target), site }, ct);
            }

            IReadOnlyList<EngineProbeOutcome> probes = [];
            if (target.TestPrompts.Count > 0)
            {
                await progress.StageAsync("Asking AI engines", target.DisplayName, ct);
                probes = await probe.ProbeAsync(target, target.TestPrompts.ToList(), engines, o => progress.AnswerAsync(o.EngineName, ct), ct);
                if (probes.Count == 0)
                {
                    logger.LogWarning("No answer engine is switched on with an API key; skipping {Count} prompt(s) for {Target}.", target.TestPrompts.Count, target.Code);
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

            // Which banks the engines name, and which kinds of sources they rely on.
            var keywords = target.GetBrandKeywords();
            var brand = BrandAnalyzer.Analyze(probes, keywords.Count > 0 ? keywords[0] : target.DisplayName,
                target.GetOwnedDomains(), competitorOptions.Value.Effective);
            if (brand is not null)
            {
                await WriteJsonAsync(Path.Combine(runFolder, $"{target.Code}.brand.json"), new { target = Describe(target), brand }, ct);
            }

            // Pages the engines cited instead of the brand, checked like the brand's own page (web-searching engines only).
            IReadOnlyList<CompetitorPage> competitors = [];
            if (probes.Any(p => p.SearchesWeb && p.Citations.Count > p.OwnedCitations.Count))
            {
                await progress.StageAsync("Reading pages the engines cited", target.DisplayName, ct);
                competitors = await crawler.AnalyzeCitedPagesAsync(probes.Where(p => p.SearchesWeb), target.GetOwnedDomains(), ct);
                await WriteJsonAsync(Path.Combine(runFolder, $"{target.Code}.competitors.json"), new { target = Describe(target), competitors }, ct);
            }

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

            // Scores count only engines that search the web; simulated (plain chat) answers are stored and shown, not scored.
            var evaluated = probes.Where(p => p.Succeeded && p.SearchesWeb).ToList();
            var failed = probes.Count(p => !p.Succeeded);
            log.HttpStatusCode = scrape.HttpStatusCode;
            log.OnPageScore = scrape.OnPageScore;
            log.PromptsEvaluated = evaluated.Count;
            log.PromptsMentioningBrand = evaluated.Count(p => p.MentionsBrand);
            log.PromptsCitingDomain = evaluated.Count(p => p.CitesOwnedDomain);
            log.PromptVisibilityScore = evaluated.Count == 0 ? 0 : Math.Round(100m * evaluated.Count(p => p.IsVisible) / evaluated.Count, 2);
            log.OverallScore = isBrand
                ? log.PromptVisibilityScore
                : AisoScore.Overall(log.OnPageScore, evaluated.Count == 0 ? null : log.PromptVisibilityScore);
            log.SnapshotPath = workspace.Value.ToRelative(scrapePath);
            log.MetricsJson = JsonSerializer.Serialize(new AuditMetricsDocument(scrape.Metrics, scrape.Signals, site, competitors, app, brand), SnapshotJson);
            log.ErrorMessage = Truncate(scrape.Error ?? (failed > 0 ? $"{failed} engine answer(s) failed." : null));
            log.Status = !scrape.Success && probes.All(p => !p.Succeeded) ? AuditStatus.Failed
                : !scrape.Success || failed > 0 ? AuditStatus.PartiallySucceeded
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

    private async Task<AisoRemediationPlan?> GeneratePlanAsync(AisoDbContext db, TargetProfile target, LiveSearchAuditLog log, string runFolderRelative, IReadOnlyList<string> dataFiles, CancellationToken ct)
    {
        if (!claude.IsEnabled)
        {
            logger.LogInformation("Remediation planner disabled; no plan for {Target}.", target.Code);
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
            var result = await claude.RunAsync(AgentPromptBuilder.Build(target, runFolderRelative, language), dataFiles, ct);
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

    private static readonly string[] DataFileKinds = ["scrape", "prompts", "brand", "site", "competitors", "app"];

    /// <summary>The run's data files for a channel that exist (site, app and competitor files are optional).</summary>
    private static List<string> DataFiles(string runFolder, string code) =>
        DataFileKinds
            .Select(kind => Path.Combine(runFolder, $"{code}.{kind}.json"))
            .Where(File.Exists)
            .ToList();

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
