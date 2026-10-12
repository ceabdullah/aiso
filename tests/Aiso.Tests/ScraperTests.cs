using Aiso.Domain.Enums;
using Aiso.Infrastructure.Scraping;

namespace Aiso.Tests;

public class ScraperTests
{
    private const string PortalHtml = """
        <!DOCTYPE html>
        <html lang="tr">
        <head>
          <title>TEB | Bireysel ve Ticari Bankacılık</title>
          <meta name="description" content="Türk Ekonomi Bankası bireysel, KOBİ ve ticari müşterileri için kredi, mevduat ve yatırım çözümleri sunar.">
          <link rel="canonical" href="https://www.teb.com.tr/">
          <link rel="alternate" hreflang="tr" href="https://www.teb.com.tr/">
          <link rel="alternate" hreflang="en" href="https://www.teb.com.tr/en/">
          <meta property="og:title" content="TEB"><meta property="og:type" content="website"><meta property="og:url" content="https://www.teb.com.tr/">
          <script type="application/ld+json">{"@context":"https://schema.org","@graph":[{"@type":"BankOrCreditUnion","name":"TEB"},{"@type":"FAQPage","mainEntity":[]}]}</script>
          <script>var tracking = "these words must not be counted";</script>
        </head>
        <body>
          <h1>Bankacılık &amp; Ötesi</h1>
          <h2>Krediler</h2><h2>Mevduat</h2><h3>KOBİ</h3>
          <img src="a.png" alt="logo"><img src="b.png">
          <p>Kısa içerik.</p>
        </body></html>
        """;

    [Fact]
    public void Parse_extracts_heading_meta_and_schema_signals()
    {
        var m = NativeHtmlScraperService.Parse(PortalHtml, TargetChannelKind.CoreWebPortal);

        Assert.Equal("tr", m.HtmlLang);
        Assert.Equal(1, m.Headings.H1Count);
        Assert.Equal("Bankacılık & Ötesi", m.Headings.H1Texts[0]);
        Assert.Equal(2, m.Headings.H2Count);
        Assert.Equal(1, m.Headings.H3Count);
        Assert.Contains("BankOrCreditUnion", m.JsonLdTypes);
        Assert.Contains("FAQPage", m.JsonLdTypes);
        Assert.Equal(["tr", "en"], m.HreflangAlternates);
        Assert.Equal(3, m.OpenGraphTagCount);
        Assert.Equal(2, m.ImageCount);
        Assert.Equal(1, m.ImagesWithAlt);
        Assert.False(m.IsNoIndex);
        Assert.DoesNotContain("tracking", m.Title, StringComparison.Ordinal);
        Assert.True(m.WordCount < 20, $"script text must be excluded, got {m.WordCount}");
    }

    [Fact]
    public void Score_is_weighted_explainable_and_bounded()
    {
        var m = NativeHtmlScraperService.Parse(PortalHtml, TargetChannelKind.CoreWebPortal);
        var signals = NativeHtmlScraperService.Score(m, TargetChannelKind.CoreWebPortal);
        var score = NativeHtmlScraperService.ToScore(signals);

        Assert.Equal(100m, signals.Sum(s => s.Weight));
        Assert.InRange(score, 0m, 100m);
        Assert.False(signals.Single(s => s.Name == "ContentDepth").Passed);
        Assert.False(signals.Single(s => s.Name == "ImageAltText").Passed);
        Assert.True(signals.Single(s => s.Name == "FaqSchema").Passed);
        Assert.Equal(100m - 8 - 4, score); // everything but ContentDepth (8) and ImageAltText (4)
    }

    [Fact]
    public void Parse_reads_app_store_storefront_values()
    {
        const string html = """
            <html lang="tr"><head><title>TEB Mobil - App Store</title>
            <script type="application/ld+json">
            {"@context":"http://schema.org","@type":"SoftwareApplication","name":"CEPTETEB","applicationCategory":"Finance",
             "author":{"@type":"Person","name":"TEB A.S."},
             "aggregateRating":{"@type":"AggregateRating","ratingValue":4.7,"reviewCount":182345},
             "offers":{"@type":"Offer","price":0,"priceCurrency":"TRY","category":"free"}}
            </script></head><body><h1>CEPTETEB</h1></body></html>
            """;

        var m = NativeHtmlScraperService.Parse(html, TargetChannelKind.MobileAppStore);
        var signals = NativeHtmlScraperService.Score(m, TargetChannelKind.MobileAppStore);

        Assert.NotNull(m.Storefront);
        Assert.Equal("CEPTETEB", m.Storefront.AppName);
        Assert.Equal(4.7m, m.Storefront.RatingValue);
        Assert.Equal(182345, m.Storefront.RatingCount);
        Assert.Equal("0 TRY", m.Storefront.Price);
        Assert.Equal("Finance", m.Storefront.Category);
        Assert.True(signals.Single(s => s.Name == "StorefrontRating").Passed);
        Assert.Equal(100m, signals.Sum(s => s.Weight));
    }

    [Fact]
    public void Parse_tolerates_malformed_json_ld_and_noindex()
    {
        const string html = """<html><head><meta name="ROBOTS" content="NOINDEX, follow"><script type="application/ld+json">{ broken</script></head><body></body></html>""";
        var m = NativeHtmlScraperService.Parse(html, TargetChannelKind.DigitalChannel);

        Assert.True(m.IsNoIndex);
        Assert.Empty(m.JsonLdTypes);
        Assert.Null(m.Title);
    }
}
