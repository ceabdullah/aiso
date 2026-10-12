namespace Aiso.Infrastructure.Engines;

/// <summary>
/// Decides whether a cited source belongs to the brand. Entries are hosts (<c>teb.com.tr</c>, which also
/// covers subdomains) optionally followed by a path prefix (<c>apps.apple.com/tr/app/teb-mobil</c>).
/// </summary>
public static class OwnedDomainMatcher
{
    public static IReadOnlyList<EngineCitation> FindOwned(IEnumerable<EngineCitation> citations, IReadOnlyList<string> ownedEntries)
    {
        ArgumentNullException.ThrowIfNull(citations);
        ArgumentNullException.ThrowIfNull(ownedEntries);
        var rules = ownedEntries.Select(Parse).Where(r => r.Host.Length > 0).ToList();
        if (rules.Count == 0)
        {
            return [];
        }

        return citations.Where(c => IsOwned(c, rules)).ToList();
    }

    /// <summary>Host of a cited URL, or of the title when the URL is an opaque redirect (Gemini).</summary>
    public static string? HostOf(EngineCitation citation)
    {
        ArgumentNullException.ThrowIfNull(citation);
        if (Uri.TryCreate(citation.Url, UriKind.Absolute, out var uri) && !IsRedirector(uri.Host))
        {
            return Normalize(uri.Host);
        }

        // Gemini grounding returns vertexaisearch redirect URLs and puts the source domain in the title.
        var title = citation.Title?.Trim();
        return !string.IsNullOrEmpty(title) && title.Contains('.', StringComparison.Ordinal) && !title.Contains(' ', StringComparison.Ordinal)
            ? Normalize(title)
            : null;
    }

    private static bool IsOwned(EngineCitation citation, List<(string Host, string Path)> rules)
    {
        var host = HostOf(citation);
        if (host is null)
        {
            return false;
        }

        var path = Uri.TryCreate(citation.Url, UriKind.Absolute, out var uri) && !IsRedirector(uri.Host)
            ? uri.AbsolutePath
            : "/";

        return rules.Any(r =>
            (host == r.Host || host.EndsWith("." + r.Host, StringComparison.Ordinal)) &&
            (r.Path.Length == 0 || path.StartsWith(r.Path, StringComparison.OrdinalIgnoreCase)));
    }

    private static (string Host, string Path) Parse(string entry)
    {
        var e = entry.Trim();
        if (e.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(e, UriKind.Absolute, out var uri))
        {
            return (Normalize(uri.Host), uri.AbsolutePath.TrimEnd('/'));
        }

        var slash = e.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? (Normalize(e), string.Empty) : (Normalize(e[..slash]), e[slash..].TrimEnd('/'));
    }

    private static string Normalize(string host)
    {
        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        return h.StartsWith("www.", StringComparison.Ordinal) ? h[4..] : h;
    }

    private static bool IsRedirector(string host) =>
        host.EndsWith("vertexaisearch.cloud.google.com", StringComparison.OrdinalIgnoreCase);
}
