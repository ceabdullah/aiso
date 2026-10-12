using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aiso.Infrastructure.Workspace;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aiso.Infrastructure.Reporting;

/// <summary>
/// Turns raw agent output into a structured Markdown checklist under
/// <c>reports/{target}-{lang}-{timestamp}_AISO_remediation_plan.md</c>.
/// Accepts the JSON contract defined in CLAUDE.md and degrades gracefully to free-text parsing
/// when a smaller local model does not follow the contract exactly.
/// </summary>
public sealed partial class AisoMarkdownExporter(IOptions<WorkspaceOptions> workspace, ILogger<AisoMarkdownExporter> logger)
{
    public const string FileSuffix = "_AISO_remediation_plan.md";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public async Task<ExportedReport> ExportAsync(ReportContext context, string rawAgentOutput, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var payload = ParseAgentOutput(rawAgentOutput);
        var markdown = Render(context, payload);

        var ws = workspace.Value;
        Directory.CreateDirectory(ws.ReportsPath);
        var fileName = BuildFileName(context);
        var absolute = Path.Combine(ws.ReportsPath, fileName);

        // Atomic write: readers (dashboard, claude.exe) never observe a half-written report.
        var temp = absolute + ".tmp";
        await File.WriteAllTextAsync(temp, markdown, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
        File.Move(temp, absolute, overwrite: true);

        logger.LogInformation("Exported remediation plan with {Count} item(s) to {Path}.", payload.Items.Count, absolute);
        return new ExportedReport(absolute, ws.ToRelative(absolute), markdown, payload.Items.Count, payload.Summary);
    }

    public static string BuildFileName(ReportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var safeCode = SafeFileRegex().Replace(context.TargetCode, "-");
        var safeLang = SafeFileRegex().Replace(context.LanguageCode, "-");
        return string.Create(CultureInfo.InvariantCulture,
            $"{safeCode}-{safeLang}-{context.GeneratedAtUtc:yyyyMMdd-HHmm}{FileSuffix}");
    }

    /// <summary>Parses the agent's JSON contract (optionally inside a ```json fence), else falls back to line parsing.</summary>
    public static RemediationPayload ParseAgentOutput(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new RemediationPayload(null, []);
        }

        var json = ExtractJson(raw);
        if (json is not null)
        {
            try
            {
                var payload = JsonSerializer.Deserialize<RemediationPayload>(json, JsonOptions);
                if (payload?.Items is { Count: > 0 })
                {
                    return payload with { Items = payload.Items.Where(i => !string.IsNullOrWhiteSpace(i.Title)).ToList() };
                }
            }
            catch (JsonException)
            {
                // fall through to free-text parsing
            }
        }

        return ParseFreeText(raw);
    }

    private static string? ExtractJson(string raw)
    {
        var fence = JsonFenceRegex().Match(raw);
        if (fence.Success)
        {
            return fence.Groups["json"].Value;
        }

        var start = raw.IndexOf('{', StringComparison.Ordinal);
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : null;
    }

    private static RemediationPayload ParseFreeText(string raw)
    {
        var items = new List<RemediationItem>();
        var summary = new StringBuilder();
        RemediationItem? current = null;

        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            var bullet = BulletRegex().Match(line);
            if (bullet.Success)
            {
                if (current is not null)
                {
                    items.Add(current);
                }

                var text = bullet.Groups["text"].Value.Trim();
                var prio = PriorityTagRegex().Match(text);
                var priority = prio.Success ? prio.Groups["p"].Value : null;
                if (prio.Success)
                {
                    text = text.Remove(prio.Index, prio.Length).Trim(' ', '-', ':');
                }

                var (title, detail) = SplitTitle(text);
                current = new RemediationItem(title, detail, priority, null, null);
            }
            else if (current is not null && !string.IsNullOrWhiteSpace(line) && char.IsWhiteSpace(rawLine.FirstOrDefault()))
            {
                current = current with { Detail = string.Join(' ', new[] { current.Detail, line.Trim() }.Where(s => !string.IsNullOrEmpty(s))) };
            }
            else if (current is null && !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
            {
                summary.AppendLine(line.Trim());
            }
        }

        if (current is not null)
        {
            items.Add(current);
        }

        var summaryText = summary.ToString().Trim();
        return new RemediationPayload(summaryText.Length == 0 ? null : summaryText, items);
    }

    private static (string Title, string? Detail) SplitTitle(string text)
    {
        text = text.Replace("**", string.Empty, StringComparison.Ordinal);
        var idx = text.IndexOfAny([':', '—']);
        return idx is > 3 and < 120
            ? (text[..idx].Trim(), text[(idx + 1)..].Trim() is { Length: > 0 } d ? d : null)
            : (text, null);
    }

