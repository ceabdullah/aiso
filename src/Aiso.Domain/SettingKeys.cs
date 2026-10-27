namespace Aiso.Domain;

/// <summary>Well-known <see cref="Entities.GlobalSystemSetting"/> keys and their defaults.</summary>
public static class SettingKeys
{
    public const string DataRetentionDays = "DataRetentionDays";
    public const string CleanupIntervalDays = "CleanupIntervalDays";
    public const string VisibilityAlertThreshold = "VisibilityAlertThreshold";
    public const string AlertRecipients = "AlertRecipients";
    public const string AuditIntervalHours = "AuditIntervalHours";
    public const string AuditRunRequestedAtUtc = "AuditRunRequestedAtUtc";
    public const string DefaultUiTheme = "DefaultUiTheme";
    public const string SearchCountry = "SearchCountry";
    public const string SearchRegion = "SearchRegion";
    public const string SearchCity = "SearchCity";
    public const string SearchTimezone = "SearchTimezone";
    public const string AuditRunStatus = "AuditRunStatus";
    public const string GraphicsBackgroundColor = "GraphicsBackgroundColor";
    public const string GraphicsAccentColor = "GraphicsAccentColor";

    public static readonly IReadOnlyDictionary<string, (string Value, string Description)> Defaults =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            [DataRetentionDays] = ("30", "Audit log records older than this many days are purged."),
            [CleanupIntervalDays] = ("7", "How often (days) the retention maintenance sweep runs."),
            [VisibilityAlertThreshold] = ("60", "Overall AISO score (0-100) below which an alert email is sent."),
            [AlertRecipients] = ("aiso-audit@example.com", "Semicolon-separated alert recipients."),
            [AuditIntervalHours] = ("24", "How often (hours) the full audit pipeline runs automatically."),
            [AuditRunRequestedAtUtc] = ("", "Set by the dashboard to request an immediate pipeline run."),
            [DefaultUiTheme] = ("dark", "Default dashboard theme: dark or light."),
            [SearchCountry] = ("TR", "Two-letter country the AI engines search from (empty: no location)."),
            [SearchRegion] = ("Istanbul", "Region or province the AI engines search from."),
            [SearchCity] = ("Istanbul", "City the AI engines search from."),
            [SearchTimezone] = ("Europe/Istanbul", "IANA time zone sent with the search location."),
            [AuditRunStatus] = ("", "Progress of the running audit, written by the worker (JSON; empty when idle)."),
            [GraphicsBackgroundColor] = ("#14213D", "Background of the content graphics (a dark brand colour; text on it is white)."),
            [GraphicsAccentColor] = ("#1FA67A", "Accent colour of the content graphics (bars, numbers, highlights)."),
        };
}
