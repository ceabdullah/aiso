using System.Text.Json;

namespace Aiso.Infrastructure.Scraping;

/// <summary>Per-prompt outcome persisted with an audit (the full answer text lives in the data/ snapshot).</summary>
public sealed record PromptOutcome(Guid PromptId, string LanguageCode, bool MentionsBrand, IReadOnlyList<string> MatchedKeywords);

/// <summary>Shape of <c>LiveSearchAuditLog.MetricsJson</c>; shared by the worker (writer) and dashboard (reader).</summary>
public sealed record AuditMetricsDocument(PageMetrics? Metrics, IReadOnlyList<ScoreSignal> Signals, IReadOnlyList<PromptOutcome> Prompts)
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
