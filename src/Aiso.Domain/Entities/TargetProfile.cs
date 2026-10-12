using Aiso.Domain.Enums;

namespace Aiso.Domain.Entities;

/// <summary>
/// An external, black-box channel whose AI-search visibility is tracked.
/// Nothing in the platform depends on the target's internal implementation.
/// </summary>
public class TargetProfile : EntityBase
{
    /// <summary>Stable short code used in file names and report paths (e.g. <c>teb-portal</c>).</summary>
    public required string Code { get; set; }

    public required string DisplayName { get; set; }

    public required string BaseUrl { get; set; }

    public TargetChannelKind ChannelKind { get; set; }

    /// <summary>Brand terms that count as a "mention" when probing LLM answers (comma separated).</summary>
    public string BrandKeywords { get; set; } = string.Empty;

    /// <summary>
    /// Domains the brand owns (comma separated). An entry may include a path prefix, e.g.
    /// <c>apps.apple.com/tr/app/teb-mobil</c>. A citation of any of them counts as visibility.
    /// Nullable because Oracle stores an empty string as NULL.
    /// </summary>
    public string? OwnedDomains { get; set; }

    /// <summary>Primary content language of the target, BCP-47 (e.g. <c>tr-TR</c>).</summary>
    public string PrimaryLanguageCode { get; set; } = "tr-TR";

    public bool IsActive { get; set; } = true;

    public ICollection<AutomatedTestPrompt> TestPrompts { get; } = [];
    public ICollection<LiveSearchAuditLog> AuditLogs { get; } = [];
    public ICollection<AisoRemediationPlan> RemediationPlans { get; } = [];

    public IReadOnlyList<string> GetOwnedDomains() =>
        (OwnedDomains ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public IReadOnlyList<string> GetBrandKeywords() =>
        BrandKeywords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
