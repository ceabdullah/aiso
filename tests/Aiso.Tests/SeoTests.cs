using Aiso.Domain.Enums;
using Aiso.Infrastructure.Recommendations;
using Aiso.Infrastructure.Scraping;

namespace Aiso.Tests;

/// <summary>The technical SEO audit over a crawled site, question-to-page matching, and the SEO suggestions and kits.</summary>
public sealed class SeoTests
{
    private const string Viewport = """<meta name="viewport" content="width=device-width, initial-scale=1">""";

    private static ScrapeResult Page(string url, string head, string body = "<h1>Sayfa</h1>", long ms = 300, string? finalUrl = null)
    {
        var metrics = NativeHtmlScraperService.Parse($"<html lang=\"tr\"><head>{head}</head><body>{body}</body></html>", TargetChannelKind.DigitalChannel);
        var signals = NativeHtmlScraperService.Score(metrics, TargetChannelKind.DigitalChannel);
        return new ScrapeResult(url, 200, true, null, DateTime.UtcNow, ms, metrics, signals, NativeHtmlScraperService.ToScore(signals), finalUrl);
    }

    private static ScrapeResult Broken(string url) => new(url, 404, false, "HTTP 404", DateTime.UtcNow, 50, null, [], 0);

    private static readonly IReadOnlyList<ScrapeResult> Site =
    [
        Page("https://www.teb.com.tr/", $"<title>TEB</title><meta name=\"description\" content=\"Bankacılık\">{Viewport}"),
        Page("https://www.teb.com.tr/kartlar/kredi-kartlari", "<title>TEB</title><meta name=\"description\" content=\"Bankacılık\">",
            "<h1>Aidatsız Kredi Kartları</h1><h2>Kart avantajları</h2>"),
        Page("https://www.teb.com.tr/Kampanya_Detay?id=3", $"<title>Kampanya detayı sayfası</title><meta name=\"robots\" content=\"noindex\">{Viewport}", ms: 3200),
        Page("https://www.teb.com.tr/eski", $"<title>Yeni sayfa başlığı</title>{Viewport}", finalUrl: "https://www.teb.com.tr/yeni"),
        Broken("https://www.teb.com.tr/kayip"),
    ];

    private static SeoSiteFacts Facts(bool sitemap = true, bool declared = false) =>
        new(true, declared, sitemap
            ? new HashSet<string>(StringComparer.Ordinal) { SeoChecks.UrlKey(new Uri("https://teb.com.tr/Kampanya_Detay")), SeoChecks.UrlKey(new Uri("https://www.teb.com.tr/")) }
            : new HashSet<string>(StringComparer.Ordinal));

    [Fact]
    public void Parse_records_the_viewport_and_html_size()
    {
        var with = NativeHtmlScraperService.Parse($"<html><head>{Viewport}</head><body></body></html>", TargetChannelKind.DigitalChannel);
        var without = NativeHtmlScraperService.Parse("<html><head></head><body></body></html>", TargetChannelKind.DigitalChannel);

        Assert.True(with.HasViewport);
        Assert.False(without.HasViewport);
        Assert.True(with.HtmlLength > 40);
    }

    [Fact]
    public void Audit_finds_site_wide_and_page_issues_and_scores_health()
    {
        var (audit, pageIssues) = SeoChecks.Audit(Site, Facts());

        var keys = audit.Issues.Select(i => i.Key).ToHashSet();
        Assert.Superset(new HashSet<string> { "broken", "viewport", "duplicate-title", "duplicate-description", "noindex-in-sitemap", "slow",
            "redirected", "breadcrumbs", "url-hygiene", "sitemap-not-declared" }, keys);
        Assert.DoesNotContain("no-sitemap", keys);
        Assert.DoesNotContain("no-robots", keys);
        Assert.Equal(4, audit.PagesChecked);
        Assert.InRange(audit.Health, 1m, 99m);

        var dup = audit.Issues.Single(i => i.Key == "duplicate-title");
        Assert.Equal(2, dup.Pages);
        Assert.Equal("“TEB” is used on 2 pages", dup.Note);
        Assert.Equal(new[] { "https://www.teb.com.tr/eski → https://www.teb.com.tr/yeni" }, audit.Issues.Single(i => i.Key == "redirected").Examples);
        Assert.Equal(new[] { "viewport", "duplicate-title", "duplicate-description", "breadcrumbs" }, pageIssues["https://www.teb.com.tr/kartlar/kredi-kartlari"]);
        Assert.Equal(new[] { "noindex-in-sitemap", "slow", "url-hygiene" }, pageIssues["https://www.teb.com.tr/Kampanya_Detay?id=3"]);
        // Ordered by what they cost: no viewport (weight 10 on 1 of 4 pages) before the missing Sitemap: line (weight 2, site-wide).
        Assert.True(audit.Issues.ToList().FindIndex(i => i.Key == "viewport") < audit.Issues.ToList().FindIndex(i => i.Key == "sitemap-not-declared"));
    }

