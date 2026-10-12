namespace Aiso.Infrastructure.Runtime;

/// <summary>
/// Settings for launching the local Claude Code CLI (<c>claude.exe</c>) as the agent runtime.
/// Bound from <c>Aiso:ClaudeCli</c>.
/// </summary>
public sealed class ClaudeCliOptions
{
    public const string SectionName = "Aiso:ClaudeCli";

    /// <summary>Set false to skip agent analysis entirely (e.g. on build agents without the CLI).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Executable name on PATH or an absolute path, e.g. <c>C:\Tools\claude\claude.exe</c>.</summary>
    public string ExecutablePath { get; set; } = "claude.exe";

    /// <summary>Working directory for the CLI. Defaults to the workspace root when empty.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Claude Code permission mode. With the default read-only tool allow-list, <c>default</c> never
    /// prompts. Use <c>acceptEdits</c> only if the agent must write files itself.
    /// </summary>
    public string PermissionMode { get; set; } = "default";

    /// <summary>Least-privilege tool allow-list. The platform, not the agent, writes reports.</summary>
    public IList<string> AllowedTools { get; } = ["Read", "Glob", "Grep"];

    /// <summary>Tools that are always denied, even if a CLAUDE.md or setting tries to enable them.</summary>
    public IList<string> DisallowedTools { get; } = ["Bash", "WebFetch", "WebSearch"];

    public int MaxTurns { get; set; } = 25;

    public int TimeoutMinutes { get; set; } = 20;

    /// <summary>Total attempts for a failed (non-zero exit / timeout) invocation.</summary>
    public int MaxAttempts { get; set; } = 2;

    /// <summary>Optional corporate root CA bundle (PEM) for TLS inspection proxies → <c>NODE_EXTRA_CA_CERTS</c>.</summary>
    public string? ExtraCaCertificatePath { get; set; }

    /// <summary>Optional outbound proxy for the CLI process → <c>HTTPS_PROXY</c>.</summary>
    public string? HttpsProxy { get; set; }

    /// <summary>Disables auto-update, telemetry and error reporting — required on an air-gapped network.</summary>
    public bool DisableNonEssentialTraffic { get; set; } = true;
}
