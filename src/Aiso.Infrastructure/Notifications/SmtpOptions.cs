namespace Aiso.Infrastructure.Notifications;

/// <summary>Internal relay settings. Bound from <c>Aiso:Smtp</c>; credentials via environment / secret store only.</summary>
public sealed class SmtpOptions
{
    public const string SectionName = "Aiso:Smtp";

    public bool Enabled { get; set; }

    public string Host { get; set; } = "smtp.corp.local";

    public int Port { get; set; } = 25;

    public bool EnableSsl { get; set; } = true;

    public string FromAddress { get; set; } = "aiso-platform@example.com";

    public string FromDisplayName { get; set; } = "AISO Visibility Platform";

    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>Absolute dashboard URL included in alert emails, e.g. <c>https://aiso.corp.local</c>.</summary>
    public string? DashboardUrl { get; set; }

    public int TimeoutSeconds { get; set; } = 30;
}
