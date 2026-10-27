namespace Aiso.Infrastructure.Scraping;

public sealed class ScraperOptions
{
    public const string SectionName = "Aiso:Scraper";

    /// <summary>Identify the crawler honestly so target owners can recognise it in their logs.</summary>
    public string UserAgent { get; set; } = "AISO-Audit-Bot/1.0 (+internal visibility audit)";

    /// <summary>A phone browser's user agent, still naming AISO: for sites that serve phones different HTML.</summary>
    public string MobileUserAgent { get; set; } =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 AISO-Audit-Bot/1.0";

    public string AcceptLanguage { get; set; } = "tr-TR,tr;q=0.9,en-US;q=0.8,en;q=0.7";

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Hard cap on downloaded HTML to protect the worker from pathological pages.</summary>
    public int MaxResponseBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>Pages crawled per own site (sitemap first, then same-site links). 0 turns the crawl off.</summary>
    public int CrawlMaxPages { get; set; } = 25;

    /// <summary>Pause between requests to the same site, to stay polite.</summary>
    public int CrawlDelayMs { get; set; } = 1000;

    /// <summary>Pages cited instead of the brand that are fetched per channel and audit. 0 turns it off.</summary>
    public int CompetitorPagesMax { get; set; } = 8;

    /// <summary>robots.txt product token; matches the start of <see cref="UserAgent"/>.</summary>
    public string RobotsUserAgent { get; set; } = "AISO-Audit-Bot";
}
