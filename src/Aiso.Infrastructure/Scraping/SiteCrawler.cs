using System.Text.RegularExpressions;
using Aiso.Domain.Enums;
using Aiso.Infrastructure.Engines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aiso.Infrastructure.Scraping;

/// <summary>
/// Crawls a channel's own site and the pages AI engines cite instead of it, politely: robots.txt is obeyed for AISO's
/// user agent, requests to a site are spaced by <see cref="ScraperOptions.CrawlDelayMs"/>, and only public HTML is read.
/// </summary>
public sealed partial class SiteCrawler(NativeHtmlScraperService scraper, IOptions<ScraperOptions> options, ILogger<SiteCrawler> logger)
{
    private static readonly string[] SkippedExtensions =
        [".pdf", ".jpg", ".jpeg", ".png", ".gif", ".svg", ".webp", ".zip", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".mp4", ".mp3", ".css", ".js", ".xml", ".json", ".ico"];

    private readonly Dictionary<string, RobotsTxt> _robots = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Sitemap URLs kept with the crawl: their paths tell which topics have a page, beyond the pages crawled.</summary>
    public const int MaxSitemapUrlsKept = 1500;

    private readonly HashSet<string> _robotsMissing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Crawls the site of <paramref name="home"/>, which has already been scraped (it is not fetched again).</summary>
    public async Task<SiteCrawl> CrawlAsync(Uri start, PageScrape home, TargetChannelKind kind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(home);
        var max = Math.Max(0, options.Value.CrawlMaxPages);
        var pages = new List<ScrapeResult> { home.Result };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Key(start) };
        var queue = new Queue<Uri>();
        var skipped = 0;

        var robots = await RobotsForAsync(start, ct);
        var sitemapUrls = await SitemapUrlsAsync(start, robots, ct);
        foreach (var url in sitemapUrls.Concat(home.Links))
        {
            Enqueue(url);
        }

        while (pages.Count < max && queue.TryDequeue(out var next))
        {
            if (!robots.IsAllowed(next))
            {
                skipped++;
                continue;
            }

            await Task.Delay(options.Value.CrawlDelayMs, ct);
            var page = await scraper.ScrapePageAsync(next, kind, ct);
            pages.Add(page.Result);
            foreach (var link in page.Links)
            {
                Enqueue(link);
            }
        }

        logger.LogInformation("Crawled {Count} page(s) of {Host}; {Skipped} skipped by robots.txt.", pages.Count, start.Host, skipped);
        // Sites that serve phones their own HTML may leave the viewport out for desktop crawlers: ask once as a phone.
        var noViewport = pages.FirstOrDefault(SeoChecks.MissesViewport);
        var phonesGetViewport = false;
        if (noViewport is not null)
        {
            await Task.Delay(options.Value.CrawlDelayMs, ct);
            phonesGetViewport = await scraper.PhonesGetViewportAsync(new Uri(noViewport.Url), ct);
        }

        var facts = new SeoSiteFacts(!_robotsMissing.Contains(start.GetLeftPart(UriPartial.Authority)), robots.Sitemaps.Count > 0,
            sitemapUrls.Select(SeoChecks.UrlKey).ToHashSet(StringComparer.Ordinal), phonesGetViewport);
        return Summarize(pages, skipped, facts) with { SitemapUrls = sitemapUrls.Take(MaxSitemapUrlsKept).Select(u => u.ToString()).ToList() };

