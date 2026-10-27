using System.Text.Json;
using Aiso.Domain.Entities;
using Aiso.Domain.Enums;
using Aiso.Infrastructure.AppStore;
using Aiso.Infrastructure.Content;
using Aiso.Infrastructure.Engines;
using Aiso.Infrastructure.Persistence;
using Aiso.Infrastructure.Recommendations;
using Aiso.Infrastructure.Runtime;
using Aiso.Infrastructure.Scraping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aiso.Worker.Content;

/// <summary>
/// Runs the machine steps of the content loop. Writes one queued draft per tick with the planner model, checks
/// content the team marked as published (the page is public and indexable), and watches the latest audits for
/// AI engines citing it. Publishing itself is always done by people.
/// </summary>
public sealed class ContentWorker(
    IDbContextFactory<AisoDbContext> contextFactory,
    IClaudeCliOrchestrator writer,
    IServiceScopeFactory scopeFactory,
    AppStoreClient appStore,
    IOptions<CompetitorOptions> competitorOptions,
    TimeProvider timeProvider,
    ILogger<ContentWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RecheckAfter = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan CitationCheckEvery = TimeSpan.FromMinutes(10);
    private DateTime _lastCitationCheck = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval, timeProvider);
        do
        {
            try
            {
                await WriteNextDraftAsync(stoppingToken);
                await CheckPublishedAsync(stoppingToken);
                var now = timeProvider.GetUtcNow().UtcDateTime;
                if (now - _lastCitationCheck >= CitationCheckEvery)
                {
                    _lastCitationCheck = now;
                    await CheckCitationsAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Content loop iteration failed; will retry on the next poll.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task WriteNextDraftAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var item = await db.ContentItems.Include(c => c.TargetProfile)
            .Where(c => c.Status == ContentStatus.Drafting)
            .OrderBy(c => c.UpdatedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (item?.TargetProfile is not { } target)
        {
            return;
        }

        if (!writer.IsEnabled)
        {
            Fail(item, "The writing model is switched off on this worker (Aiso:Planner).");
            await db.SaveChangesAsync(ct);
            return;
        }

        logger.LogInformation("Writing a {Kind} draft for “{Question}”.", item.Kind, item.Question);
        var result = await writer.RunAsync(ContentLoop.BuildPrompt(item, await ContextAsync(db, item, target, ct)), [], ct);
        if (result.Success && ContentLoop.ParseDraft(result.ResultText) is { } draft)
        {
            item.Draft = draft.Body;
            item.ImagePrompt = item.Kind == ContentKind.SocialPost ? draft.ImagePrompt : null;
            item.Status = ContentStatus.InReview;
            item.Error = null;
            item.UpdatedBy = "writer";
        }
        else
        {
            Fail(item, string.IsNullOrWhiteSpace(result.StandardError) ? "The writing model gave no answer." : result.StandardError);
        }

        await db.SaveChangesAsync(ct);
    }

    private static void Fail(ContentItem item, string error)
    {
        item.Status = ContentStatus.Idea;
        item.Error = error.Length > 1990 ? error[..1990] : error;
    }

    private async Task<WritingContext> ContextAsync(AisoDbContext db, ContentItem item, TargetProfile target, CancellationToken ct)
    {
        var keywords = target.GetBrandKeywords();
        var brand = keywords.Count > 0 ? keywords[0] : target.DisplayName;
        var since = timeProvider.GetUtcNow().UtcDateTime.AddDays(-30);
        var answers = await db.EngineProbeResults.AsNoTracking()
            .Where(r => r.AuditLog!.TargetProfileId == target.Id && r.PromptText == item.Question && r.CreatedAtUtc >= since && r.ErrorMessage == null)
            .OrderByDescending(r => r.CreatedAtUtc)
            .Select(r => new { r.AnswerExcerpt, r.CitationsJson, r.LanguageCode })
            .Take(8)
            .ToListAsync(ct);

        var competitors = competitorOptions.Value.Effective
            .Where(c => answers.Any(a => a.AnswerExcerpt is { } text && BrandMatcher.MatchKeywords(text, [c.Name, .. c.Aliases], a.LanguageCode).Count > 0))
            .Select(c => c.Name).Take(6).ToList();
        var owned = target.GetOwnedDomains();
        var hosts = answers.SelectMany(a => Citations(a.CitationsJson))
            .Where(c => OwnedDomainMatcher.FindOwned([c], owned).Count == 0)
            .Select(OwnedDomainMatcher.HostOf).OfType<string>()
            .GroupBy(h => h, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Take(5).Select(g => g.Key).ToList();

        var complaints = new List<string>();
        if (item.Kind == ContentKind.AppStoreText)
        {
            var json = await db.LiveSearchAuditLogs.AsNoTracking()
                .Where(a => a.TargetProfileId == target.Id && a.MetricsJson != null)
                .OrderByDescending(a => a.CreatedAtUtc).Select(a => a.MetricsJson).FirstOrDefaultAsync(ct);
            complaints = AuditMetricsDocument.TryParse(json)?.App?.RecentReviews.Where(r => r.Rating <= 2)
                .Select(r => r.Title).OfType<string>().Take(6).ToList() ?? [];
        }

        return new WritingContext(brand, target.DisplayName, target.BaseUrl, competitors, hosts, complaints);
    }

    private static List<EngineCitation> Citations(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<EngineCitation>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task CheckPublishedAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var due = timeProvider.GetUtcNow().UtcDateTime - RecheckAfter;
        var items = await db.ContentItems.Include(c => c.TargetProfile)
            .Where(c => c.Status == ContentStatus.Published && c.LiveUrl != null && (c.LiveCheckedAtUtc == null || c.LiveCheckedAtUtc < due))
            .OrderBy(c => c.UpdatedAtUtc).Take(3)
            .ToListAsync(ct);
        if (items.Count == 0)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var scraper = scope.ServiceProvider.GetRequiredService<NativeHtmlScraperService>();
        foreach (var item in items)
        {
            var (live, note) = item.Kind switch
            {
                // Social networks need a sign-in to show posts to a crawler, so the saved link is the record.
                ContentKind.SocialPost => (true, "Link saved. Social networks need a sign-in, so AISO can't open the post itself."),
                ContentKind.AppStoreText => await CheckListingAsync(item, ct),
                _ => ContentLoop.JudgeLivePage(await scraper.ScrapeAsync(new Uri(item.LiveUrl!), TargetChannelKind.DigitalChannel, ct)),
            };
            item.LiveCheckedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            item.LiveNote = note.Length > 500 ? note[..500] : note;
            if (live)
            {
                item.Status = ContentStatus.Live;
            }

            logger.LogInformation("Checked published content {Url}: {Note}", item.LiveUrl, note);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<(bool, string)> CheckListingAsync(ContentItem item, CancellationToken ct)
    {
        var data = await appStore.GetAsync(item.LiveUrl ?? item.TargetProfile!.BaseUrl, ct);
        return data.App is { } app
            ? (true, $"Listing found: {app.Name}, version {app.Version}, description {app.DescriptionLength:N0} characters.")
            : (false, $"Listing not found: {data.Error}");
    }

    private async Task CheckCitationsAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var live = await db.ContentItems.Where(c => c.Status == ContentStatus.Live && c.LiveUrl != null && c.Kind != ContentKind.AppStoreText).ToListAsync(ct);
        foreach (var item in live)
        {
            var since = item.LiveCheckedAtUtc ?? item.UpdatedAtUtc;
            var cited = await db.EngineProbeResults.AsNoTracking()
                .Where(r => r.AuditLog!.TargetProfileId == item.TargetProfileId && r.CreatedAtUtc >= since && r.CitationsJson != null)
                .Select(r => new { r.EngineName, r.CitationsJson })
                .ToListAsync(ct);
            var engines = cited.Where(r => ContentLoop.Cites(r.CitationsJson, item.LiveUrl!)).Select(r => r.EngineName).Distinct().Order(StringComparer.Ordinal).ToList();
            if (engines.Count > 0)
            {
                item.Status = ContentStatus.Cited;
                item.CitedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
                item.CitedBy = string.Join(", ", engines);
                logger.LogInformation("{Url} is cited by {Engines}.", item.LiveUrl, item.CitedBy);
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
