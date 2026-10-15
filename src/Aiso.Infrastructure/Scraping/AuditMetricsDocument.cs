using System.Text.Json;

namespace Aiso.Infrastructure.Scraping;

/// <summary>Shape of <c>LiveSearchAuditLog.MetricsJson</c>; shared by the worker (writer) and dashboard (reader).</summary>
/// <param name="Site">Crawl of the channel's own site (web channels).</param>
/// <param name="Competitors">Pages the AI engines cited instead of the brand.</param>
/// <param name="App">Public App Store data (app channels).</param>
/// <param name="Brand">Which banks the engines named and which kinds of sources they cited.</param>
public sealed record AuditMetricsDocument(
    PageMetrics? Metrics,
    IReadOnlyList<ScoreSignal> Signals,
    SiteCrawl? Site = null,
    IReadOnlyList<CompetitorPage>? Competitors = null,
    Aiso.Infrastructure.AppStore.AppStoreData? App = null,
    Aiso.Infrastructure.Recommendations.BrandAnalysis? Brand = null)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static AuditMetricsDocument? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AuditMetricsDocument>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
