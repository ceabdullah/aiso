using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aiso.Infrastructure.Security;

public static class SecretProtectionExtensions
{
    /// <summary>Shared ASP.NET Core Data Protection key ring for the web app and the worker.</summary>
    public static IHostApplicationBuilder AddAisoSecretProtection(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = builder.Configuration.GetSection(SecretProtectionOptions.SectionName).Get<SecretProtectionOptions>()
                      ?? new SecretProtectionOptions();

        var keyRing = new DirectoryInfo(options.ResolvedKeyRingPath);
        keyRing.Create();

        var dp = builder.Services.AddDataProtection()
            .SetApplicationName("AISO") // same name in web and worker, so they share keys
            .PersistKeysToFileSystem(keyRing);

        if (!string.IsNullOrWhiteSpace(options.CertificateThumbprint))
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            var cert = store.Certificates.Find(X509FindType.FindByThumbprint, options.CertificateThumbprint, validOnly: false)
                .OfType<X509Certificate2>().FirstOrDefault()
                ?? throw new InvalidOperationException($"Certificate {options.CertificateThumbprint} not found in LocalMachine\\My.");
            dp.ProtectKeysWithCertificate(cert);
        }
        else if (OperatingSystem.IsWindows())
        {
            dp.ProtectKeysWithDpapi(protectToLocalMachine: true);
        }

        // On Linux development machines without a certificate the key ring is stored unencrypted; the
        // folder's file permissions are the only protection. Production runs on Windows (DPAPI) or uses a certificate.
        builder.Services.AddSingleton<EngineApiKeyProtector>();
        return builder;
    }
}
