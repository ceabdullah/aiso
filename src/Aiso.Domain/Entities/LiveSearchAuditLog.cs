using Aiso.Domain.Enums;

namespace Aiso.Domain.Entities;

/// <summary>One audit run against one target: on-page signals plus LLM prompt visibility.</summary>
public class LiveSearchAuditLog : EntityBase
{
    public Guid TargetProfileId { get; set; }
    public TargetProfile? TargetProfile { get; set; }

    /// <summary>Correlates all targets audited in the same pipeline run.</summary>
    public Guid RunId { get; set; }

    public AuditStatus Status { get; set; } = AuditStatus.Pending;

    public int? HttpStatusCode { get; set; }

    /// <summary>0-100 score from structural/on-page signals (headings, schema.org, meta, lang).</summary>
    public decimal OnPageScore { get; set; }

    /// <summary>0-100 share of test prompts whose LLM answer mentioned the brand.</summary>
    public decimal PromptVisibilityScore { get; set; }

    /// <summary>Weighted overall AISO score (0-100).</summary>
    public decimal OverallScore { get; set; }

    public int PromptsEvaluated { get; set; }
    public int PromptsMentioningBrand { get; set; }

    /// <summary>Relative path (from workspace root) of the raw scrape snapshot JSON.</summary>
    public string? SnapshotPath { get; set; }

    /// <summary>Serialized JSON of the extracted metrics (large text column).</summary>
    public string? MetricsJson { get; set; }

    public string? ErrorMessage { get; set; }

    public long DurationMs { get; set; }
}
