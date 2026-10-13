using Aiso.Domain.Enums;
using Aiso.Infrastructure.Recommendations;
using Aiso.Infrastructure.Runtime;
using Aiso.Infrastructure.Scraping;

namespace Aiso.Web.Services;

public sealed record TargetSummary(
    Guid Id,
    string Code,
    string DisplayName,
    string BaseUrl,
    TargetChannelKind ChannelKind,
    decimal? OverallScore,
    decimal? OnPageScore,
    decimal? PromptVisibilityScore,
    decimal? PreviousOverallScore,
    AuditStatus? LastStatus,
    DateTime? LastAuditUtc,
    int ActivePrompts,
    IReadOnlyList<EngineRate> Engines,
    IReadOnlyList<TrendPoint> Trend);

/// <summary>Share of one engine's answers (latest run) in which the brand was visible.</summary>
public sealed record EngineRate(string Engine, int Evaluated, int Visible, int Cited)
{
    public decimal Rate => Evaluated == 0 ? 0 : Math.Round(100m * Visible / Evaluated, 1);
    public decimal CitedRate => Evaluated == 0 ? 0 : Math.Round(100m * Cited / Evaluated, 1);
}

/// <summary>One engine × target cell of the analytics matrix.</summary>
public sealed record EngineVisibility(string Target, string Engine, int Evaluated, int Mentioned, int Cited, int Visible)
{
    public decimal Rate => Evaluated == 0 ? 0 : Math.Round(100m * Visible / Evaluated, 1);
    public decimal CitedRate => Evaluated == 0 ? 0 : Math.Round(100m * Cited / Evaluated, 1);
}

/// <summary>A web domain the engines cited for the test prompts.</summary>
public sealed record CitedDomain(string Domain, int Citations, IReadOnlyList<string> Engines, bool IsOwned);

public sealed record DashboardSummary(
    IReadOnlyList<TargetSummary> Targets,
    int AlertThreshold,
    DateTime? LastRunUtc,
    bool RunRequested,
    int PlansPublished30d,
    int AuditsInRetention);

public sealed record TrendPoint(DateTime AtUtc, decimal Overall, decimal OnPage, decimal Prompt);

public sealed record TargetTrend(string Code, string DisplayName, IReadOnlyList<TrendPoint> Points);

public sealed record SignalMatrix(IReadOnlyList<string> SignalNames, IReadOnlyList<(string Target, IReadOnlyDictionary<string, ScoreSignal> Signals)> Rows);

public sealed record LanguageVisibility(string Target, string LanguageCode, int Evaluated, int Mentioned)
{
    public decimal Rate => Evaluated == 0 ? 0 : Math.Round(100m * Mentioned / Evaluated, 1);
}

public sealed record AuditRow(DateTime AtUtc, string Target, AuditStatus Status, decimal Overall, int? HttpStatus, long DurationMs, string? Error);

public sealed record AnalyticsData(
    IReadOnlyList<TargetTrend> Trends,
    SignalMatrix Signals,
    IReadOnlyList<LanguageVisibility> Languages,
    IReadOnlyList<AuditRow> RecentAudits,
    IReadOnlyList<EngineVisibility> Engines,
    IReadOnlyList<CitedDomain> TopDomains);

public sealed record ReportListItem(
    Guid Id,
    string Title,
    string Target,
    string LanguageCode,
    RemediationStatus Status,
    int ItemCount,
    string? ReportFilePath,
    DateTime CreatedAtUtc);

/// <summary>Mutable edit model for the prompt editor.</summary>
public sealed class PromptEditModel
{
    public Guid? Id { get; set; }
    public Guid TargetProfileId { get; set; }
    public string PromptText { get; set; } = string.Empty;
    public string LanguageCode { get; set; } = "tr-TR";
    public string? Category { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; }
}

