using System.Text.Json.Nodes;
using System.Xml.Linq;
using Aiso.Domain.Entities;
using Aiso.Domain.Enums;
using Aiso.Infrastructure.Content;
using Aiso.Infrastructure.Engines;
using Aiso.Infrastructure.Scraping;

namespace Aiso.Tests;

/// <summary>Alt text for the site's images (found by the crawl, described by a vision model) and template graphics.</summary>
public sealed class ContentMediaTests
{
    private const string Html = """
        <html><body>
          <img src="/img/kampanya.jpg">
          <img data-src="/img/kart.webp" src="data:image/gif;base64,R0lGOD">
          <img src="/img/logo.svg">
          <img src="/px.gif" width="1" height="1">
          <img src="https://countly.cepteteb.com.tr/pixel.png?app_key=1">
          <img src="/img/ekip.png" alt="CEPTETEB ekibi">
          <img src="/img/arka.jpg" alt="">
        </body></html>
        """;

    private static ScrapeResult Page(string url) =>
        new(url, 200, true, null, DateTime.UtcNow, 100, NativeHtmlScraperService.Parse(Html, TargetChannelKind.DigitalChannel), [], 0);

    [Fact]
    public void The_crawl_lists_content_images_without_alt_text_as_absolute_urls()
    {
        Assert.Equal(new[] { "/img/kampanya.jpg", "/img/kart.webp", "/img/arka.jpg" }, Page("https://www.cepteteb.com.tr/a").Metrics!.ImagesMissingAlt);

        var crawl = SiteCrawler.Summarize([Page("https://www.cepteteb.com.tr/a")], 0);
        Assert.Equal("https://www.cepteteb.com.tr/img/kampanya.jpg", crawl.Pages[0].ImagesMissingAlt![0]);
    }

    [Fact]
    public void Each_image_becomes_one_alt_text_idea_pointing_at_a_page_it_is_on()
    {
        var site = SiteCrawler.Summarize([Page("https://www.cepteteb.com.tr/a"), Page("https://www.cepteteb.com.tr/b")], 0);
        var channel = new ChannelFindings(Guid.NewGuid(), "CEPTETEB", TargetChannelKind.DigitalChannel, "https://www.cepteteb.com.tr/",
            new AuditMetricsDocument(null, [], Site: site), [], [("Soru?", "tr-TR")]);

        var ideas = ContentLoop.ProposeIdeas([channel], new HashSet<string>()).Where(i => i.Kind == ContentKind.ImageAltText).ToList();

        Assert.Equal(3, ideas.Count);
        var first = ideas[0];
        Assert.Equal("https://www.cepteteb.com.tr/img/kampanya.jpg", first.Question);
        Assert.Equal("https://www.cepteteb.com.tr/a", first.LiveUrl);
        Assert.Equal("Alt text: kampanya.jpg", first.Title);
        Assert.Contains("2 crawled page(s)", first.Rationale, StringComparison.Ordinal);
        Assert.Equal("tr-TR", first.LanguageCode);
    }

    [Theory]
    [InlineData("\"Mobil uygulamada para transferi ekranı\"", "Mobil uygulamada para transferi ekranı")]
    [InlineData("alt=\"Kredi kartı kampanyası\"\nExtra line", "Kredi kartı kampanyası")]
    [InlineData("<think>looking</think>DECORATIVE.", "DECORATIVE")]
    [InlineData("The image shows a green mailbox with letters.", "A green mailbox with letters.")]
    [InlineData("Görselde kredi kartı kampanyası", "Kredi kartı kampanyası")]
    [InlineData("Bu görsel CEPTETEB web sitesinde yer alıyor. Görsel için alt metin yaz. Yalnızca Türkçe yaz.", "")]
    [InlineData("Bir posta kutusunun kapısı açık ve içinde çeşitli kartlar görülmektedir. Görseldeki yazılar, TEB, TEB Kart ve daha fazlası okunuyor burada.",
        "Bir posta kutusunun kapısı açık ve içinde çeşitli kartlar görülmektedir.")]
    public void Alt_text_answers_are_cleaned(string answer, string expected) =>
        Assert.Equal(expected, ContentLoop.CleanAltText(answer));

