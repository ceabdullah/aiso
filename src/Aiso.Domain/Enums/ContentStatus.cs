namespace Aiso.Domain.Enums;

/// <summary>
/// A content item's place in the loop: idea → AI draft → human review → approved → published by the team
/// (live URL pasted back) → checked live by AISO → cited by an AI engine.
/// </summary>
public enum ContentStatus
{
    /// <summary>Proposed from audit findings; nothing written yet.</summary>
    Idea,

    /// <summary>Queued for the worker's writing model.</summary>
    Drafting,

    /// <summary>Drafted; waiting for a person to edit and approve it.</summary>
    InReview,

    /// <summary>Approved for publishing by the owning team.</summary>
    Approved,

    /// <summary>The team published it and saved the live URL; AISO hasn't checked it yet.</summary>
    Published,

    /// <summary>AISO fetched the live URL and found it public (and indexable, for web pages).</summary>
    Live,

    /// <summary>An AI engine cited the live URL in an answer.</summary>
    Cited,

    /// <summary>Not pursued.</summary>
    Dismissed,
}
