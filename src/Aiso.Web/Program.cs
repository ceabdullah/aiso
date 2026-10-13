using Aiso.Domain.Enums;
using Aiso.Infrastructure;
using Aiso.Web.Components;
using Aiso.Web.Security;
using Aiso.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddAisoCore();

// ---- Authentication: Windows/Kerberos (Negotiate) on-prem; a stub handler for cloud development.
var useDevelopmentAuth = builder.Configuration.GetValue<bool>("Aiso:Security:UseDevelopmentAuthentication");
if (useDevelopmentAuth && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException("Aiso:Security:UseDevelopmentAuthentication is only permitted in the Development environment.");
}

if (useDevelopmentAuth)
{
    builder.Services.AddAuthentication(DevelopmentAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(DevelopmentAuthenticationHandler.SchemeName, null);
}
else
{
    builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate(o =>
    {
        // On Linux hosts, resolve group membership via LDAP so AD groups appear as role claims.
        var domain = builder.Configuration["Aiso:Security:LdapDomain"];
        if (!OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(domain))
        {
            o.EnableLdap(domain);
        }
    });
}

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<RoleMappingProvider>();
builder.Services.AddTransient<IClaimsTransformation, AisoClaimsTransformation>();
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AisoPolicies.CanView, p => p.RequireRole(
        AisoPolicies.RoleClaim(AisoRole.Viewer), AisoPolicies.RoleClaim(AisoRole.Analyst), AisoPolicies.RoleClaim(AisoRole.Administrator)))
    .AddPolicy(AisoPolicies.CanEdit, p => p.RequireRole(AisoPolicies.RoleClaim(AisoRole.Analyst), AisoPolicies.RoleClaim(AisoRole.Administrator)))
    .AddPolicy(AisoPolicies.Admin, p => p.RequireRole(AisoPolicies.RoleClaim(AisoRole.Administrator)))
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<DashboardEvents>();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapDefaultEndpoints();

// Raw Markdown download for a generated remediation plan.
app.MapGet("/reports/{id:guid}/download", async (Guid id, DashboardService dashboard, CancellationToken ct) =>
        await dashboard.GetReportContentAsync(id, ct) is { } report
            ? Results.File(System.Text.Encoding.UTF8.GetBytes(report.Markdown), "text/markdown; charset=utf-8", report.FileName)
            : Results.NotFound())
    .RequireAuthorization(AisoPolicies.CanView);

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

await app.RunAsync();
