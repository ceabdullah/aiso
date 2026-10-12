using System.Text.Json.Serialization;

namespace Aiso.Infrastructure.Reporting;

public enum RemediationPriority
{
    Critical = 0,
    High = 1,
    Medium = 2,
    Low = 3,
}

/// <summary>One actionable recommendation produced by the runtime agent.</summary>
public sealed record RemediationItem(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("priority")] string? PriorityRaw,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("owner")] string? Owner)
{
    [JsonIgnore]
    public RemediationPriority Priority => AisoMarkdownExporter.ParsePriority(PriorityRaw);
}

/// <summary>The structured payload the agent is instructed to return (see CLAUDE.md).</summary>
public sealed record RemediationPayload(
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("items")] IReadOnlyList<RemediationItem> Items);

public sealed record ReportContext(
    string TargetCode,
    string TargetName,
    string TargetUrl,
    string LanguageCode,
    decimal? OverallScore,
    decimal? OnPageScore,
    decimal? PromptVisibilityScore,
    DateTime GeneratedAtUtc,
    string? AgentSessionId,
    IReadOnlyList<EngineScore>? EngineScores = null);

/// <summary>Per-engine visibility for one target: share of answers naming the brand or citing it, and citing it.</summary>
public sealed record EngineScore(string Engine, decimal VisibilityRate, decimal CitationRate);

public sealed record ExportedReport(string AbsolutePath, string RelativePath, string Markdown, int ItemCount, string? Summary);