/// <summary>Mutable edit model for the AD group → role grid.</summary>
public sealed class RoleMappingEditModel
{
    public Guid? Id { get; set; }
    public string AdGroupCn { get; set; } = string.Empty;
    public AisoRole Role { get; set; } = AisoRole.Viewer;
    public bool IsEnabled { get; set; } = true;
    public string? Notes { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>Editable row of the AI Engines admin tab, with recent usage.</summary>
public sealed class EngineEditModel
{
    public Guid Id { get; set; }
    public string EngineKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public string? DefaultModel { get; set; }
    public string? ModelOverride { get; set; }
    public int? MaxSearchesOverride { get; set; }
    /// <summary>The worker has a key in its own environment; it overrides the dashboard key.</summary>
    public bool KeyConfigured { get; set; }

    // Dashboard key: never the key itself, only its hint and audit fields.
    public bool HasUiKey { get; set; }
    public string? ApiKeyHint { get; set; }
    public DateTime? ApiKeyUpdatedAtUtc { get; set; }
    public string? ApiKeyUpdatedBy { get; set; }

    /// <summary>Write-only input: a new key to store. Cleared after saving.</summary>
    public string? NewApiKey { get; set; }

    public bool RemoveApiKey { get; set; }

    public DateTime? LastSyncedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    // Usage over the last 30 days.
    public int Answers30d { get; set; }
    public int Errors30d { get; set; }
    public long AvgLatencyMs { get; set; }
    public DateTime? LastUsedUtc { get; set; }

    /// <summary>Answers this engine would give per month with the current prompts and audit interval.</summary>
    public int EstimatedAnswersPerMonth { get; set; }

    public bool SupportsSearchLimit => Kind == "AnthropicWebSearch";

    public bool HasAnyKey => KeyConfigured || (HasUiKey && !RemoveApiKey) || !string.IsNullOrWhiteSpace(NewApiKey);
}

/// <summary>A channel in the suggestion feed, with what the Apply kits need to know about it.</summary>
public sealed record FeedChannel(
    Guid Id,
    string Code,
    string DisplayName,
    string BaseUrl,
    TargetChannelKind ChannelKind,
    DateTime? LastAuditUtc,
    bool HasEngineData,
    KitContext Kit);

public sealed record FeedItem(FeedChannel Channel, Suggestion Suggestion);

public sealed record SuggestionFeed(DateTime GeneratedAtUtc, IReadOnlyList<FeedChannel> Channels, IReadOnlyList<FeedItem> Items);

/// <summary>Share of one engine's answers, across all channels, that showed the brand on one day.</summary>
public sealed record EngineDayRate(DateOnly Day, int Answers, int Visible)
{
    public decimal Rate => Answers == 0 ? 0 : Math.Round(100m * Visible / Answers, 1);
}

public sealed record EngineSeries(string EngineKey, string Engine, IReadOnlyList<EngineDayRate> Days);

/// <summary>A cited source: host for display, full URL for the link, and whether the brand owns it.</summary>
public sealed record CitedSource(string Host, string Url, bool IsOwned);

/// <summary>One engine's answer to one test prompt, for the Answers tab.</summary>
public sealed record AnswerRow(
    Guid Id,
    DateTime AtUtc,
    string ChannelCode,
    string Channel,
    string EngineKey,
    string Engine,
    string LanguageCode,
    string Prompt,
    string? Excerpt,
    IReadOnlyList<string> MatchedKeywords,
    IReadOnlyList<string> BrandKeywords,
    bool MentionsBrand,
    bool CitesOwnedDomain,
    IReadOnlyList<CitedSource> Sources,
    string? Error)
{
    public bool IsVisible => MentionsBrand || CitesOwnedDomain;
}

public sealed record AnswerFilter(string? EngineKey, string? ChannelCode, string? LanguageCode, string Visibility, string? Search);

/// <summary>What the live strip shows: an audit queued, running (with progress), or neither.</summary>
public sealed record RunState(bool Queued, AuditRunStatus? Running);

public sealed record AnswerFacets(
    IReadOnlyList<(string Key, string Name)> Engines,
    IReadOnlyList<(string Code, string Name)> Channels,
    IReadOnlyList<string> Languages);
