using Aiso.Domain.Enums;

namespace Aiso.Domain.Entities;

/// <summary>An agent-produced remediation plan, exported as a Markdown checklist.</summary>
public class AisoRemediationPlan : EntityBase
{
    public Guid TargetProfileId { get; set; }
    public TargetProfile? TargetProfile { get; set; }

    public Guid? AuditLogId { get; set; }
    public LiveSearchAuditLog? AuditLog { get; set; }

    public required string Title { get; set; }

    public required string LanguageCode { get; set; }

    public RemediationStatus Status { get; set; } = RemediationStatus.Draft;

    /// <summary>Raw agent output as received from the CLI runtime (large text).</summary>
    public string? RawAgentOutput { get; set; }

    /// <summary>Rendered Markdown document (large text). Mirrors the file on disk.</summary>
    public string? MarkdownContent { get; set; }

    /// <summary>Relative path of the exported report, e.g. <c>reports/teb-portal_AISO_remediation_plan.md</c>.</summary>
    public string? ReportFilePath { get; set; }

    public int ItemCount { get; set; }

    /// <summary>Claude Code session id reported by the CLI, for traceability.</summary>
    public string? AgentSessionId { get; set; }
}
