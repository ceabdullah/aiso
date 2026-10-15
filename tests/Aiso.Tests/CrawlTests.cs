using System.Net;
using System.Text;
using System.Text.Json;
using Aiso.Domain.Enums;
using Aiso.Infrastructure.AppStore;
using Aiso.Infrastructure.Engines;
using Aiso.Infrastructure.Recommendations;
using Aiso.Infrastructure.Scraping;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aiso.Tests;

/// <summary>robots.txt, the site crawl, cited competitor pages, App Store data, and the suggestions they feed.</summary>
public sealed class CrawlTests
{
    private const string Page = """
        <html lang="tr"><head><title>TEB Kredi Kartları Sayfası</title><meta name="description" content="TEB kredi kartları: aidatsız seçenekler, kampanyalar ve başvuru."></head>
        <body><h1>Kartlar</h1><a href="/a">A</a><a href="/private/x">P</a><a href="https://external.example/z">X</a><a href="/file.pdf">F</a><a href="#top">T</a></body></html>
        """;

    private const string FaqPage = """
        <html><head><title>Rakip Banka Mevduat</title><script type="application/ld+json">{"@type":"FAQPage"}</script></head><body><h1>Mevduat</h1></body></html>
        """;

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Text(string body, string type = "text/html") =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };

    private static (SiteCrawler Crawler, NativeHtmlScraperService Scraper, Handler Handler) Crawler(Func<HttpRequestMessage, HttpResponseMessage> respond, int maxPages = 10)
    {
        var handler = new Handler(respond);
        var scraper = new NativeHtmlScraperService(new HttpClient(handler), Options.Create(new ScraperOptions()), NullLogger<NativeHtmlScraperService>.Instance);
        var crawler = new SiteCrawler(scraper, Options.Create(new ScraperOptions { CrawlDelayMs = 0, CrawlMaxPages = maxPages, CompetitorPagesMax = 5 }), NullLogger<SiteCrawler>.Instance);
        return (crawler, scraper, handler);
    }

    [Fact]
    public void Robots_uses_the_most_specific_group_and_the_longest_rule()
    {
        var robots = RobotsTxt.Parse("""
            User-agent: *
            Disallow: /

            User-agent: AISO-Audit-Bot
            Disallow: /private
            Allow: /private/public
            Disallow: /*.pdf$

            Sitemap: https://www.teb.com.tr/sitemap.xml
            """, "AISO-Audit-Bot");

        Assert.True(robots.IsAllowed(new Uri("https://www.teb.com.tr/kartlar")));       // our group, not the * "Disallow: /"
        Assert.False(robots.IsAllowed(new Uri("https://www.teb.com.tr/private/a")));
        Assert.True(robots.IsAllowed(new Uri("https://www.teb.com.tr/private/public/a"))); // longer Allow wins
        Assert.False(robots.IsAllowed(new Uri("https://www.teb.com.tr/docs/rates.pdf")));
        Assert.True(robots.IsAllowed(new Uri("https://www.teb.com.tr/docs/rates.pdf?v=2")));
        Assert.Equal(["https://www.teb.com.tr/sitemap.xml"], robots.Sitemaps);
        Assert.True(RobotsTxt.Parse(null, "AISO-Audit-Bot").IsAllowed(new Uri("https://x.example/a")));
        Assert.False(RobotsTxt.Parse("User-agent: *\nDisallow: /", "AISO-Audit-Bot").IsAllowed(new Uri("https://x.example/a")));
    }

    [Fact]
    public async Task Crawl_follows_sitemap_and_links_on_the_same_site_and_obeys_robots()
    {
        var (crawler, scraper, handler) = Crawler(req => req.RequestUri!.AbsolutePath switch
        {
            "/robots.txt" => Text("User-agent: *\nDisallow: /private\nSitemap: https://www.teb.com.tr/sitemap.xml", "text/plain"),
            "/sitemap.xml" => Text("<urlset><url><loc>https://www.teb.com.tr/b</loc></url></urlset>", "application/xml"),
            _ => Text(Page),
        });
        var start = new Uri("https://www.teb.com.tr/");
        var home = await scraper.ScrapePageAsync(start, TargetChannelKind.CoreWebPortal, TestContext.Current.CancellationToken);

        var site = await crawler.CrawlAsync(start, home, TargetChannelKind.CoreWebPortal, TestContext.Current.CancellationToken);

        Assert.Equal(3, site.PagesCrawled); // home, /b from the sitemap, /a from links
        Assert.Equal(1, site.PagesSkippedByRobots);
        Assert.DoesNotContain(handler.Requested, u => u.Contains("external.example", StringComparison.Ordinal) || u.EndsWith(".pdf", StringComparison.Ordinal));
        Assert.Equal(3, Assert.Single(site.Checks, c => c.Signal == "SingleH1").Passed);
        Assert.Equal(0, Assert.Single(site.Checks, c => c.Signal == "FaqSchema").Passed);
    }

    [Fact]
    public async Task Cited_competitor_pages_are_checked_and_robots_and_owned_pages_are_respected()
    {
        var (crawler, _, _) = Crawler(req => req.RequestUri!.Host switch
        {
            "blocked.example" when req.RequestUri.AbsolutePath == "/robots.txt" => Text("User-agent: *\nDisallow: /", "text/plain"),
            _ when req.RequestUri.AbsolutePath == "/robots.txt" => new HttpResponseMessage(HttpStatusCode.NotFound),
            _ => Text(FaqPage),
        });
        EngineCitation C(string url, string? title = null) => new(url, title);
        var probe = new EngineProbeOutcome("chatgpt", "ChatGPT", Guid.NewGuid(), "tr-TR", "q", "a",
            [C("https://www.rival.com.tr/mevduat"), C("https://www.rival.com.tr/mevduat"), C("https://blocked.example/x"),
             C("https://www.teb.com.tr/kart"), C("https://vertexaisearch.cloud.google.com/grounding-api-redirect/abc", "other.com")],
            [C("https://www.teb.com.tr/kart")], [], null, 1);

        var pages = await crawler.AnalyzeCitedPagesAsync([probe, probe], ["teb.com.tr"], TestContext.Current.CancellationToken);

        Assert.Equal(["https://www.rival.com.tr/mevduat", "https://blocked.example/x"], pages.Select(p => p.Url));
        var rival = pages[0];
        Assert.True(rival.Fetched);
        Assert.Equal(4, rival.Citations); // cited twice in each of two answers
        Assert.Contains("FAQPage", rival.JsonLdTypes);
        Assert.False(pages[1].Fetched);
        Assert.Contains("robots.txt", pages[1].Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task App_store_data_comes_from_apples_public_apis_with_peers()
    {
        var http = new StubHttpFactory(req => req.RequestUri!.AbsolutePath switch
        {
            "/lookup" => Text("""{"resultCount":1,"results":[{"trackId":353385550,"trackName":"CEPTETEB","sellerName":"TEB","averageUserRating":4.59285,"userRatingCount":379873,"version":"8.22.0","currentVersionReleaseDate":"2026-09-16T16:25:09Z","description":"kısa","screenshotUrls":["a","b"],"trackViewUrl":"https://apps.apple.com/tr/app/cepteteb/id353385550?uo=4"}]}""", "application/json"),
            "/search" => Text("""{"results":[{"trackId":1,"trackName":"Garanti BBVA","sellerName":"Garanti","averageUserRating":4.7,"userRatingCount":2200000},{"trackId":2,"trackName":"CEPTETEB İŞTE","sellerName":"TEB","averageUserRating":4.5,"userRatingCount":46000},{"trackId":3,"trackName":"Akbank","sellerName":"Akbank","averageUserRating":4.8,"userRatingCount":1700000}]}""", "application/json"),
            _ => Text("""{"feed":{"entry":[{"title":{"label":"Çöküyor"},"content":{"label":"Uygulama açılmıyor"},"im:rating":{"label":"1"},"im:version":{"label":"8.22.0"},"updated":{"label":"2026-10-13T06:37:52-07:00"}},{"title":{"label":"Harika"},"content":{"label":"Süper"},"im:rating":{"label":"2"}}]}}""", "application/json"),
        });
        var client = new AppStoreClient(http, Options.Create(new AppStoreOptions()), NullLogger<AppStoreClient>.Instance);

        var data = await client.GetAsync("https://apps.apple.com/tr/app/cepteteb/id353385550", TestContext.Current.CancellationToken);

        Assert.Null(data.Error);
        Assert.Equal("CEPTETEB", data.App!.Name);
        Assert.Equal(4.59m, data.App.Rating);
        Assert.Equal("https://apps.apple.com/tr/app/cepteteb/id353385550", data.App.Url);
        Assert.Equal(["Akbank", "Garanti BBVA"], data.Peers.Select(p => p.Name).Order(StringComparer.Ordinal)); // the bank's own other app is left out
        Assert.Equal(1.5m, data.RecentAverage);

        var missing = await client.GetAsync("https://apps.apple.com/tr/app/teb-mobil/id000000000", TestContext.Current.CancellationToken);
        Assert.Contains("no App Store app id", missing.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Suggestions_use_competitor_pages_the_site_crawl_and_app_data()
    {
        var page = new AuditMetricsDocument(null,
            [new ScoreSignal("FaqSchema", 10, false, "no FAQPage JSON-LD"), new ScoreSignal("MetaDescription", 8, true, "ok"), new ScoreSignal("SingleH1", 8, true, "1")],
            new SiteCrawl(10, 0, 1,
                [new SiteCheck("MetaDescription", 2, 9, ["https://www.teb.com.tr/a"]), new SiteCheck("FaqSchema", 0, 9, [])],
                [new CrawledPage("https://www.teb.com.tr/old", 404, 0, [], 0)]),
            [new CompetitorPage("https://www.rival.com.tr/m", "rival.com.tr", 3, ["ChatGPT"], true, null, [new ScoreSignal("FaqSchema", 10, true, "ok")], 1240, ["FAQPage"])],
            new AppStoreData(new AppListing(1, "CEPTETEB", "TEB", 4.5m, 300_000, "8.22.0", DateTime.UtcNow.AddDays(-10), 900, null, 6, null), null,
                [new AppReview("Çöküyor", "x", 1, null, null)],
                [new AppListing(2, "Garanti BBVA", "Garanti", 4.7m, 2_200_000, null, null, 0, null, 0, null), new AppListing(3, "Akbank", "Akbank", 4.8m, 1_700_000, null, null, 0, null, 0, null)]));
        var probe = new ProbeSnapshot(Guid.NewGuid(), "Mevduat faizi en yüksek banka?", "tr-TR", "chatgpt", "ChatGPT", false, false, ["rival.com.tr"]);

        var s = VisibilityAdvisor.Analyze([new AuditSnapshot(DateTime.UtcNow, page, [probe])], ["teb.com.tr"]);

        var faq = Assert.Single(s, x => x.Key == "signal:FaqSchema");
        Assert.Equal(SuggestionPriority.Critical, faq.Priority); // High, raised because the cited competitor page has it
        Assert.Contains("1 of 1 pages the AI engines cited instead of yours pass this check (rival.com.tr)", faq.Detail, StringComparison.Ordinal);
        Assert.Contains("across the site: 0 of 9 pages pass", faq.Evidence, StringComparison.Ordinal);

        Assert.Contains(s, x => x.Key == "site:MetaDescription" && x.Title.StartsWith("Across the site", StringComparison.Ordinal)); // home passes, most pages don't
        Assert.DoesNotContain(s, x => x.Key == "site:FaqSchema"); // already covered by the home-page suggestion
        Assert.Contains(s, x => x.Key == "site:broken");

        Assert.Contains(s, x => x.Key == "app:rating-gap");
        Assert.Contains(s, x => x.Key == "app:rating-count");
        Assert.Contains(s, x => x.Key == "app:recent-reviews" && x.Evidence.Contains("Çöküyor", StringComparison.Ordinal));
        Assert.Contains(s, x => x.Key == "app:description");

        var gap = Assert.Single(s, x => x.Key.StartsWith("prompt:", StringComparison.Ordinal));
        Assert.Contains("rival.com.tr (FAQPage, 1,240 words)", gap.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Sitemap_locations_and_page_links_are_parsed()
    {
        Assert.Equal(["https://a.example/1", "https://a.example/2?x=1&y=2"],
            SiteCrawler.Locations("<urlset><url><loc> https://a.example/1 </loc></url><url><loc>https://a.example/2?x=1&amp;y=2</loc></url></urlset>").Select(u => u.ToString()));
        var links = NativeHtmlScraperService.ExtractLinks(Page, new Uri("https://www.teb.com.tr/kart/")).Select(u => u.ToString()).ToList();
        Assert.Contains("https://www.teb.com.tr/a", links);
        Assert.Contains("https://www.teb.com.tr/kart/", links); // "#top" without the fragment
        Assert.Equal(5, links.Count);
    }
}

public class ApplyKitKeyTests
{
    private static readonly KitContext Ctx = new("TEB", "https://www.teb.com.tr", "tr-TR", ["Aidatsız kart var mı?"]);

    private static Suggestion S(string key) => new(key, "t", "d", SuggestionPriority.High, "technical", [], "e", SuggestionStatus.ToDo, "s", []);

    [Theory]
    [InlineData("signal:MetaDescription", "head-tags.html")]
    [InlineData("site:MetaDescription", "head-tags.html")]
    [InlineData("app:rating-gap", "rating-prompt.md")]
    [InlineData("app:link", "listing-check.md")]
    public void Kits_match_page_site_and_app_suggestions(string key, string file) =>
        Assert.Contains(ApplyKit.For(S(key), Ctx), k => k.FileName == file);

    [Fact]
    public void Suggestions_without_a_kit_get_none() => Assert.Empty(ApplyKit.For(S("site:broken"), Ctx));
}
