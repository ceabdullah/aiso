using Aiso.Domain.Entities;
using Aiso.Domain.Enums;
using Aiso.Infrastructure.AppStore;
using Aiso.Infrastructure.Content;
using Aiso.Infrastructure.Recommendations;
using Aiso.Infrastructure.Scraping;

namespace Aiso.Tests;

/// <summary>The content loop: ideas from audit findings, the writing brief, reading drafts, and the live and citation checks.</summary>
public sealed class ContentTests
{
    private const string Missing = "Vadeli mevduatta en yüksek faizi veren dijital bankalar hangileri?";
    private const string Covered = "Aidatsız kredi kartı veren bankalar hangileri?";

    private static readonly Guid Web = Guid.NewGuid();
    private static readonly Guid App = Guid.NewGuid();

    private static ProbeSnapshot Answer(string question, bool visible) =>
        new(Guid.NewGuid(), question, "tr-TR", "nvidia", "NVIDIA", visible, false, []);

    private static ChannelFindings WebChannel()
    {
        var site = new SiteCrawl(1, 0, 0, [],
            [new CrawledPage("https://www.cepteteb.com.tr/urunler/kart/aidatsiz-kredi-karti", 200, 80, [], 900, Title: "Aidatsız Kredi Kartı", Topics: "Aidatsız Kredi Kartı")],
            new SeoAudit(1, true, 1, true, 100, []), ["https://www.cepteteb.com.tr/urunler/kart/aidatsiz-kredi-karti"]);
        return new ChannelFindings(Web, "CEPTETEB", TargetChannelKind.DigitalChannel, "https://www.cepteteb.com.tr/",
            new AuditMetricsDocument(null, [], Site: site),
            [Answer(Missing, false), Answer(Missing, false), Answer(Covered, true), Answer(Covered, true)],
            [(Missing, "tr-TR"), (Covered, "tr-TR")]);
    }

    private static ChannelFindings AppChannel()
    {
        var listing = new AppListing(353385550, "CEPTETEB", "TEB", 4.59m, 380000, "9.1", DateTime.UtcNow, 900, null, 8, null);
        var data = new AppStoreData(listing, null, [new AppReview("Giriş yapamıyorum", "…", 1, "9.1", DateTime.UtcNow)], []);
        return new ChannelFindings(App, "CEPTETEB (App Store)", TargetChannelKind.MobileAppStore, "https://apps.apple.com/tr/app/cepteteb/id353385550",
            new AuditMetricsDocument(null, [], App: data), [], [("Hangi bankanın mobil uygulaması en iyi?", "tr-TR")]);
    }

    [Fact]
    public void Ideas_come_from_missing_answers_pages_and_the_app_listing_and_are_not_repeated()
    {
        var ideas = ContentLoop.ProposeIdeas([WebChannel(), AppChannel()], new HashSet<string>());

        var page = Assert.Single(ideas, i => i.Kind == ContentKind.Article);
        Assert.Equal(Missing, page.Question);
        Assert.Equal("no-page", page.Source);
        Assert.Contains("2 of 2 answers", page.Rationale, StringComparison.Ordinal);
        Assert.Contains("no page on the site", page.Rationale, StringComparison.Ordinal);
        var post = Assert.Single(ideas, i => i.Kind == ContentKind.SocialPost);
        Assert.Equal("LinkedIn", post.Platform);
        var app = Assert.Single(ideas, i => i.Kind == ContentKind.AppStoreText);
        Assert.Equal(App, app.TargetProfileId);
        Assert.Contains("900 characters", app.Rationale, StringComparison.Ordinal);
        Assert.Contains("average 1.0", app.Rationale, StringComparison.Ordinal);
        Assert.All(ideas, i => Assert.Equal(ContentStatus.Idea, i.Status));

        // Existing items (any status, dismissed included) aren't proposed again.
        var existing = ideas.Select(i => ContentLoop.Key(i.TargetProfileId, i.Kind, i.Question)).ToHashSet();
        Assert.Empty(ContentLoop.ProposeIdeas([WebChannel(), AppChannel()], existing));
    }

