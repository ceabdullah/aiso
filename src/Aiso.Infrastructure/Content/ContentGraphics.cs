using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Aiso.Domain.Enums;

namespace Aiso.Infrastructure.Content;

/// <summary>What a graphic is made from: the item and its draft, the brand and the site's address.</summary>
public sealed record GraphicSource(ContentKind Kind, string Title, string Question, string? Draft, string Brand, string Host);

/// <summary>Brand colours: a dark background (text on it is white) and an accent.</summary>
public sealed partial record GraphicStyle(string Background, string Accent)
{
    public static readonly GraphicStyle Default = new("#14213D", "#1FA67A");

    /// <summary>The given colours when they are #RRGGBB, else the defaults.</summary>
    public static GraphicStyle From(string? background, string? accent) =>
        new(IsHex(background) ? background!.Trim() : Default.Background, IsHex(accent) ? accent!.Trim() : Default.Accent);

    private static bool IsHex(string? value) => value is not null && HexColor().IsMatch(value.Trim());

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();
}

/// <summary>One finished graphic as SVG; the dashboard turns it into PNG in the browser.</summary>
public sealed record Graphic(string Label, string FileName, int Width, int Height, string Svg, string HowTo);

/// <summary>
/// Branded graphics made from templates and the draft's own text: a share image (og:image) for web pages, a post image
/// and an FAQ carousel for LinkedIn, and caption frames for App Store screenshots. No AI and no invented visuals, so
/// nothing to license: designers can use them as they are or as a starting point.
/// </summary>
public static partial class ContentGraphics
{
    private const string Font = "'Segoe UI', 'Helvetica Neue', Helvetica, Arial, sans-serif";
    private const int MaxSlides = 5;

    public static IReadOnlyList<Graphic> For(GraphicSource source, GraphicStyle style)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(style);
        if (string.IsNullOrWhiteSpace(source.Draft) || source.Kind == ContentKind.ImageAltText)
        {
            return [];
        }

