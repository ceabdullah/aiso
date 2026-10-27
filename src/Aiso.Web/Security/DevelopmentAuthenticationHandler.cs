using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Aiso.Web.Security;

/// <summary>
/// Development-only stand-in for Windows authentication (cloud dev boxes have no domain).
/// Signs in a fixed user carrying the configured AD group claims so the role mapping
/// pipeline is still exercised end-to-end. Refused outside the Development environment.
/// </summary>
public sealed class DevelopmentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AisoDevelopment";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = configuration["Aiso:Security:DevelopmentUser"] ?? @"DEV\aiso.developer";
        var groups = configuration.GetSection("Aiso:Security:DevelopmentGroups").Get<string[]>() ?? ["GG-AISO-Administrators"];

        var identity = new ClaimsIdentity(SchemeName, ClaimTypes.Name, "dev-role-unused");
        identity.AddClaim(new Claim(ClaimTypes.Name, user));
        foreach (var group in groups)
        {
            identity.AddClaim(new Claim("groups", group));
        }

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
