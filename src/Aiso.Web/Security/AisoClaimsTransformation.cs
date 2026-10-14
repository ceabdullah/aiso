using System.Security.Claims;
using System.Security.Principal;
using Aiso.Domain.Enums;
using Microsoft.AspNetCore.Authentication;

namespace Aiso.Web.Security;

/// <summary>
/// Maps the signed-in user's Active Directory groups to application roles using the
/// UI-managed <see cref="Domain.Entities.SecurityRoleMapping"/> table — no code change when group names change.
/// Roles are added as a separate identity so the Windows identity is left untouched.
/// </summary>
public sealed class AisoClaimsTransformation(RoleMappingProvider mappings, ILogger<AisoClaimsTransformation> logger) : IClaimsTransformation
{
    public const string AuthenticationType = "AisoRoleMapping";

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity?.IsAuthenticated != true ||
            principal.Identities.Any(i => i.AuthenticationType == AuthenticationType))
        {
            return principal;
        }

        var groups = GetGroupCommonNames(principal);
        var lookup = await mappings.GetAsync();
        var roles = groups.SelectMany(g => lookup[g]).Distinct().ToList();

        // Role hierarchy: Administrator ⊃ Analyst ⊃ Viewer.
        if (roles.Contains(AisoRole.Administrator)) roles.AddRange([AisoRole.Analyst, AisoRole.Viewer]);
        if (roles.Contains(AisoRole.Analyst)) roles.Add(AisoRole.Viewer);

        var identity = new ClaimsIdentity(AuthenticationType, ClaimTypes.Name, ClaimTypes.Role);
        foreach (var role in roles.Distinct())
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, AisoPolicies.RoleClaim(role)));
        }

        logger.LogDebug("User {User}: {GroupCount} group(s) → roles [{Roles}]", principal.Identity.Name, groups.Count, string.Join(", ", roles.Distinct()));

        var clone = principal.Clone();
        clone.AddIdentity(identity);
        return clone;
    }

    internal static HashSet<string> GetGroupCommonNames(ClaimsPrincipal principal)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Windows (IIS / Kestrel on Windows): translate group SIDs to DOMAIN\Group names.
        if (OperatingSystem.IsWindows())
        {
            foreach (var wi in principal.Identities.OfType<WindowsIdentity>())
            {
                if (wi.Groups is null) continue;
                foreach (var reference in wi.Groups)
                {
                    try
                    {
                        if (reference.Translate(typeof(NTAccount)) is NTAccount account &&
                            AdGroupNameParser.ToCommonName(account.Value) is { } cn)
                        {
                            names.Add(cn);
                        }
                    }
                    catch (IdentityNotMappedException)
                    {
                        // Orphaned / foreign SID — ignore.
                    }
                }
            }
        }

        // Linux + Negotiate LDAP mode, or other handlers: groups surface as role claims (name or DN).
        foreach (var claim in principal.Claims.Where(c => c.Type is ClaimTypes.Role or "groups" or "memberOf"))
        {
            if (AdGroupNameParser.ToCommonName(claim.Value) is { } cn)
            {
                names.Add(cn);
            }
        }

        return names;
    }
}
