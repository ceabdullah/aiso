using System.Text.RegularExpressions;

namespace Aiso.Web.Services;

/// <summary>
/// Turns an engine's Markdown answer into plain readable text for the dashboard. Answers are untrusted, so they
/// are never rendered as HTML; this only removes the Markdown syntax (links keep their text, headings and
/// emphasis lose their markers) and collapses blank lines.
/// </summary>
public static partial class AnswerText
{
    public static string ToPlain(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var text = MarkdownLink().Replace(markdown, "$1");
        text = Heading().Replace(text, string.Empty);
        text = Emphasis().Replace(text, "$2");
        text = InlineCode().Replace(text, "$1");
        text = BlankLines().Replace(text, "\n\n");
        return text.Trim();
    }

    [GeneratedRegex(@"\[([^\]\n]+)\]\((?:https?://|www\.)[^)\s]*\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s*", RegexOptions.Multiline)]
    private static partial Regex Heading();

    [GeneratedRegex(@"(\*\*|__)(.+?)\1")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"`([^`\n]+)`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();
}
