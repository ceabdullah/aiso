using Aiso.Domain.Enums;

namespace Aiso.Web.Security;

public static class AisoPolicies
{
    public const string CanView = nameof(CanView);
    public const string CanEdit = nameof(CanEdit);
    public const string Admin = nameof(Admin);

    /// <summary>Role claim value emitted for an application role.</summary>
    public static string RoleClaim(AisoRole role) => $"Aiso.{role}";
}
