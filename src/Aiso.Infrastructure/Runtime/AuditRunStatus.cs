using System.Text.Json;

namespace Aiso.Infrastructure.Runtime;

/// <summary>
/// Progress of the audit the worker is running, stored as JSON in the <c>AuditRunStatus</c> setting so the
/// dashboard can show it live. Shared by the worker (writer) and the dashboard (reader).
/// </summary>
public sealed record AuditRunStatus(
    DateTime StartedAtUtc,
    DateTime UpdatedAtUtc,
    string Stage,
    string? Target,
    int TargetsDone,
    int TargetsTotal,
    int AnswersDone,
    int AnswersTotal,
    string? LastEngine)
{
    /// <summary>A status not updated for this long belongs to a worker that stopped mid-run.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>The running audit, or null when idle, unreadable or stale.</summary>
    public static AuditRunStatus? TryParse(string? json, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var status = JsonSerializer.Deserialize<AuditRunStatus>(json, Options);
            return status is not null && nowUtc - status.UpdatedAtUtc < StaleAfter ? status : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