        var draft = DraftParts.Parse(source.Draft);
        return source.Kind switch
        {
            ContentKind.SocialPost =>
            [
                new("Post image", "post-image.svg", 1200, 627, Banner(1200, 627, source, style, draft.Hook ?? source.Question, $"{source.Host}"),
                    "Attach to the LinkedIn post (1200×627)."),
                .. Carousel(source, style, draft),
            ],
            ContentKind.AppStoreText => AppFrames(source, style, draft),
            _ =>
            [
                new("Share image (og:image)", "share-image.svg", 1200, 630,
                    Banner(1200, 630, source, style, draft.H1 ?? source.Question, draft.MetaDescription ?? draft.Lead),
                    "Upload to the CMS and set it as the page's og:image (1200×630). It also fixes the Open Graph check."),
                .. Carousel(source, style, draft),
            ],
        };
    }

    // Title, subtitle and site on the brand background, with an accent bar and a quiet ring motif.
    private static string Banner(int w, int h, GraphicSource s, GraphicStyle style, string title, string? subtitle)
    {
        var svg = Open(w, h, style);
        svg.Append(Rect(0, 0, 14, h, style.Accent));
        svg.Append(Ring(w - 120, h / 2, 260, style.Accent, 0.18)).Append(Ring(w - 40, h - 60, 140, style.Accent, 0.12));
        svg.Append(Wordmark(80, 92, s.Brand, 30, style));
        var y = Lines(svg, title, 80, 200, 56, 700, "#FFFFFF", 1.0, w - 260, 3);
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            Lines(svg, subtitle, 80, y + 30, 28, 400, "#FFFFFF", 0.82, w - 260, 2);
        }

        svg.Append(Text(80, h - 60, s.Host, 24, 600, style.Accent, 1));
        return svg.Append("</svg>").ToString();
    }

    // Cover, one slide per FAQ answer (or section), and a closing slide: square 1080×1080 for LinkedIn documents.
    private static List<Graphic> Carousel(GraphicSource s, GraphicStyle style, DraftParts draft)
    {
        var pairs = (draft.Faq.Count > 0 ? draft.Faq : draft.Sections).Take(MaxSlides).ToList();
        if (pairs.Count < 2)
        {
            return [];
        }

        var total = pairs.Count + 2;
        var slides = new List<Graphic>
        {
            Slide(1, total, "carousel-01.svg", s, style, null, draft.H1 ?? s.Question, null),
        };
        for (var i = 0; i < pairs.Count; i++)
        {
            slides.Add(Slide(i + 2, total, $"carousel-{i + 2:00}.svg", s, style, (i + 1).ToString(CultureInfo.InvariantCulture), pairs[i].Question, pairs[i].Answer));
        }

        slides.Add(Slide(total, total, $"carousel-{total:00}.svg", s, style, null, s.Brand, s.Host));
        return slides;
    }

    private static Graphic Slide(int n, int total, string file, GraphicSource s, GraphicStyle style, string? number, string heading, string? body)
    {
        const int size = 1080;
        var svg = Open(size, size, style);
        svg.Append(Rect(0, size - 16, size * n / total, 16, style.Accent));
        svg.Append(Wordmark(80, 110, s.Brand, 30, style));
        svg.Append(Text(size - 80, 110, string.Create(CultureInfo.InvariantCulture, $"{n}/{total}"), 28, 600, "#FFFFFF", 0.6, "end"));
        var y = 300;
        if (number is not null)
        {
            svg.Append(Text(80, 290, number, 120, 800, style.Accent, 1));
            y = 400;
        }
        else
        {
            svg.Append(Ring(size - 140, size - 200, 220, style.Accent, 0.16));
        }

        y = Lines(svg, heading, 80, y, number is null ? 72 : 52, 700, "#FFFFFF", 1, size - 160, number is null ? 5 : 4);
        if (!string.IsNullOrWhiteSpace(body))
        {
            Lines(svg, body, 80, y + 36, number is null ? 36 : 34, 400, "#FFFFFF", 0.85, size - 160, 8);
        }

        return new($"Carousel {n}/{total}", file, size, size, svg.Append("</svg>").ToString(),
            "Upload the slides in order as a LinkedIn document (PDF) or an image carousel.");
    }

    // 6.7" iPhone screenshot frames: a caption on top and a placeholder where the app screen goes.
    private static List<Graphic> AppFrames(GraphicSource s, GraphicStyle style, DraftParts draft)
    {
        var captions = draft.Captions.Take(3).ToList();
        var frames = new List<Graphic>();
        for (var i = 0; i < captions.Count; i++)
        {
            const int w = 1290, h = 2796;
            var svg = Open(w, h, style);
            svg.Append(Ring(w - 100, 320, 420, style.Accent, 0.14));
            svg.Append(Wordmark(120, 200, s.Brand, 48, style));
            Lines(svg, captions[i], 120, 420, 96, 800, "#FFFFFF", 1, w - 240, 3);
            svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"195\" y=\"880\" width=\"900\" height=\"1800\" rx=\"90\" fill=\"#FFFFFF\" fill-opacity=\"0.06\" stroke=\"{style.Accent}\" stroke-width=\"6\"/>");
            svg.Append(Text(w / 2, 1800, "[app screenshot]", 48, 400, "#FFFFFF", 0.5, "middle"));
            frames.Add(new($"App Store frame {i + 1}", $"app-frame-{i + 1}.svg", w, h, svg.Append("</svg>").ToString(),
                "Place a real app screenshot in the frame, then upload in App Store Connect (6.7\" display, 1290×2796)."));
        }

        return frames;
    }

    private static StringBuilder Open(int w, int h, GraphicStyle style) => new StringBuilder()
        .Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{w}\" height=\"{h}\" viewBox=\"0 0 {w} {h}\">")
        .Append(CultureInfo.InvariantCulture, $"<rect width=\"{w}\" height=\"{h}\" fill=\"{style.Background}\"/>");

    private static string Rect(int x, int y, int w, int h, string fill) =>
        string.Create(CultureInfo.InvariantCulture, $"<rect x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" fill=\"{fill}\"/>");

    private static string Ring(int cx, int cy, int r, string color, double opacity) =>
        string.Create(CultureInfo.InvariantCulture, $"<circle cx=\"{cx}\" cy=\"{cy}\" r=\"{r}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"40\" stroke-opacity=\"{opacity}\"/>");

    private static string Wordmark(int x, int y, string brand, int size, GraphicStyle style) =>
        string.Create(CultureInfo.InvariantCulture, $"<circle cx=\"{x + size / 3}\" cy=\"{y - size / 3}\" r=\"{size / 3}\" fill=\"{style.Accent}\"/>")
        + Text(x + size, y, brand, size, 800, "#FFFFFF", 1);

    private static string Text(int x, int y, string text, int size, int weight, string color, double opacity, string anchor = "start") =>
        string.Create(CultureInfo.InvariantCulture,
            $"<text x=\"{x}\" y=\"{y}\" font-family=\"{Font}\" font-size=\"{size}\" font-weight=\"{weight}\" fill=\"{color}\" fill-opacity=\"{opacity}\" text-anchor=\"{anchor}\">{Escape(text)}</text>");

    // Wraps text to the width (an estimate from average glyph widths) and returns the y below the last line.
    private static int Lines(StringBuilder svg, string text, int x, int y, int size, int weight, string color, double opacity, int width, int maxLines)
    {
        var lines = Wrap(text, Math.Max(8, (int)(width / (size * (weight >= 700 ? 0.58 : 0.52)))), maxLines);
        var lineHeight = (int)(size * 1.25);
        foreach (var line in lines)
        {
            svg.Append(Text(x, y, line, size, weight, color, opacity));
            y += lineHeight;
        }

        return y - lineHeight + size / 2;
    }

    /// <summary>Greedy word wrap to <paramref name="maxChars"/> per line; the last line ends with … when text is cut.</summary>
    public static IReadOnlyList<string> Wrap(string text, int maxChars, int maxLines)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<string>();
        var current = new StringBuilder();
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var cut = false;
        foreach (var word in words)
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > maxChars)
            {
                lines.Add(current.ToString());
                current.Clear();
                if (lines.Count == maxLines)
                {
                    cut = true;
                    break;
                }
            }

            current.Append(current.Length > 0 ? " " : string.Empty).Append(word.Length > maxChars ? word[..maxChars] : word);
        }

        if (!cut && current.Length > 0)
        {
            lines.Add(current.ToString());
        }

        if (cut && lines.Count > 0)
        {
            var last = lines[^1];
            lines[^1] = (last.Length > maxChars - 1 ? last[..(maxChars - 1)] : last).TrimEnd(',', '.', ';', ':', ' ') + "…";
        }

        return lines;
    }

    private static string Escape(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);
}

