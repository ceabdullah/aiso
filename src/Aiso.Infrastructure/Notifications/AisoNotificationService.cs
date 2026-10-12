using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text;
using Aiso.Domain;
using Aiso.Infrastructure.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aiso.Infrastructure.Notifications;

public sealed record VisibilityAlertRow(
    string TargetName,
    string TargetUrl,
    decimal OverallScore,
    decimal OnPageScore,
    decimal PromptVisibilityScore,
    decimal? PreviousOverallScore,
    string? ReportPath);

public interface IAisoNotificationService
{
    /// <summary>Sends an alert for rows below the configured threshold. Returns false when nothing was sent.</summary>
    Task<bool> SendVisibilityAlertAsync(IReadOnlyList<VisibilityAlertRow> rows, CancellationToken cancellationToken);
}

/// <summary>Dispatches HTML visibility-alert tables to the banking audit distribution list via the internal SMTP relay.</summary>
public sealed class AisoNotificationService(
    IOptions<SmtpOptions> smtpOptions,
    ISystemSettingsService settings,
    ILogger<AisoNotificationService> logger) : IAisoNotificationService
{
    public async Task<bool> SendVisibilityAlertAsync(IReadOnlyList<VisibilityAlertRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var options = smtpOptions.Value;

        var threshold = await settings.GetIntAsync(SettingKeys.VisibilityAlertThreshold, cancellationToken);
        var breaching = rows.Where(r => r.OverallScore < threshold).OrderBy(r => r.OverallScore).ToList();
        if (breaching.Count == 0)
        {
            return false;
        }

        var recipients = (await settings.GetStringAsync(SettingKeys.AlertRecipients, cancellationToken))
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (!options.Enabled || recipients.Length == 0)
        {
            logger.LogWarning("{Count} target(s) below threshold {Threshold} but SMTP is disabled or no recipients configured.",
                breaching.Count, threshold);
            return false;
        }

        using var message = new MailMessage
        {
            From = new MailAddress(options.FromAddress, options.FromDisplayName),
            Subject = string.Create(CultureInfo.InvariantCulture,
                $"[AISO] Visibility alert: {breaching.Count} channel(s) below {threshold}"),
            Body = BuildHtml(breaching, threshold, options.DashboardUrl, DateTime.UtcNow),
            IsBodyHtml = true,
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8,
            Priority = breaching.Any(r => r.OverallScore < threshold / 2m) ? MailPriority.High : MailPriority.Normal,
        };

        foreach (var recipient in recipients)
        {
            message.To.Add(recipient);
        }

        using var client = new SmtpClient(options.Host, options.Port)
        {
            EnableSsl = options.EnableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = options.TimeoutSeconds * 1000,
        };

        if (!string.IsNullOrWhiteSpace(options.Username))
        {
            client.Credentials = new NetworkCredential(options.Username, options.Password);
        }
        else
        {
            client.UseDefaultCredentials = true; // integrated auth against an Exchange relay
        }

        try
        {
            await client.SendMailAsync(message, cancellationToken);
            logger.LogInformation("Visibility alert sent to {Recipients} for {Count} target(s).", string.Join(";", recipients), breaching.Count);
            return true;
        }
        catch (SmtpException ex)
        {
            // Alerting must never take down the audit pipeline.
            logger.LogError(ex, "Failed to send visibility alert email via {Host}:{Port}.", options.Host, options.Port);
            return false;
        }
    }

    /// <summary>Builds an email-client-safe (inline-styled, table-based) HTML body. Every value is HTML-encoded.</summary>
    public static string BuildHtml(IReadOnlyList<VisibilityAlertRow> rows, int threshold, string? dashboardUrl, DateTime generatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(rows);
        static string E(string? v) => WebUtility.HtmlEncode(v ?? string.Empty);
        var inv = CultureInfo.InvariantCulture;

        var sb = new StringBuilder();
        sb.Append("""
            <!DOCTYPE html><html><body style="margin:0;padding:24px;background:#0f172a;font-family:Segoe UI,Arial,sans-serif;color:#e2e8f0;">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:760px;margin:0 auto;background:#111827;border:1px solid #1f2937;border-radius:8px;">
            <tr><td style="padding:20px 24px;border-bottom:1px solid #1f2937;">
            <div style="font-size:12px;letter-spacing:.08em;text-transform:uppercase;color:#94a3b8;">AISO Visibility Platform</div>
            <div style="font-size:20px;font-weight:600;color:#f8fafc;margin-top:4px;">AI search visibility below threshold</div>
            """);
        sb.Append(inv, $"""<div style="font-size:13px;color:#94a3b8;margin-top:6px;">Threshold {threshold} &middot; generated {generatedAtUtc:yyyy-MM-dd HH:mm} UTC</div></td></tr>""");
        sb.Append("""
            <tr><td style="padding:16px 24px;">
            <table width="100%" cellpadding="8" cellspacing="0" style="border-collapse:collapse;font-size:13px;">
            <tr style="color:#94a3b8;text-align:left;border-bottom:1px solid #334155;">
            <th align="left">Channel</th><th align="right">Overall</th><th align="right">On-page</th><th align="right">Prompt visibility</th><th align="right">&Delta; prev</th><th align="left">Report</th></tr>
            """);

        foreach (var r in rows)
        {
            var color = r.OverallScore < threshold / 2m ? "#f87171" : "#fbbf24";
            var delta = r.PreviousOverallScore is { } prev
                ? (r.OverallScore - prev).ToString("+0.0;-0.0;0.0", inv)
                : "n/a";
            sb.Append(inv, $"""
                <tr style="border-bottom:1px solid #1f2937;">
                <td><a href="{E(r.TargetUrl)}" style="color:#93c5fd;text-decoration:none;">{E(r.TargetName)}</a></td>
                <td align="right" style="color:{color};font-weight:600;">{r.OverallScore:0.0}</td>
                <td align="right">{r.OnPageScore:0.0}</td>
                <td align="right">{r.PromptVisibilityScore:0.0}</td>
                <td align="right">{E(delta)}</td>
                <td style="font-family:Consolas,monospace;font-size:12px;color:#cbd5e1;">{E(r.ReportPath ?? "—")}</td>
                </tr>
                """);
        }

        sb.Append("</table></td></tr>");
        if (!string.IsNullOrWhiteSpace(dashboardUrl))
        {
            sb.Append(inv, $"""<tr><td style="padding:0 24px 20px;"><a href="{E(dashboardUrl)}" style="display:inline-block;background:#2563eb;color:#fff;padding:8px 14px;border-radius:6px;text-decoration:none;font-size:13px;">Open dashboard</a></td></tr>""");
        }

        sb.Append("""<tr><td style="padding:12px 24px;border-top:1px solid #1f2937;font-size:11px;color:#64748b;">Automated message. Targets are audited externally as black boxes; no production systems were modified.</td></tr></table></body></html>""");
        return sb.ToString();
    }
}
