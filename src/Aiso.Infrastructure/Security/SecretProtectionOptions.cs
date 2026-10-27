namespace Aiso.Infrastructure.Security;

/// <summary>
/// Key ring for secrets entered in the dashboard (AI engine API keys). Bound from <c>Aiso:SecretProtection</c>.
/// The web UI encrypts and the worker decrypts, so both must point at the same key ring and protection.
/// </summary>
public sealed class SecretProtectionOptions
{
    public const string SectionName = "Aiso:SecretProtection";

    /// <summary>
    /// Folder holding the key ring. Must be readable by both the web and worker service accounts and must NOT be
    /// inside a database backup path. Defaults to <c>%ProgramData%\AISO\keys</c> (Windows) or <c>~/.aiso/keys</c>.
    /// </summary>
    public string? KeyRingPath { get; set; }

    /// <summary>
    /// Optional certificate (LocalMachine\My) that encrypts the key ring. When empty on Windows, the key ring is
    /// encrypted with machine-wide DPAPI, so web and worker must run on the same server.
    /// </summary>
    public string? CertificateThumbprint { get; set; }

    public string ResolvedKeyRingPath =>
        !string.IsNullOrWhiteSpace(KeyRingPath)
            ? KeyRingPath
            : OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AISO", "keys")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aiso", "keys");
}
