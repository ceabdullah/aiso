using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aiso.Domain.Enums;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aiso.Infrastructure.Scraping;

/// <summary>
/// Fetches a target page as a black box and extracts the structural signals that generative
/// search engines rely on (headings, schema.org JSON-LD, meta, language, storefront values).
/// No JavaScript is executed: what is measured is what a crawler without a browser sees.
/// </summary>
public sealed partial class NativeHtmlScraperService(
    HttpClient httpClient,
    IOptions<ScraperOptions> options,
    ILogger<NativeHtmlScraperService> logger)
{
    public const string HttpClientName = "aiso-scraper";

    public async Task<ScrapeResult> ScrapeAsync(Uri url, TargetChannelKind kind, CancellationToken cancellationToken) =>
        (await ScrapePageAsync(url, kind, cancellationToken)).Result;

    /// <summary>Scrapes a page and also returns its links, for crawling a whole site.</summary>
    public async Task<PageScrape> ScrapePageAsync(Uri url, TargetChannelKind kind, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", options.Value.UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
            request.Headers.TryAddWithoutValidation("Accept-Language", options.Value.AcceptLanguage);

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                return new PageScrape(Failed(url, status, $"HTTP {status} {response.ReasonPhrase}", stopwatch), []);
            }

            var html = await ReadBoundedAsync(response.Content, options.Value.MaxResponseBytes, cancellationToken);
            var metrics = Parse(html, kind);
            var signals = Score(metrics, kind);

            logger.LogInformation("Scraped {Url} ({Status}) in {Elapsed} ms.", url, status, stopwatch.ElapsedMilliseconds);
            return new PageScrape(
                new ScrapeResult(url.ToString(), status, true, null, DateTime.UtcNow, stopwatch.ElapsedMilliseconds, metrics, signals, ToScore(signals)),
                ExtractLinks(html, response.RequestMessage?.RequestUri ?? url));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Scrape failed for {Url}.", url);
            var status = ex is HttpRequestException { StatusCode: { } code } ? (int)code : (int?)null;
            return new PageScrape(Failed(url, status, ex.Message, stopwatch), []);
        }
    }

    /// <summary>Fetches a small text resource (robots.txt, sitemap.xml) with the crawler's identity; null when unavailable.</summary>
    public async Task<string?> FetchTextAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", options.Value.UserAgent);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.IsSuccessStatusCode ? await ReadBoundedAsync(response.Content, options.Value.MaxResponseBytes, cancellationToken) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Could not fetch {Url}: {Message}", url, ex.Message);
            return null;
        }
    }

    /// <summary>Absolute http(s) links of a page, without fragments. Static for unit testing.</summary>
    public static IReadOnlyList<Uri> ExtractLinks(string html, Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        var doc = new HtmlDocument();
        doc.LoadHtml(html ?? string.Empty);
        var links = new List<Uri>();
        foreach (var a in doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
        {
            var href = WebUtility.HtmlDecode(a.GetAttributeValue("href", string.Empty)).Trim();
            if (href.Length > 0 && Uri.TryCreate(baseUri, href, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                links.Add(new UriBuilder(uri) { Fragment = string.Empty }.Uri);
            }
        }

        return links;
    }

    private static ScrapeResult Failed(Uri url, int? status, string error, Stopwatch sw) =>
        new(url.ToString(), status, false, error, DateTime.UtcNow, sw.ElapsedMilliseconds, null, [], 0m);

    private static async Task<string> ReadBoundedAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                buffer.Write(chunk, 0, (int)(maxBytes - buffer.Length));
                break;
            }

            buffer.Write(chunk, 0, read);
        }

        var charset = content.Headers.ContentType?.CharSet;
        var encoding = System.Text.Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { encoding = System.Text.Encoding.GetEncoding(charset.Trim('"')); }
            catch (ArgumentException) { /* fall back to UTF-8 */ }
        }

        return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>Pure HTML → metrics extraction. Static and deterministic for unit testing.</summary>
    public static PageMetrics Parse(string html, TargetChannelKind kind)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var root = doc.DocumentNode;

        string? Meta(string attr, string name) =>
            Clean(root.SelectSingleNode($"//meta[translate(@{attr},'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')='{name}']")
                ?.Attributes["content"]?.Value);

        var h1 = Texts(root, "//h1");
        var h2 = Texts(root, "//h2");
        var h3Count = root.SelectNodes("//h3")?.Count ?? 0;

        var jsonLdNodes = JsonLdDocuments(root).ToList();
        var jsonLdTypes = jsonLdNodes.SelectMany(CollectTypes).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var images = root.SelectNodes("//img")?.ToList() ?? [];
        var robots = Meta("name", "robots") ?? string.Empty;

        // Visible text approximation: strip script/style/noscript before counting words.
        foreach (var node in root.SelectNodes("//script|//style|//noscript")?.ToList() ?? [])
        {
            if (node.GetAttributeValue("type", string.Empty) != "application/ld+json")
            {
                node.Remove();
            }
        }

        var bodyText = WebUtility.HtmlDecode(root.SelectSingleNode("//body")?.InnerText ?? root.InnerText);
        var wordCount = WordRegex().Count(bodyText);

        return new PageMetrics(
            Title: Clean(root.SelectSingleNode("//title")?.InnerText),
            MetaDescription: Meta("name", "description"),
            HtmlLang: Clean(root.SelectSingleNode("//html")?.Attributes["lang"]?.Value),
            CanonicalUrl: Clean(root.SelectSingleNode("//link[@rel='canonical']")?.Attributes["href"]?.Value),
            IsNoIndex: robots.Contains("noindex", StringComparison.OrdinalIgnoreCase),
            Headings: new HeadingMetrics(h1.Count, h2.Count, h3Count, h1, h2.Take(20).ToList()),
            JsonLdTypes: jsonLdTypes,
            HreflangAlternates: (root.SelectNodes("//link[@rel='alternate'][@hreflang]") ?? Enumerable.Empty<HtmlNode>())
                .Select(n => n.GetAttributeValue("hreflang", string.Empty)).Where(s => s.Length > 0).Distinct().ToList(),
            OpenGraphTagCount: root.SelectNodes("//meta[starts-with(@property,'og:')]")?.Count ?? 0,
            WordCount: wordCount,
            ImageCount: images.Count,
            ImagesWithAlt: images.Count(i => !string.IsNullOrWhiteSpace(i.Attributes["alt"]?.Value)),
            Storefront: kind == TargetChannelKind.MobileAppStore ? ParseStorefront(jsonLdNodes, Meta) : null);
    }

    private static StorefrontMetrics ParseStorefront(List<JsonElement> jsonLd, Func<string, string, string?> meta)
    {
        foreach (var app in jsonLd.SelectMany(Flatten).Where(e => HasType(e, "SoftwareApplication", "MobileApplication")))
        {
            decimal? rating = null;
            long? count = null;
            if (app.TryGetProperty("aggregateRating", out var agg))
            {
                rating = ReadDecimal(agg, "ratingValue");
                count = (long?)ReadDecimal(agg, "reviewCount") ?? (long?)ReadDecimal(agg, "ratingCount");
            }

            string? price = null;
            if (app.TryGetProperty("offers", out var offers))
            {
                var offer = offers.ValueKind == JsonValueKind.Array && offers.GetArrayLength() > 0 ? offers[0] : offers;
                if (offer.ValueKind == JsonValueKind.Object)
                {
                    var amount = ReadString(offer, "price");
                    var currency = ReadString(offer, "priceCurrency");
                    price = amount is null ? null : $"{amount} {currency}".Trim();
                }
            }

            string? seller = null;
            if (app.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object)
            {
                seller = ReadString(author, "name");
            }

            return new StorefrontMetrics(ReadString(app, "name"), rating, count, price,
                ReadString(app, "applicationCategory"), seller);
        }

        return new StorefrontMetrics(meta("property", "og:title"), null, null, null, null, null);
    }

    /// <summary>Weighted, explainable on-page signals. Weights sum to 100.</summary>
    public static IReadOnlyList<ScoreSignal> Score(PageMetrics m, TargetChannelKind kind)
    {
        ArgumentNullException.ThrowIfNull(m);
        var hasOrg = m.JsonLdTypes.Any(t => t is "Organization" or "BankOrCreditUnion" or "FinancialService" or "Corporation");
        var hasFaq = m.JsonLdTypes.Any(t => t is "FAQPage" or "QAPage" or "HowTo");
        var altCoverage = m.ImageCount == 0 ? 1m : (decimal)m.ImagesWithAlt / m.ImageCount;

        var signals = new List<ScoreSignal>
        {
            new("Indexable", 15, !m.IsNoIndex, m.IsNoIndex ? "robots meta contains noindex" : "no noindex directive"),
            new("Title", 8, m.Title is { Length: >= 10 and <= 70 }, $"length {m.Title?.Length ?? 0} (10-70 ideal)"),
            new("MetaDescription", 8, m.MetaDescription is { Length: >= 50 and <= 170 }, $"length {m.MetaDescription?.Length ?? 0} (50-170 ideal)"),
            new("HtmlLang", 6, !string.IsNullOrWhiteSpace(m.HtmlLang), $"lang='{m.HtmlLang}'"),
            new("SingleH1", 8, m.Headings.H1Count == 1, $"{m.Headings.H1Count} H1 element(s)"),
            new("HeadingHierarchy", 7, m.Headings.H2Count >= 2, $"{m.Headings.H2Count} H2 / {m.Headings.H3Count} H3"),
            new("Canonical", 5, !string.IsNullOrWhiteSpace(m.CanonicalUrl), m.CanonicalUrl ?? "missing"),
            new("OpenGraph", 4, m.OpenGraphTagCount >= 3, $"{m.OpenGraphTagCount} og:* tags"),
            new("ImageAltText", 4, altCoverage >= 0.8m, $"{altCoverage:P0} of {m.ImageCount} images have alt"),
        };

        if (kind == TargetChannelKind.MobileAppStore)
        {
            var sf = m.Storefront;
            signals.Add(new("StorefrontStructuredData", 15, sf?.RatingValue is not null, sf?.AppName ?? "no SoftwareApplication JSON-LD"));
            signals.Add(new("StorefrontRating", 20, sf?.RatingValue >= 4.0m,
                sf?.RatingValue is { } r ? string.Create(CultureInfo.InvariantCulture, $"{r:0.0} from {sf.RatingCount} ratings") : "no rating"));
        }
        else
        {
            signals.Add(new("OrganizationSchema", 12, hasOrg, hasOrg ? "Organization-type JSON-LD present" : "no Organization/BankOrCreditUnion JSON-LD"));
            signals.Add(new("FaqSchema", 10, hasFaq, hasFaq ? "FAQ/QA/HowTo JSON-LD present" : "no FAQPage JSON-LD — answer engines favour Q&A markup"));
            signals.Add(new("ContentDepth", 8, m.WordCount >= 300, $"{m.WordCount} words of server-rendered text"));
            signals.Add(new("Hreflang", 5, m.HreflangAlternates.Count >= 2, $"{m.HreflangAlternates.Count} hreflang alternates"));
        }

        return signals;
    }

    public static decimal ToScore(IReadOnlyList<ScoreSignal> signals)
    {
        ArgumentNullException.ThrowIfNull(signals);
        var total = signals.Sum(s => s.Weight);
        return total == 0 ? 0 : Math.Round(signals.Where(s => s.Passed).Sum(s => s.Weight) / total * 100m, 2);
    }

    private static IEnumerable<JsonElement> JsonLdDocuments(HtmlNode root)
    {
        foreach (var node in root.SelectNodes("//script[@type='application/ld+json']") ?? Enumerable.Empty<HtmlNode>())
        {
            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(node.InnerText, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            }
            catch (JsonException)
            {
                // Malformed JSON-LD is itself a finding, but it must not break the scrape.
            }

            if (doc is not null)
            {
                yield return doc.RootElement.Clone();
                doc.Dispose();
            }
        }
    }

    private static IEnumerable<JsonElement> Flatten(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray().SelectMany(Flatten))
                {
                    yield return item;
                }

                break;
            case JsonValueKind.Object:
                yield return element;
                if (element.TryGetProperty("@graph", out var graph))
                {
                    foreach (var item in Flatten(graph))
                    {
                        yield return item;
                    }
                }

                break;
        }
    }

    private static IEnumerable<string> CollectTypes(JsonElement element) =>
        Flatten(element).SelectMany(e => e.TryGetProperty("@type", out var t)
            ? t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Select(x => x.GetString() ?? string.Empty)
                : [t.GetString() ?? string.Empty]
            : []).Where(s => s.Length > 0);

    private static bool HasType(JsonElement e, params string[] types) =>
        CollectTypes(e).Any(t => types.Contains(t, StringComparer.OrdinalIgnoreCase));

    private static string? ReadString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    private static decimal? ReadDecimal(JsonElement e, string name) =>
        decimal.TryParse(ReadString(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static List<string> Texts(HtmlNode root, string xpath) =>
        (root.SelectNodes(xpath) ?? Enumerable.Empty<HtmlNode>())
            .Select(n => Clean(n.InnerText)).OfType<string>().ToList();

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return WhitespaceRegex().Replace(WebUtility.HtmlDecode(value), " ").Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\p{L}[\p{L}\p{Mn}'’-]*")]
    private static partial Regex WordRegex();
}
