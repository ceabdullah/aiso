using System.Globalization;
using System.Text.RegularExpressions;

namespace Aiso.Infrastructure.Scraping;

/// <summary>
/// One technical SEO check over a crawled site. <see cref="Weight"/> sets its share of the SEO health score;
/// <see cref="SiteWide"/> checks are about the site (sitemap, robots.txt), not about individual pages.
/// </summary>
public sealed record SeoCheck(string Key, string Label, string Title, string Why, int Weight, bool SiteWide = false);

/// <summary>A failed check: how many of the checked pages fail it, a few example URLs and an optional note.</summary>
public sealed record SeoIssue(string Key, int Pages, int Total, IReadOnlyList<string> Examples, string? Note, bool SiteWide)
{
    public decimal Share => SiteWide ? 1m : Total == 0 ? 0 : (decimal)Pages / Total;
}

/// <summary>Technical SEO of a crawled site, 0–100 health, with the failed checks most costly first.</summary>
public sealed record SeoAudit(int PagesChecked, bool RobotsTxtFound, int SitemapUrls, bool SitemapDeclared, decimal Health, IReadOnlyList<SeoIssue> Issues);

/// <summary>What the crawl learned about the site beyond its pages.</summary>
/// <param name="SitemapUrls">The sitemap's URLs, normalised with <see cref="SeoChecks.UrlKey"/>.</param>
public sealed record SeoSiteFacts(bool RobotsTxtFound, bool SitemapDeclared, IReadOnlySet<string> SitemapUrls);

/// <summary>
/// Classic technical SEO checks across a crawled site: things a single page's signals can't show (duplicate titles,
/// sitemap conflicts, redirects) or that matter for ranking in the search indexes AI engines ground on.
/// Deterministic and offline over what the crawler fetched.
/// </summary>
public static partial class SeoChecks
{
    public const int SlowMs = 2000;
    public const int HeavyHtmlChars = 400_000;

    public static readonly IReadOnlyList<SeoCheck> All =
    [
        new("broken", "Broken pages", "Fix broken pages linked from the site",
            "Links on the site lead to pages that return an error. Search engines and AI crawlers drop pages they can't load, and broken links lower trust in the site.", 10),
        new("viewport", "No mobile viewport", "Add a mobile viewport tag to every page",
            "Google indexes the mobile version of pages. Without <meta name=\"viewport\">, phones show a zoomed-out desktop page and the page counts as not mobile-friendly.", 10),
        new("no-sitemap", "No XML sitemap", "Publish an XML sitemap",
            "A sitemap lists every page that should be indexed, so Google and Bing (the indexes AI engines search) find new and deep pages quickly.", 8, SiteWide: true),
        new("noindex-in-sitemap", "Noindex pages in the sitemap", "Resolve pages that are in the sitemap but marked noindex",
            "The sitemap asks search engines to index these pages while the pages tell them not to. Decide which is right: make the page indexable, or remove it from the sitemap.", 8),
        new("duplicate-title", "Duplicate titles", "Give every page its own title",
            "Pages with the same title compete with each other, and search engines can't tell which one answers a question. Name the page's own topic first, then the brand.", 8),
        new("slow", "Slow pages", "Speed up slow pages",
            "These pages took over 2 seconds to deliver their HTML. Slow pages rank lower and crawlers fetch fewer pages per visit. Measured from AISO's server: confirm with PageSpeed Insights.", 6),
        new("duplicate-description", "Duplicate meta descriptions", "Write a distinct meta description for each page",
            "Identical descriptions make pages look like duplicates in search results. Summarise each page's own offer in one sentence.", 5),
        new("redirected", "Links through redirects", "Link straight to final URLs instead of redirects",
            "These internal links go through a redirect first. Each hop costs crawl time and weakens link signals; point the links at the final address.", 4),
        new("canonical-elsewhere", "Canonical points elsewhere", "Check pages whose canonical points to another URL",
            "A canonical to a different URL tells search engines to index that URL instead. Right for duplicates, wrong for pages that should rank on their own.", 3),
        new("breadcrumbs", "No breadcrumb markup", "Add BreadcrumbList structured data to inner pages",
            "Breadcrumbs tell search engines and AI crawlers where a page sits in the site (e.g. Bireysel › Kartlar › Kredi Kartları) and show in Google results.", 3),
        new("heavy-html", "Very large HTML", "Slim down very large HTML pages",
            "These pages send over 400 KB of HTML. Crawlers may stop reading before the main content; move inline scripts and data out of the page.", 3),
        new("url-hygiene", "Untidy URLs", "Use short, lowercase URLs with hyphens",
            "Uppercase letters, underscores, query parameters and very long URLs create duplicate addresses and are harder to read and share.", 2),
        new("sitemap-not-declared", "Sitemap not in robots.txt", "List the sitemap in robots.txt",
            "A Sitemap: line in robots.txt lets every crawler find the sitemap, not only those you submitted it to.", 2, SiteWide: true),
        new("no-robots", "No robots.txt", "Publish a robots.txt file",
            "robots.txt is where crawlers look for the sitemap and for which areas to skip. Without it they guess.", 2, SiteWide: true),
    ];

