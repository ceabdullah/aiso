using Aiso.Domain.Enums;

namespace Aiso.Domain.Entities;

/// <summary>
/// One piece of content in the content loop: proposed from audit findings, drafted by the writing model,
/// reviewed by a person, published by the owning team (AISO never publishes), then checked live and tracked
/// until an AI engine cites it.
/// </summary>
public class ContentItem : EntityBase
{
    public Guid TargetProfileId { get; set; }
    public TargetProfile? TargetProfile { get; set; }

    public ContentKind Kind { get; set; }

    public ContentStatus Status { get; set; } = ContentStatus.Idea;

    public required string Title { get; set; }

    /// <summary>The customer question the content answers.</summary>
    public required string Question { get; set; }

    public required string LanguageCode { get; set; }

    /// <summary>Which finding proposed it, e.g. <c>no-page</c>, <c>brand-missing</c>, <c>app-reviews</c>.</summary>
    public required string Source { get; set; }

    /// <summary>Why it is worth writing, from the audit data.</summary>
    public string? Rationale { get; set; }

    /// <summary>The draft (Markdown), as written by the model and edited by people (large text).</summary>
    public string? Draft { get; set; }

    /// <summary>For social posts: a description of the image to create.</summary>
    public string? ImagePrompt { get; set; }

    /// <summary>For social posts: the network, e.g. LinkedIn.</summary>
    public string? Platform { get; set; }

    public string? ApprovedBy { get; set; }

    public string? UpdatedBy { get; set; }

    /// <summary>The published address, pasted back by the team.</summary>
    public string? LiveUrl { get; set; }

    public DateTime? LiveCheckedAtUtc { get; set; }

    /// <summary>What the live check found.</summary>
    public string? LiveNote { get; set; }

    public DateTime? CitedAtUtc { get; set; }

    /// <summary>Engines that cited the live URL.</summary>
    public string? CitedBy { get; set; }

    /// <summary>The last drafting or checking error.</summary>
    public string? Error { get; set; }
}
