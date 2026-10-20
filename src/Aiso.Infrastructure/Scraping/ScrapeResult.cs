namespace Aiso.Infrastructure.Scraping;

public sealed record HeadingMetrics(int H1Count, int H2Count, int H3Count, IReadOnlyList<string> H1Texts, IReadOnlyList<string> H2Texts);

/// <summary>App Store listing values extracted from the storefront page.</summary>
public sealed record StorefrontMetrics(
    string? AppName,
    decimal? RatingValue,
    long? RatingCount,
    string? Price,
    string? Category,
    string? Seller);

public sealed record PageMetrics(
    string? Title,
    string? MetaDescription,
    string? HtmlLang,
    string? CanonicalUrl,
    bool IsNoIndex,
    HeadingMetrics Headings,
    IReadOnlyList<string> JsonLdTypes,
    IReadOnlyList<string> HreflangAlternates,
    int OpenGraphTagCount,
    int WordCount,
    int ImageCount,
    int ImagesWithAlt,
    StorefrontMetrics? Storefront,
    bool HasViewport = true,
    int HtmlLength = 0);

/// <summary>One weighted on-page signal, kept so the score is explainable in reports.</summary>
public sealed record ScoreSignal(string Name, decimal Weight, bool Passed, string Detail);

public sealed record ScrapeResult(
    string Url,
    int? HttpStatusCode,
    bool Success,
    string? Error,
    DateTime FetchedAtUtc,
    long ElapsedMs,
    PageMetrics? Metrics,
    IReadOnlyList<ScoreSignal> Signals,
    decimal OnPageScore,
    string? FinalUrl = null);

/// <summary>A scraped page plus its links (empty when the fetch failed).</summary>
public sealed record PageScrape(ScrapeResult Result, IReadOnlyList<Uri> Links);

/// <summary>One page of a site crawl, reduced to what the advisor, the SEO view and the plan writer need.</summary>
/// <param name="Topics">Title, H1 and H2 texts: what the page targets, for matching it against customer questions.</param>
/// <param name="SeoIssues">Keys of the <see cref="SeoChecks"/> this page fails.</param>
public sealed record CrawledPage(
    string Url,
    int? HttpStatus,
    decimal Score,
    IReadOnlyList<string> FailedSignals,
    int WordCount,
    long ResponseMs = 0,
    string? Title = null,
    string? Topics = null,
    IReadOnlyList<string>? SeoIssues = null);

/// <summary>How many crawled pages pass one on-page check, with a few failing URLs as examples.</summary>
public sealed record SiteCheck(string Signal, int Passed, int Total, IReadOnlyList<string> FailingExamples)
{
    public decimal PassRate => Total == 0 ? 0 : Math.Round(100m * Passed / Total, 1);
}

/// <summary>Result of crawling a channel's own site (same host, robots.txt respected).</summary>
public sealed record SiteCrawl(
    int PagesCrawled,
    int PagesSkippedByRobots,
    int BrokenPages,
    IReadOnlyList<SiteCheck> Checks,
    IReadOnlyList<CrawledPage> Pages,
    SeoAudit? Seo = null);

/// <summary>A page an AI engine cited instead of the brand, fetched and checked like the brand's own page.</summary>
public sealed record CompetitorPage(
    string Url,
    string Host,
    int Citations,
    IReadOnlyList<string> Engines,
    bool Fetched,
    string? Error,
    IReadOnlyList<ScoreSignal> Signals,
    int WordCount,
    IReadOnlyList<string> JsonLdTypes);
