using Aiso.Domain.Enums;

namespace Aiso.Domain.Entities;

/// <summary>
/// Maps an Active Directory group (by CN) onto an application role.
/// Managed from the UI so that group names can change without a code deployment.
/// </summary>
public class SecurityRoleMapping : EntityBase
{
    /// <summary>The group's common name, e.g. <c>GG-AISO-Admins</c> (matched case-insensitively).</summary>
    public required string AdGroupCn { get; set; }

    public AisoRole Role { get; set; } = AisoRole.Viewer;

    public bool IsEnabled { get; set; } = true;

    public string? Notes { get; set; }
}
