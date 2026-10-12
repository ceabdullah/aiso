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
        };
}
