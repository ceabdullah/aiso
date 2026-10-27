namespace Aiso.Infrastructure.Scraping;

/// <summary>
/// The rules of a site's <c>robots.txt</c> that apply to AISO's crawler: the group for its user agent if there is one,
/// otherwise the <c>*</c> group. The longest matching rule wins and Allow wins ties, as major search crawlers do.
/// A missing or unreadable robots.txt allows everything.
/// </summary>
public sealed class RobotsTxt
{
    private readonly List<(string Path, bool Allow)> _rules;

    private RobotsTxt(List<(string Path, bool Allow)> rules, IReadOnlyList<string> sitemaps)
    {
        _rules = rules;
        Sitemaps = sitemaps;
    }

    public static RobotsTxt AllowAll { get; } = new([], []);

    /// <summary>Sitemap URLs listed in the file, for every crawler.</summary>
    public IReadOnlyList<string> Sitemaps { get; }

    /// <param name="userAgentToken">The product token of the crawler, e.g. <c>AISO-Audit-Bot</c>.</param>
    public static RobotsTxt Parse(string? content, string userAgentToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgentToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            return AllowAll;
        }

        var groups = new List<(List<string> Agents, List<(string, bool)> Rules)>();
        var sitemaps = new List<string>();
        (List<string> Agents, List<(string, bool)> Rules)? current = null;
        var lastWasAgent = false;

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var field = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            switch (field)
            {
                case "user-agent":
                    if (current is null || !lastWasAgent)
                    {
                        current = ([], []);
                        groups.Add(current.Value);
                    }

                    current.Value.Agents.Add(value.ToLowerInvariant());
                    lastWasAgent = true;
                    break;
                case "allow" or "disallow" when current is not null:
                    // An empty Disallow means "allow everything" and adds no rule.
                    if (value.Length > 0)
                    {
                        current.Value.Rules.Add((value, field == "allow"));
                    }

                    lastWasAgent = false;
                    break;
                case "sitemap":
                    sitemaps.Add(value);
                    break;
                default:
                    lastWasAgent = false;
                    break;
            }
        }

        var token = userAgentToken.ToLowerInvariant();
        var group = groups.FirstOrDefault(g => g.Agents.Any(a => a != "*" && token.StartsWith(a, StringComparison.Ordinal)));
        if (group.Agents is null)
        {
            group = groups.FirstOrDefault(g => g.Agents.Contains("*"));
        }

        return new RobotsTxt(group.Rules?.ToList() ?? [], sitemaps);
    }

    public bool IsAllowed(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var path = url.PathAndQuery;
        (string Path, bool Allow)? best = null;
        foreach (var rule in _rules)
        {
            if (Matches(rule.Path, path)
                && (best is null || rule.Path.Length > best.Value.Path.Length || (rule.Path.Length == best.Value.Path.Length && rule.Allow)))
            {
                best = rule;
            }
        }

        return best?.Allow ?? true;
    }

    // Supports the two wildcards search crawlers honour: '*' (any characters) and a trailing '$' (end of URL).
    private static bool Matches(string pattern, string path)
    {
        var anchored = pattern.EndsWith('$');
        var parts = (anchored ? pattern[..^1] : pattern).Split('*');
        if (!path.StartsWith(parts[0], StringComparison.Ordinal))
        {
            return false;
        }

        var pos = parts[0].Length;
        for (var i = 1; i < parts.Length; i++)
        {
            var found = path.IndexOf(parts[i], pos, StringComparison.Ordinal);
            if (found < 0)
            {
                return false;
            }

            pos = found + parts[i].Length;
        }

        return !anchored || pos == path.Length || (parts.Length > 1 && path.EndsWith(parts[^1], StringComparison.Ordinal));
    }
}