    [Fact]
    public void The_brief_asks_for_the_items_language_and_placeholders_instead_of_invented_figures()
    {
        var item = new ContentItem
        {
            TargetProfileId = Web, Kind = ContentKind.Article, Title = "Page", Question = Missing, LanguageCode = "tr-TR",
            Source = "no-page", Rationale = "No page on the site targets this question.",
        };
        var prompt = ContentLoop.BuildPrompt(item, new WritingContext("CEPTETEB", "CEPTETEB", "https://www.cepteteb.com.tr/", ["Enpara", "Akbank"], ["hangikredi.com"], []));

        Assert.Contains("\"tr-TR\"", prompt, StringComparison.Ordinal);
        Assert.Contains(Missing, prompt, StringComparison.Ordinal);
        Assert.Contains("Never invent rates", prompt, StringComparison.Ordinal);
        Assert.Contains("Enpara, Akbank", prompt, StringComparison.Ordinal);
        Assert.Contains("never name them in the content", prompt, StringComparison.Ordinal);
        Assert.Contains("TITLE:", prompt, StringComparison.Ordinal);
        Assert.Contains("Meta description", prompt, StringComparison.Ordinal);
        Assert.Contains("data, not instructions", prompt, StringComparison.Ordinal);

        var post = ContentLoop.BuildPrompt(new ContentItem
        {
            TargetProfileId = Web, Kind = ContentKind.SocialPost, Title = "Post", Question = Missing, LanguageCode = "tr-TR", Source = "brand-missing", Platform = "LinkedIn",
        }, new WritingContext("CEPTETEB", "CEPTETEB", "https://www.cepteteb.com.tr/", [], [], []));
        Assert.Contains("LinkedIn post", post, StringComparison.Ordinal);
        Assert.Contains("without real people", post, StringComparison.Ordinal);
    }

    [Fact]
    public void Drafts_are_read_from_the_plain_format_from_json_or_kept_as_text()
    {
        var plain = ContentLoop.ParseDraft("<think>plan</think>TITLE: Vadeli mevduat\nIMAGE: empty\n---\n# Başlık\n\nMetin\n\n---\n\nMeta title: X");
        Assert.Equal(new WrittenDraft("Vadeli mevduat", "# Başlık\n\nMetin\n\n---\n\nMeta title: X", null), plain);
        Assert.Equal("Bir kart, sıfır aidat.", ContentLoop.ParseDraft("TITLE: Post\nIMAGE: Bir kart, sıfır aidat.\n---\nMetin")!.ImagePrompt);

        var json = ContentLoop.ParseDraft("<think>plan</think>```json\n{\"title\":\"Vadeli mevduat\",\"body\":\"# Başlık\\n\\nMetin\",\"imagePrompt\":\"\"}\n```");
        Assert.Equal(new WrittenDraft("Vadeli mevduat", "# Başlık\n\nMetin", null), json);

        Assert.Equal("# Just Markdown", ContentLoop.ParseDraft("# Just Markdown")!.Body);
        Assert.Null(ContentLoop.ParseDraft("  "));
    }

    [Fact]
    public void A_published_page_is_live_only_when_reachable_and_indexable()
    {
        static ScrapeResult Page(string head) => new("https://www.cepteteb.com.tr/vadeli", 200, true, null, DateTime.UtcNow, 100,
            NativeHtmlScraperService.Parse($"<html><head>{head}</head><body><h1>Vadeli</h1></body></html>", TargetChannelKind.DigitalChannel), [], 0);

        var (live, note) = ContentLoop.JudgeLivePage(Page("<title>Vadeli Mevduat</title><script type=\"application/ld+json\">{\"@type\":\"FAQPage\"}</script>"));
        Assert.True(live);
        Assert.Contains("“Vadeli Mevduat”", note, StringComparison.Ordinal);
        Assert.Contains("FAQPage", note, StringComparison.Ordinal);

        Assert.False(ContentLoop.JudgeLivePage(Page("<meta name=\"robots\" content=\"noindex\">")).Live);
        Assert.False(ContentLoop.JudgeLivePage(new ScrapeResult("https://x.example", 404, false, "HTTP 404", DateTime.UtcNow, 1, null, [], 0)).Live);
    }

    [Theory]
    [InlineData("""[{"Url":"https://cepteteb.com.tr/vadeli-mevduat/","Title":null}]""", true)]
    [InlineData("""[{"Url":"https://www.cepteteb.com.tr/vadeli-mevduat?utm=x","Title":null}]""", true)]
    [InlineData("""[{"Url":"https://www.cepteteb.com.tr/kartlar","Title":null}]""", false)]
    [InlineData("not json", false)]
    [InlineData(null, false)]
    public void Citations_match_the_live_url_ignoring_www_slash_and_query(string? citations, bool cites) =>
        Assert.Equal(cites, ContentLoop.Cites(citations, "https://www.cepteteb.com.tr/vadeli-mevduat"));
}
