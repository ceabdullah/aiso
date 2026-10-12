namespace Aiso.Domain.Entities;

/// <summary>
/// Common base for every persisted aggregate.
/// Keys are client-generated sequential GUIDs (v7) so that no provider-specific
/// identity/sequence strategy is required: the same model maps cleanly to
/// SQL Server (uniqueidentifier) and Oracle (RAW(16)).
/// </summary>
public abstract class EntityBase
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>UTC creation timestamp. Stored without offset for provider portability.</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>UTC last-modified timestamp, maintained by the DbContext.</summary>
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optimistic concurrency token. A GUID stamp is used instead of SQL Server
    /// <c>rowversion</c>, which has no Oracle equivalent.
    /// </summary>
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
}