    /// <summary>Not part of the health score: found by matching customer questions to the crawled pages' headings.</summary>
    public static readonly SeoCheck TopicGap = new("topic-gap", "Questions without a page", "Create pages that target the questions customers ask",
        "No crawled page has these questions' main words in its title or headings, so search engines have no page to rank for them, and AI engines no page of yours to cite.", 0);

    private static readonly Dictionary<string, SeoCheck> ByKey = All.Append(TopicGap).ToDictionary(c => c.Key, StringComparer.Ordinal);

    public static SeoCheck? Find(string key) => ByKey.GetValueOrDefault(key);

    /// <summary>Host without www, and path without a trailing slash: one key for the variants of a page's URL.</summary>
    public static string UrlKey(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var host = url.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? url.Host[4..] : url.Host;
        return (host + url.AbsolutePath.TrimEnd('/')).ToLowerInvariant();
    }

    /// <summary>Runs every check over the crawled pages. Returns the audit and each page's failed check keys.</summary>
    public static (SeoAudit Audit, IReadOnlyDictionary<string, List<string>> PageIssues) Audit(IReadOnlyList<ScrapeResult> pages, SeoSiteFacts facts)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(facts);
        var issuesByPage = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var p in pages)
        {
            issuesByPage.TryAdd(p.Url, []);
        }

        var ok = pages.Where(p => p.Success && p.Metrics is not null).ToList();
        var found = new List<SeoIssue>();

        void Add(string key, IReadOnlyList<ScrapeResult> failing, int total, Func<ScrapeResult, string>? example = null, string? note = null)
        {
            if (failing.Count == 0)
            {
                return;
            }

            foreach (var p in failing)
            {
                issuesByPage[p.Url].Add(key);
            }

            found.Add(new SeoIssue(key, failing.Count, total, failing.Take(3).Select(example ?? (p => p.Url)).ToList(), note, false));
        }

        Add("broken", pages.Where(p => !p.Success).ToList(), pages.Count,
            p => $"{p.Url} ({p.HttpStatusCode?.ToString(CultureInfo.InvariantCulture) ?? "no answer"})");
        Add("viewport", ok.Where(p => p.Metrics!.HtmlLength > 0 && !p.Metrics.HasViewport).ToList(), ok.Count);
        Add("noindex-in-sitemap", ok.Where(p => p.Metrics!.IsNoIndex && facts.SitemapUrls.Contains(UrlKey(new Uri(p.Url)))).ToList(), ok.Count);

        var (dupTitles, titleNote) = Duplicates(ok, m => m.Title);
        Add("duplicate-title", dupTitles, ok.Count, note: titleNote);
        Add("slow", ok.Where(p => p.ElapsedMs > SlowMs).ToList(), ok.Count,
            p => string.Create(CultureInfo.InvariantCulture, $"{p.Url} ({p.ElapsedMs / 1000m:0.0} s)"));
        var (dupDescriptions, descriptionNote) = Duplicates(ok, m => m.MetaDescription);
        Add("duplicate-description", dupDescriptions, ok.Count, note: descriptionNote);
        Add("redirected", ok.Where(p => p.FinalUrl is not null && p.FinalUrl.TrimEnd('/') != p.Url.TrimEnd('/')).ToList(), ok.Count,
            p => $"{p.Url} → {p.FinalUrl}");
        Add("canonical-elsewhere", ok.Where(CanonicalElsewhere).ToList(), ok.Count, p => $"{p.Url} → {p.Metrics!.CanonicalUrl}");
        Add("breadcrumbs", ok.Where(p => Depth(p.Url) >= 2 && !p.Metrics!.JsonLdTypes.Contains("BreadcrumbList", StringComparer.OrdinalIgnoreCase)).ToList(), ok.Count);
        Add("heavy-html", ok.Where(p => p.Metrics!.HtmlLength > HeavyHtmlChars).ToList(), ok.Count,
            p => string.Create(CultureInfo.InvariantCulture, $"{p.Url} ({p.Metrics!.HtmlLength / 1024:N0} KB)"));
        Add("url-hygiene", ok.Where(p => UntidyUrl(new Uri(p.Url))).ToList(), ok.Count);

        if (facts.SitemapUrls.Count == 0)
        {
            found.Add(new SeoIssue("no-sitemap", 1, 1, [], "No sitemap at the robots.txt Sitemap: URL or /sitemap.xml.", true));
        }
        else if (!facts.SitemapDeclared)
        {
            found.Add(new SeoIssue("sitemap-not-declared", 1, 1, [], "Found at /sitemap.xml, but robots.txt has no Sitemap: line.", true));
        }

        if (!facts.RobotsTxtFound)
        {
            found.Add(new SeoIssue("no-robots", 1, 1, [], "/robots.txt didn't answer.", true));
        }

        var weights = All.ToDictionary(c => c.Key, c => c.Weight, StringComparer.Ordinal);
        var lost = found.Sum(i => weights[i.Key] * i.Share);
        var health = pages.Count == 0 ? 0 : Math.Round(100m * (1 - lost / All.Sum(c => c.Weight)), 1);
        var ordered = found.OrderByDescending(i => weights[i.Key] * i.Share).ThenBy(i => i.Key, StringComparer.Ordinal).ToList();
        return (new SeoAudit(ok.Count, facts.RobotsTxtFound, facts.SitemapUrls.Count, facts.SitemapDeclared, health, ordered), issuesByPage);
    }

    /// <summary>"Title · H1 · H2s" of a page: what it targets.</summary>
    public static string? Topics(PageMetrics? m) =>
        m is null ? null : string.Join(" · ", new[] { m.Title }.Concat(m.Headings.H1Texts).Concat(m.Headings.H2Texts).OfType<string>());

    private static (List<ScrapeResult> Pages, string? Note) Duplicates(List<ScrapeResult> pages, Func<PageMetrics, string?> field)
    {
        var groups = pages
            .Where(p => !string.IsNullOrWhiteSpace(field(p.Metrics!)))
            .GroupBy(p => field(p.Metrics!)!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .OrderByDescending(g => g.Count())
            .ToList();
        var note = groups.Count == 0 ? null
            : string.Create(CultureInfo.InvariantCulture, $"“{Shorten(groups[0].Key, 80)}” is used on {groups[0].Count()} pages");
        return (groups.SelectMany(g => g).ToList(), note);
    }

    private static bool CanonicalElsewhere(ScrapeResult p)
    {
        var page = new Uri(p.FinalUrl ?? p.Url);
        return !string.IsNullOrWhiteSpace(p.Metrics!.CanonicalUrl)
               && Uri.TryCreate(page, p.Metrics.CanonicalUrl, out var canonical)
               && UrlKey(canonical) != UrlKey(page);
    }

    private static int Depth(string url) => new Uri(url).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;

    private static bool UntidyUrl(Uri url) =>
        url.AbsolutePath.Any(char.IsUpper) || url.AbsolutePath.Contains('_', StringComparison.Ordinal)
        || url.Query.Length > 1 || url.AbsoluteUri.Length > 115;

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    // ---- Topic coverage: does any page target a customer question? ----

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        // Turkish question and filler words
        "hangi", "hangisi", "nasıl", "neden", "nedir", "nelerdir", "nerede", "nereden", "için", "olan", "olarak", "daha", "veya",
        "gibi", "kadar", "bana", "benim", "şimdi", "misin", "musun", "midir", "mıdır", "önerir", "önerirsin", "öner", "söyle",
        "acaba", "yapabilirim", "yapılır", "alabilirim", "istiyorum", "lazım", "gerekir", "gerekiyor", "hakkında", "arasında",
        "tavsiye", "eder", "edersin", "mevcut", "şunlar", "bunlar", "olur", "oluyor", "veriyor", "veren", "sunan", "sunuyor",
        // English
        "what", "which", "best", "with", "does", "have", "from", "that", "this", "about", "should", "would", "could", "there",
        "their", "your", "into", "when", "where", "recommend", "good", "need", "want",
    };

    /// <summary>The question's content words (4+ letters, no question or filler words), lower-cased in its language.</summary>
    public static IReadOnlyList<string> Keywords(string text, string languageCode)
    {
        ArgumentNullException.ThrowIfNull(text);
        var culture = CultureFor(languageCode);
        return WordRegex().Matches(text)
            .Select(m => m.Value.ToLower(culture))
            .Where(w => w.Length >= 4 && !StopWords.Contains(w))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The page whose title or headings contain most (60%+) of the question's keywords. Two words match when they share
    /// a prefix of at least 4 letters that leaves at most 2 letters of the shorter one, a rough stem that copes with
    /// Turkish suffixes (kartı ~ kartları, kredi ~ kredisi).
    /// </summary>
    public static CrawledPage? BestPageFor(string question, string languageCode, IReadOnlyList<CrawledPage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var keywords = Keywords(question, languageCode);
        if (keywords.Count == 0)
        {
            return null;
        }

        var culture = CultureFor(languageCode);
        return pages
            .Where(p => !string.IsNullOrWhiteSpace(p.Topics))
            .Select(p => (Page: p, Words: WordRegex().Matches(p.Topics!).Select(m => m.Value.ToLower(culture)).Distinct(StringComparer.Ordinal).ToList()))
            .Select(x => (x.Page, Hits: keywords.Count(k => x.Words.Any(w => SameStem(k, w)))))
            .Where(x => x.Hits * 10 >= keywords.Count * 6)
            .OrderByDescending(x => x.Hits)
            .Select(x => x.Page)
            .FirstOrDefault();
    }

    private static bool SameStem(string a, string b)
    {
        var common = 0;
        while (common < a.Length && common < b.Length && a[common] == b[common])
        {
            common++;
        }

        return common >= Math.Max(4, Math.Min(a.Length, b.Length) - 2);
    }

    private static CultureInfo CultureFor(string languageCode) =>
        languageCode.StartsWith("tr", StringComparison.OrdinalIgnoreCase) ? CultureInfo.GetCultureInfo("tr-TR") : CultureInfo.InvariantCulture;

    [GeneratedRegex(@"\p{L}[\p{L}\p{Mn}]*")]
    private static partial Regex WordRegex();
}