    public static RemediationPriority ParsePriority(string? raw)
    {
        var p = (raw ?? string.Empty).Trim().ToUpperInvariant();
        return p switch
        {
            "CRITICAL" or "KRİTİK" or "KRITIK" or "P0" => RemediationPriority.Critical,
            "HIGH" or "YÜKSEK" or "YUKSEK" or "P1" => RemediationPriority.High,
            "LOW" or "DÜŞÜK" or "DUSUK" or "P3" => RemediationPriority.Low,
            _ => RemediationPriority.Medium,
        };
    }

    public static string Render(ReportContext context, RemediationPayload payload)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(payload);

        var s = ReportLocalization.For(context.LanguageCode);
        var inv = CultureInfo.InvariantCulture;
        var md = new StringBuilder();

        md.Append(inv, $"# {s.Title} — {Escape(context.TargetName)}").AppendLine().AppendLine();
        md.AppendLine("| | |").AppendLine("|---|---|");
        md.Append(inv, $"| **{s.Target}** | [{Escape(context.TargetName)}]({context.TargetUrl}) (`{context.TargetCode}`) |").AppendLine();
        md.Append(inv, $"| **{s.Generated}** | {context.GeneratedAtUtc:yyyy-MM-dd HH:mm} UTC |").AppendLine();
        md.Append(inv, $"| **{s.Language}** | `{context.LanguageCode}` |").AppendLine();
        if (!string.IsNullOrWhiteSpace(context.AgentSessionId))
        {
            md.Append(inv, $"| **Agent session** | `{context.AgentSessionId}` |").AppendLine();
        }

        md.AppendLine();

        if (context.OverallScore is not null)
        {
            md.Append(inv, $"## {s.Scores}").AppendLine().AppendLine();
            md.AppendLine("| Metric | Score | |").AppendLine("|---|---:|---|");
            AppendScore(md, s.Overall, context.OverallScore);
            AppendScore(md, s.OnPage, context.OnPageScore);
            AppendScore(md, s.PromptVisibility, context.PromptVisibilityScore);
            md.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(payload.Summary))
        {
            md.Append(inv, $"## {s.Summary}").AppendLine().AppendLine();
            md.AppendLine(payload.Summary.Trim()).AppendLine();
        }

        md.Append(inv, $"## {s.Checklist}").AppendLine().AppendLine();
        if (payload.Items.Count == 0)
        {
            md.Append(inv, $"> {s.NoItems}").AppendLine().AppendLine();
        }

        foreach (var group in payload.Items.GroupBy(i => i.Priority).OrderBy(g => g.Key))
        {
            md.Append(inv, $"### {PriorityIcon(group.Key)} {s.PriorityNames[group.Key]}").AppendLine().AppendLine();
            foreach (var item in group)
            {
                md.Append(inv, $"- [ ] **{Escape(item.Title)}**");
                var tags = new List<string>();
                if (!string.IsNullOrWhiteSpace(item.Category)) tags.Add($"{s.Category}: `{item.Category}`");
                if (!string.IsNullOrWhiteSpace(item.Owner)) tags.Add($"{s.Owner}: {Escape(item.Owner)}");
                if (tags.Count > 0)
                {
                    md.Append(" — ").Append(string.Join(" · ", tags));
                }

                md.AppendLine();
                if (!string.IsNullOrWhiteSpace(item.Detail))
                {
                    foreach (var detailLine in item.Detail.Trim().Split('\n'))
                    {
                        md.Append("  ").AppendLine(detailLine.TrimEnd());
                    }
                }
            }

            md.AppendLine();
        }

        md.AppendLine("---").Append(inv, $"_{s.Footer}_").AppendLine();
        return md.ToString();
    }

    private static void AppendScore(StringBuilder md, string label, decimal? score)
    {
        if (score is null)
        {
            return;
        }

        var bar = new string('█', (int)Math.Round(score.Value / 10m)).PadRight(10, '░');
        md.Append(CultureInfo.InvariantCulture, $"| {label} | {score.Value:0.0} | `{bar}` |").AppendLine();
    }

    private static string PriorityIcon(RemediationPriority p) => p switch
    {
        RemediationPriority.Critical => "🔴",
        RemediationPriority.High => "🟠",
        RemediationPriority.Medium => "🟡",
        _ => "🟢",
    };

    /// <summary>Neutralises characters that would break table/list structure or inject HTML.</summary>
    private static string Escape(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal)
             .Replace("<", "&lt;", StringComparison.Ordinal)
             .Replace(">", "&gt;", StringComparison.Ordinal)
             .ReplaceLineEndings(" ");

    [GeneratedRegex(@"```(?:json)?\s*(?<json>\{.*?\})\s*```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex JsonFenceRegex();

    [GeneratedRegex(@"^\s*(?:[-*•]|\d+[.)])\s+(?:\[[ xX]\]\s*)?(?<text>.+)$")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"^\s*[\[(](?<p>critical|high|medium|low|kritik|yüksek|orta|düşük|p[0-3])[\])]", RegexOptions.IgnoreCase)]
    private static partial Regex PriorityTagRegex();

    [GeneratedRegex(@"[^A-Za-z0-9_-]")]
    private static partial Regex SafeFileRegex();
}