    [Fact]
    public void A_missing_sitemap_is_reported_once_and_a_clean_site_is_healthy()
    {
        var (audit, _) = SeoChecks.Audit(Site, Facts(sitemap: false));
        Assert.Contains(audit.Issues, i => i.Key == "no-sitemap" && i.SiteWide);
        Assert.DoesNotContain(audit.Issues, i => i.Key is "sitemap-not-declared" or "noindex-in-sitemap");

        var (clean, _) = SeoChecks.Audit([Site[0]], Facts(declared: true));
        Assert.Empty(clean.Issues);
        Assert.Equal(100m, clean.Health);
    }

    [Fact]
    public void Summarize_carries_the_audit_and_each_pages_topics()
    {
        var crawl = SiteCrawler.Summarize(Site, 0, Facts());

        Assert.NotNull(crawl.Seo);
        var cards = crawl.Pages.Single(p => p.Url.EndsWith("kredi-kartlari", StringComparison.Ordinal));
        Assert.Equal("TEB · Aidatsız Kredi Kartları · Kart avantajları", cards.Topics);
        Assert.Contains("breadcrumbs", cards.SeoIssues!);
        Assert.Null(SiteCrawler.Summarize(Site, 0).Seo); // no site facts, no audit
    }

    [Theory]
    [InlineData("Hangi banka aidatsız kredi kartı veriyor?", true)]
    [InlineData("Kredi kartı aidatı nasıl iade alınır?", true)]
    [InlineData("En iyi mobil bankacılık uygulaması hangisi?", false)]
    public void Questions_match_pages_by_headings_with_turkish_suffixes(string question, bool covered)
    {
        var pages = SiteCrawler.Summarize(Site, 0, Facts()).Pages;
        Assert.Equal(covered, SeoChecks.BestPageFor(question, "tr-TR", pages) is not null);
    }

    [Fact]
    public void Seo_issues_and_topic_gaps_become_suggestions_for_the_seo_team_with_kits()
    {
        var crawl = SiteCrawler.Summarize(Site, 0, Facts());
        var doc = new AuditMetricsDocument(null, [], Site: crawl);
        var probes = new[]
        {
            new ProbeSnapshot(Guid.NewGuid(), "Hangi banka aidatsız kredi kartı veriyor?", "tr-TR", "nvidia", "NVIDIA", false, false, []),
            new ProbeSnapshot(Guid.NewGuid(), "En iyi mobil bankacılık uygulaması hangisi?", "tr-TR", "nvidia", "NVIDIA", false, false, []),
        };

        var s = VisibilityAdvisor.Analyze([new AuditSnapshot(DateTime.UtcNow, doc, probes)], ["teb.com.tr"]);

        var viewport = Assert.Single(s, x => x.Key == "seo:viewport");
        Assert.Equal("SEO", viewport.Owner);
        Assert.Contains("1 of 4 crawled pages", viewport.Evidence, StringComparison.Ordinal);
        Assert.DoesNotContain(s, x => x.Key == "seo:broken"); // covered by site:broken
        var gap = Assert.Single(s, x => x.Key == "seo:topic-gap");
        Assert.Contains("“En iyi mobil bankacılık uygulaması hangisi?”", gap.Evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("aidatsız", gap.Evidence, StringComparison.Ordinal);

        var ctx = new KitContext("TEB", "https://www.teb.com.tr/", "tr-TR", []);
        Assert.Contains("width=device-width", Assert.Single(ApplyKit.For(viewport, ctx)).Content, StringComparison.Ordinal);
        Assert.Contains("### En iyi mobil bankacılık uygulaması hangisi?", Assert.Single(ApplyKit.For(gap, ctx)).Content, StringComparison.Ordinal);
        var sitemap = ApplyKit.For(Assert.Single(s, x => x.Key == "seo:sitemap-not-declared"), ctx);
        Assert.Contains("Sitemap: https://www.teb.com.tr/sitemap.xml", Assert.Single(sitemap).Content, StringComparison.Ordinal);
    }
}