        void Enqueue(Uri url)
        {
            if (SameSite(url, start) && !SkippedExtensions.Any(e => url.AbsolutePath.EndsWith(e, StringComparison.OrdinalIgnoreCase)) && seen.Add(Key(url)))
            {
                queue.Enqueue(url);
            }
        }
    }

    /// <summary>
    /// Fetches the pages AI engines cited instead of the brand, most-cited first, and checks them like the brand's page.
    /// Gemini's opaque redirect links are skipped; pages robots.txt disallows are listed but not fetched.
    /// </summary>
    public async Task<IReadOnlyList<CompetitorPage>> AnalyzeCitedPagesAsync(
        IEnumerable<EngineProbeOutcome> probes, IReadOnlyList<string> ownedDomains, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(probes);
        ArgumentNullException.ThrowIfNull(ownedDomains);
        var cited = probes
            .SelectMany(p => p.Citations.Where(c => !p.OwnedCitations.Contains(c)).Select(c => (Citation: c, p.EngineName)))
            .Where(x => Uri.TryCreate(x.Citation.Url, UriKind.Absolute, out var u) && !OwnedDomainMatcher.IsRedirector(u.Host)
                        && OwnedDomainMatcher.FindOwned([x.Citation], ownedDomains).Count == 0)
            .GroupBy(x => Key(new Uri(x.Citation.Url)), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Url: new Uri(g.First().Citation.Url), Count: g.Count(), Engines: g.Select(x => x.EngineName).Distinct().Order(StringComparer.Ordinal).ToList()))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Url.ToString(), StringComparer.Ordinal)
            .Take(Math.Max(0, options.Value.CompetitorPagesMax))
            .ToList();

        var pages = new List<CompetitorPage>();
        foreach (var (url, count, engines) in cited)
        {
            var host = OwnedDomainMatcher.HostOf(new EngineCitation(url.ToString(), null)) ?? url.Host;
            if (!(await RobotsForAsync(url, ct)).IsAllowed(url))
            {
                pages.Add(new CompetitorPage(url.ToString(), host, count, engines, false, "robots.txt disallows crawling", [], 0, []));
                continue;
            }

            await Task.Delay(options.Value.CrawlDelayMs, ct);
            var result = await scraper.ScrapeAsync(url, TargetChannelKind.DigitalChannel, ct);
            pages.Add(new CompetitorPage(url.ToString(), host, count, engines, result.Success, result.Error,
                result.Signals, result.Metrics?.WordCount ?? 0, result.Metrics?.JsonLdTypes ?? []));
        }

        return pages;
    }

    /// <summary>Per-check pass counts across the crawled pages, and the SEO audit when the site facts are known. Static for unit testing.</summary>
    public static SiteCrawl Summarize(IReadOnlyList<ScrapeResult> pages, int skippedByRobots, SeoSiteFacts? facts = null)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var ok = pages.Where(p => p.Success).ToList();
        var checks = ok.SelectMany(p => p.Signals.Select(s => (p.Url, Signal: s)))
            .GroupBy(x => x.Signal.Name, StringComparer.Ordinal)
            .Select(g => new SiteCheck(g.Key, g.Count(x => x.Signal.Passed), g.Count(),
                g.Where(x => !x.Signal.Passed).Select(x => x.Url).Take(3).ToList()))
            .OrderBy(c => c.PassRate).ThenBy(c => c.Signal, StringComparer.Ordinal)
            .ToList();
        var (seo, seoIssues) = facts is null ? (null, null) : SeoChecks.Audit(pages, facts);
        var crawled = pages.Select(p => new CrawledPage(p.Url, p.HttpStatusCode, p.OnPageScore,
            p.Signals.Where(s => !s.Passed).Select(s => s.Name).ToList(), p.Metrics?.WordCount ?? 0,
            p.ElapsedMs, p.Metrics?.Title, SeoChecks.Topics(p.Metrics), seoIssues?.GetValueOrDefault(p.Url))).ToList();
        return new SiteCrawl(pages.Count, skippedByRobots, pages.Count - ok.Count, checks, crawled, seo);
    }

    private async Task<RobotsTxt> RobotsForAsync(Uri url, CancellationToken ct)
    {
        var origin = url.GetLeftPart(UriPartial.Authority);
        if (!_robots.TryGetValue(origin, out var robots))
        {
            var text = await scraper.FetchTextAsync(new Uri(origin + "/robots.txt"), ct);
            if (text is null)
            {
                _robotsMissing.Add(origin);
            }

            robots = RobotsTxt.Parse(text, options.Value.RobotsUserAgent);
            _robots[origin] = robots;
        }

        return robots;
    }

    private async Task<List<Uri>> SitemapUrlsAsync(Uri start, RobotsTxt robots, CancellationToken ct)
    {
        var sitemaps = robots.Sitemaps.Count > 0 ? robots.Sitemaps.Take(2).ToList() : [start.GetLeftPart(UriPartial.Authority) + "/sitemap.xml"];
        var urls = new List<Uri>();
        foreach (var sitemap in sitemaps)
        {
            if (!Uri.TryCreate(sitemap, UriKind.Absolute, out var sitemapUri) || await scraper.FetchTextAsync(sitemapUri, ct) is not { } xml)
            {
                continue;
            }

            var locs = Locations(xml);
            // A sitemap index lists more sitemaps: read the first one, enough for a sample of pages.
            if (xml.Contains("<sitemapindex", StringComparison.OrdinalIgnoreCase) && locs.Count > 0
                && await scraper.FetchTextAsync(locs[0], ct) is { } child)
            {
                locs = Locations(child);
            }

            urls.AddRange(locs);
        }

        return urls;
    }

    /// <summary>The &lt;loc&gt; URLs of a sitemap. Static for unit testing.</summary>
    public static List<Uri> Locations(string xml) =>
        LocRegex().Matches(xml ?? string.Empty)
            .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value.Trim()))
            .Select(s => Uri.TryCreate(s, UriKind.Absolute, out var u) ? u : null)
            .OfType<Uri>()
            .ToList();

    private static bool SameSite(Uri url, Uri start) =>
        string.Equals(Bare(url.Host), Bare(start.Host), StringComparison.OrdinalIgnoreCase);

    private static string Bare(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;

    private static string Key(Uri url) => Bare(url.Host) + url.AbsolutePath.TrimEnd('/');

    [GeneratedRegex(@"<loc>\s*(.*?)\s*</loc>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex LocRegex();
}