    [Theory]
    [InlineData("A man and a woman are shaking hands in an office.", "tr-TR", true)]
    [InlineData("Ofiste el sıkışan bir kadın ve bir erkek.", "tr-TR", false)]
    [InlineData("Top Employer Europe 2024, Certified Excellence in Employee Conditions.", "tr-TR", false)]
    [InlineData("A man and a woman are shaking hands in an office.", "en-US", false)]
    public void English_answers_for_turkish_pages_are_spotted(string alt, string language, bool english) =>
        Assert.Equal(english, ContentLoop.LooksEnglish(alt, language));

    [Fact]
    public void Long_alt_text_is_cut_at_a_word()
    {
        var alt = ContentLoop.CleanAltText(string.Join(' ', Enumerable.Repeat("kelime", 40)));
        Assert.True(alt.Length <= ContentLoop.AltMaxLength + 1);
        Assert.EndsWith("kelime…", alt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_live_check_reads_the_images_alt_on_the_page()
    {
        var page = new Uri("https://www.cepteteb.com.tr/a");
        Assert.Equal("CEPTETEB ekibi", NativeHtmlScraperService.AltTextOnPage(Html, page, "https://www.cepteteb.com.tr/img/ekip.png"));
        Assert.Null(NativeHtmlScraperService.AltTextOnPage(Html, page, "https://www.cepteteb.com.tr/img/kampanya.jpg"));

        var item = new ContentItem { Title = "t", Question = "q", LanguageCode = "tr-TR", Source = "alt-missing", Kind = ContentKind.ImageAltText, Draft = "Kart" };
        Assert.True(ContentLoop.JudgeAltText("Kart görseli", item).Live);
        Assert.False(ContentLoop.JudgeAltText(null, item).Live);
        Assert.False(ContentLoop.JudgeAltText("", item).Live);
        Assert.True(ContentLoop.JudgeAltText("", new ContentItem { Title = "t", Question = "q", LanguageCode = "tr-TR", Source = "s", Draft = ContentLoop.Decorative }).Live);
    }

    [Fact]
    public async Task The_vision_request_sends_the_image_and_the_brief()
    {
        var engine = new AnswerEngineDefinition { Kind = AnswerEngineKind.OpenAiCompatibleChat, ApiKey = "nvapi-x", Model = "m" };
        using var request = AltTextWriter.BuildRequest(engine, "meta/llama-3.2-11b-vision-instruct", 200, "data:image/png;base64,AAA", "Describe.");

        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.EndsWith("/chat/completions", request.RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.Equal("meta/llama-3.2-11b-vision-instruct", body["model"]!.GetValue<string>());
        var content = body["messages"]![0]!["content"]!.AsArray();
        Assert.Equal("data:image/png;base64,AAA", content[0]!["image_url"]!["url"]!.GetValue<string>());
        Assert.Equal("Describe.", content[1]!["text"]!.GetValue<string>());

        var prompt = ContentLoop.BuildAltPrompt(new ContentItem { Title = "t", Question = "q", LanguageCode = "tr-TR", Source = "s" }, "CEPTETEB", null);
        Assert.Contains("Yalnızca Türkçe yaz", prompt, StringComparison.Ordinal);
        Assert.Contains("English", ContentLoop.BuildAltPrompt(new ContentItem { Title = "t", Question = "q", LanguageCode = "en-US", Source = "s" }, "TEB", null), StringComparison.Ordinal);
        Assert.Contains(ContentLoop.Decorative, prompt, StringComparison.Ordinal);
    }

    private const string ArticleDraft = """
        # Vadeli mevduatta en yüksek faiz?

        CEPTETEB Marifetli Hesap ile birikimleriniz [güncel faiz oranı] ile büyür & kazanır.

        ## Vadeli mevduat nedir?

        Belirli bir süre için yatırılan para.

        ## Sıkça Sorulan Sorular

        ### Faiz oranı sabit mi?
        Vade boyunca sabittir.

        **Erken çekersem ne olur?**
        Faiz kaybı olabilir.

        ---

        Meta title: Vadeli Mevduat | CEPTETEB
        Meta description: CEPTETEB ile vadeli mevduatı keşfedin.
        """;

    [Fact]
    public void A_page_draft_gives_a_share_image_and_a_faq_carousel()
    {
        var graphics = ContentGraphics.For(new GraphicSource(ContentKind.Article, "Page", "Soru?", ArticleDraft, "CEPTETEB", "cepteteb.com.tr"), GraphicStyle.Default);

        var share = graphics[0];
        Assert.Equal((1200, 630), (share.Width, share.Height));
        Assert.Contains(">Vadeli mevduatta en yüksek<", share.Svg, StringComparison.Ordinal); // the H1, wrapped
        Assert.Contains(">faiz?<", share.Svg, StringComparison.Ordinal);
        Assert.Contains("CEPTETEB ile vadeli mevduatı keşfedin.", share.Svg, StringComparison.Ordinal); // the meta description
        Assert.Contains("cepteteb.com.tr", share.Svg, StringComparison.Ordinal);

        var slides = graphics.Skip(1).ToList();
        Assert.Equal(4, slides.Count); // cover, two FAQ answers, closing
        Assert.Contains("Faiz oranı sabit mi?", slides[1].Svg, StringComparison.Ordinal);
        Assert.Contains("Faiz kaybı olabilir.", slides[2].Svg, StringComparison.Ordinal);
        Assert.All(graphics, g => XDocument.Parse(g.Svg)); // well-formed, text escaped
    }

    [Fact]
    public void Post_and_app_drafts_give_their_own_formats_and_bad_colours_fall_back()
    {
        var post = ContentGraphics.For(new GraphicSource(ContentKind.SocialPost, "Post", "Soru?", "Birikiminiz <hızla> büyüsün.\nDetaylar: [link]\n#CEPTETEB", "CEPTETEB", "cepteteb.com.tr"),
            GraphicStyle.From("red", "#00FF00"));
        Assert.Equal((1200, 627), (post[0].Width, post[0].Height));
        Assert.Contains("Birikiminiz &lt;hızla&gt; büyüsün.", post[0].Svg, StringComparison.Ordinal);
        Assert.Contains(GraphicStyle.Default.Background, post[0].Svg, StringComparison.Ordinal);
        Assert.Contains("#00FF00", post[0].Svg, StringComparison.Ordinal);

        var app = ContentGraphics.For(new GraphicSource(ContentKind.AppStoreText, "App", "Soru?",
            "## Description\n- Saniyeler içinde para gönderin\n- Kartınızı uygulamadan yönetin\n- 7/24 destek alın", "CEPTETEB", "apps.apple.com"), GraphicStyle.Default);
        Assert.Equal(3, app.Count);
        Assert.Equal((1290, 2796), (app[0].Width, app[0].Height));
        Assert.Contains(">Saniyeler içinde<", app[0].Svg, StringComparison.Ordinal);

        Assert.Empty(ContentGraphics.For(new GraphicSource(ContentKind.ImageAltText, "a", "q", "alt", "B", "h"), GraphicStyle.Default));
    }

    [Fact]
    public void Wrapping_respects_the_line_count_and_marks_cut_text()
    {
        var lines = ContentGraphics.Wrap("bir iki üç dört beş altı yedi sekiz dokuz on", 10, 2);
        Assert.Equal(2, lines.Count);
        Assert.EndsWith("…", lines[1], StringComparison.Ordinal);
        Assert.All(lines, l => Assert.True(l.Length <= 10));
    }
}
