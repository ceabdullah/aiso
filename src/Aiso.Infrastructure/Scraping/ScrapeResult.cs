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
    StorefrontMetrics? Storefront);

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
    decimal OnPageScore);
