using Aiso.Domain.Enums;
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
    int ActivePrompts);

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
    IReadOnlyList<AuditRow> RecentAudits);

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