/// <summary>The parts of a Markdown draft that graphics use.</summary>
public sealed partial record DraftParts(
    string? H1,
    string? Lead,
    string? MetaDescription,
    string? Hook,
    IReadOnlyList<(string Question, string Answer)> Faq,
    IReadOnlyList<(string Question, string Answer)> Sections,
    IReadOnlyList<string> Captions)
{
    public static DraftParts Parse(string draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var lines = draft.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n').Select(l => l.Trim()).ToList();
        string? Meta(string name) => lines.Where(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
            .Select(l => Plain(l[(name.Length + 1)..])).FirstOrDefault(v => v.Length > 0);
        bool IsText(string l) => l.Length > 0 && !l.StartsWith('#') && l != "---" && !l.StartsWith("Meta ", StringComparison.OrdinalIgnoreCase);

        var h1 = lines.Where(l => l.StartsWith("# ", StringComparison.Ordinal)).Select(l => Plain(l[2..])).FirstOrDefault();
        var start = h1 is null ? 0 : lines.FindIndex(l => l.StartsWith("# ", StringComparison.Ordinal)) + 1;
        var lead = lines.Skip(start).Where(IsText).Select(Plain).FirstOrDefault(l => l.Length > 0);
        var hook = lines.Where(IsText).Select(Plain).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));

        // FAQ: "### Question?" or "**Question?**" followed by its answer.
        var faq = new List<(string, string)>();
        for (var i = 0; i < lines.Count - 1; i++)
        {
            var q = lines[i].StartsWith("### ", StringComparison.Ordinal) ? Plain(lines[i][4..])
                : BoldLine().Match(lines[i]) is { Success: true } m ? Plain(m.Groups[1].Value) : null;
            if (q is not null && q.EndsWith('?') && NextText(lines, i + 1) is { } a)
            {
                faq.Add((q, a));
            }
        }

        var sections = new List<(string, string)>();
        for (var i = 0; i < lines.Count - 1; i++)
        {
            if (lines[i].StartsWith("## ", StringComparison.Ordinal) && NextText(lines, i + 1) is { } a)
            {
                sections.Add((Plain(lines[i][3..]), a));
            }
        }

        // App Store: list items, or else the description's sentences.
        var captions = lines.Where(l => ListItem().IsMatch(l)).Select(l => Plain(ListItem().Replace(l, string.Empty)))
            .Where(l => l.Length is > 8 and <= 90).ToList();
        if (captions.Count == 0)
        {
            captions = Sentence().Split(string.Join(' ', lines.Where(IsText).Select(Plain)))
                .Select(x => x.Trim()).Where(x => x.Length is > 8 and <= 90).Take(3).ToList();
        }

        return new(h1, lead, Meta("Meta description"), hook, faq, sections, captions);
    }

    private static string? NextText(List<string> lines, int from)
    {
        for (var i = from; i < lines.Count; i++)
        {
            if (lines[i].Length == 0)
            {
                continue;
            }

            return lines[i].StartsWith('#') || BoldLine().IsMatch(lines[i]) ? null : Plain(lines[i]);
        }

        return null;
    }

    /// <summary>Markdown emphasis, links and list marks removed.</summary>
    public static string Plain(string text) =>
        Link().Replace(Emphasis().Replace(text ?? string.Empty, string.Empty), "$1").Trim().TrimStart('-', '•', '*', ' ').Trim();

    [GeneratedRegex(@"^\*\*(.+?)\*\*$")]
    private static partial Regex BoldLine();

    [GeneratedRegex(@"\*\*|__|`")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"^([-•*]|\d+[.)])\s+")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex Sentence();
}
