using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;

namespace Aiso.Infrastructure.Recommendations;

/// <summary>What the kit generator knows about the channel.</summary>
/// <param name="Questions">Active test prompts in the channel's primary language: the questions customers ask.</param>
public sealed record KitContext(string BrandName, string BaseUrl, string LanguageCode, IReadOnlyList<string> Questions);

/// <summary>One ready-to-use artefact for applying a suggestion in the system that owns the change.</summary>
/// <param name="Destination">Where it goes, e.g. "Web CMS · page head" or "App Store Connect".</param>
public sealed record KitItem(string Label, string Destination, string FileName, string Content, string HowTo);

/// <summary>
/// Builds hand-off material for a suggestion: JSON-LD, head tags, robots.txt lines, article briefs.
/// AISO never changes the target itself (targets are black boxes); the owning team pastes or uploads these.
/// Text in [square brackets] is a placeholder the team fills in.
/// </summary>
public static class ApplyKit
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // Keeps Turkish and other letters readable while still escaping < > & so the JSON is safe inside a script tag.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    // Escapes markup characters only; Turkish letters stay as they are instead of becoming &#xFC; entities.
    private static readonly HtmlEncoder Html = HtmlEncoder.Create(UnicodeRanges.All);

    // Crawler user agents the engines document for search-time fetching. Gemini grounds on Google Search, so it is Googlebot.
    private static readonly Dictionary<string, string> CrawlerByEngine = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ChatGPT"] = "OAI-SearchBot",
        ["Gemini"] = "Googlebot",
        ["Perplexity"] = "PerplexityBot",
        ["Claude"] = "Claude-SearchBot",
    };

    public static IReadOnlyList<KitItem> For(Suggestion suggestion, KitContext ctx)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        ArgumentNullException.ThrowIfNull(ctx);
        if (suggestion.Key.StartsWith("prompt:", StringComparison.Ordinal))
        {
            return PromptKit(suggestion, ctx);
        }

        var lang = ctx.LanguageCode.Split('-')[0];
        // "signal:X" (the channel's page) and "site:X" (across the crawled site) share the same fix for check X;
        // App Store keys map onto the closest storefront kit.
        var colon = suggestion.Key.IndexOf(':', StringComparison.Ordinal);
        var check = suggestion.Key[(colon + 1)..] switch
        {
            "rating-gap" or "rating-count" or "recent-reviews" => "StorefrontRating",
            "link" => "StorefrontStructuredData",
            var other => other,
        };
        return check switch
        {
            "OrganizationSchema" => [ScriptKit("Organization JSON-LD", "Web CMS · site-wide head", "organization.jsonld.html", OrganizationJsonLd(ctx),
                "Add once to the site template's <head>. Fill in the logo URL and the sameAs profiles, then validate with validator.schema.org.")],
            "FaqSchema" => [
                new KitItem("FAQ section (visible HTML)", "Web CMS · page body", "faq-section.html", FaqHtml(ctx.Questions),
                    "Place on the page that should answer these questions. Write each answer in 2–3 plain sentences."),
                ScriptKit("FAQPage JSON-LD", "Web CMS · page head", "faq.jsonld.html", FaqJsonLd(ctx.Questions),
                    "Copy the same answers as the visible section; the markup must match what visitors see."),
            ],
            "Title" or "MetaDescription" or "OpenGraph" or "Canonical" or "HtmlLang" =>
                [new KitItem("Head tags", "Web CMS · page head", "head-tags.html", HeadTags(ctx, lang),
                    "Most CMSs have SEO fields for title and description; use those, and the template for the rest.")],
            "Hreflang" => [new KitItem("hreflang links", "Web CMS · site template", "hreflang.html",
                $"<link rel=\"alternate\" hreflang=\"{lang}\" href=\"{ctx.BaseUrl}\" />\n" +
                "<link rel=\"alternate\" hreflang=\"en\" href=\"[English version URL]\" />\n" +
                $"<link rel=\"alternate\" hreflang=\"x-default\" href=\"{ctx.BaseUrl}\" />\n",
                "Every language version lists all versions, itself included.")],
            "Indexable" => [new KitItem("Robots meta tag", "Web CMS · page head", "robots-meta.html",
                "<!-- Remove: <meta name=\"robots\" content=\"noindex\"> -->\n<meta name=\"robots\" content=\"index, follow\" />\n",
                "Also check the X-Robots-Tag response header and the CMS page setting \"hide from search engines\".")],
            "SingleH1" or "HeadingHierarchy" or "ContentDepth" => [ArticleBrief(ctx, ctx.Questions, "Page content brief")],
            "StorefrontRating" => [new KitItem("In-app rating prompt", "Mobile app · iOS release", "rating-prompt.md", RatingPromptNote(),
                "Hand to the mobile team for the next release.")],
            "StorefrontStructuredData" => [new KitItem("Listing check", "AISO · Test Prompts / target settings", "listing-check.md",
                $"- Open {ctx.BaseUrl} in a browser. Does it show the live app listing?\n" +
                "- If not, replace the target URL with the real listing (apps.apple.com/[country]/app/[name]/id[number]).\n" +
                "- Check the app is available in that country's App Store.\n",
                "This is a configuration fix in AISO or App Store Connect availability, not a page change.")],
            "viewport" => [new KitItem("Viewport meta tag", "Web CMS · site template head", "viewport.html",
                "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />\n",
                "Add once to the shared page template, so every page gets it. Then check a few pages with Google's Mobile-Friendly view in Search Console.")],
            "no-sitemap" or "sitemap-not-declared" or "no-robots" => SitemapKit(ctx, check),
            "breadcrumbs" => [ScriptKit("BreadcrumbList JSON-LD", "Web CMS · inner page template", "breadcrumbs.jsonld.html", BreadcrumbJsonLd(ctx),
                "Generate it from the page's place in the menu, matching the visible breadcrumb trail. Validate with Google's Rich Results Test.")],
            "duplicate-title" or "duplicate-description" => [new KitItem("Title and description pattern", "Web CMS · SEO fields per page", "title-pattern.md",
                "# One title and description per page\n\n" +
                $"- **Title:** [the page's own topic] – [product group] | {ctx.BrandName} (10–70 characters, topic first)\n" +
                $"- **Description:** [what this page offers and for whom, with one concrete fact] (50–170 characters)\n" +
                "- Fill the SEO fields of every page in the CMS; don't let pages inherit the site's default title or description.\n" +
                "- Pages that are true duplicates: keep one and point the others' canonical to it.\n",
                "Hand to the content or SEO team with the example URLs from the suggestion.")],
            "topic-gap" => [ArticleBrief(ctx, QuotedQuestions(suggestion.Evidence), "Landing page brief")],
            "slow" or "heavy-html" => [new KitItem("Page speed checklist", "Web team · performance", "page-speed.md",
                "# Faster pages\n\n" +
                "- Measure the example URLs with PageSpeed Insights (mobile) and note LCP, INP and CLS.\n" +
                "- Cache the HTML at the CDN; aim for a server response under 0.8 s.\n" +
                "- Move large inline scripts, JSON and SVG out of the HTML into cached files.\n" +
                "- Compress images (WebP/AVIF), size them for the screen and lazy-load those below the fold.\n" +
                "- Defer scripts that aren't needed for the first view, e.g. chat widgets and tag managers.\n",
                "Hand to the web team. AISO re-measures on the next audit.")],
            _ => [],
        };
    }

    private static List<KitItem> SitemapKit(KitContext ctx, string check)
    {
        var origin = Uri.TryCreate(ctx.BaseUrl, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) : ctx.BaseUrl.TrimEnd('/');
        var kit = new List<KitItem>
        {
            new("robots.txt", "Web server · /robots.txt", "robots.txt",
                $"User-agent: *\nAllow: /\n\nSitemap: {origin}/sitemap.xml\n",
                check == "no-robots" ? "Publish at the site root. Add Disallow lines only for areas that must not be crawled." : "Add the Sitemap: line to the existing robots.txt; keep its other rules."),
        };
        if (check == "no-sitemap")
        {
            kit.Add(new KitItem("sitemap.xml", "Web server · /sitemap.xml", "sitemap.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n" +
                $"  <url><loc>{origin}/</loc><lastmod>[YYYY-MM-DD]</lastmod></url>\n" +
                $"  <url><loc>{origin}/[page]</loc><lastmod>[YYYY-MM-DD]</lastmod></url>\n</urlset>\n",
                "Better generated by the CMS than kept by hand: list every indexable page, and no noindex or redirecting URLs. Submit it in Google Search Console and Bing Webmaster Tools."));
        }

        return kit;
    }

    private static string BreadcrumbJsonLd(KitContext ctx) => new JsonObject
    {
        ["@context"] = "https://schema.org",
        ["@type"] = "BreadcrumbList",
        ["itemListElement"] = new JsonArray(
            Crumb(1, ctx.BrandName, ctx.BaseUrl),
            Crumb(2, "[section, e.g. Kartlar]", "[section URL]"),
            Crumb(3, "[this page's name]", "[this page's URL]")),
    }.ToJsonString(Json);

    private static JsonObject Crumb(int position, string name, string url) =>
        new() { ["@type"] = "ListItem", ["position"] = position, ["name"] = name, ["item"] = url };

    // The questions a topic-gap suggestion lists in its evidence, between “ and ”.
    private static List<string> QuotedQuestions(string evidence) =>
        evidence.Split('“').Skip(1).Select(s => s.Split('”')[0]).Where(s => s.Length > 0).ToList();

    private static List<KitItem> PromptKit(Suggestion s, KitContext ctx)
    {
        var question = s.Title[(s.Title.IndexOf('“', StringComparison.Ordinal) + 1)..].TrimEnd('”');
        var kit = new List<KitItem>
        {
            ArticleBrief(ctx, [question], "Article brief"),
            ScriptKit("FAQPage JSON-LD for this question", "Web CMS · page head", "faq.jsonld.html", FaqJsonLd([question]),
                "Add to the page that answers the question, with the same answer text as the page."),
        };

        var crawlers = s.Engines.Where(CrawlerByEngine.ContainsKey).Select(e => CrawlerByEngine[e]).Distinct().ToList();
        if (crawlers.Count > 0)
        {
            var robots = new StringBuilder("# Let AI search crawlers read the answer pages\n");
            foreach (var bot in crawlers)
            {
                robots.Append("User-agent: ").Append(bot).Append("\nAllow: /\n\n");
            }

            kit.Add(new KitItem("robots.txt lines", "Web server · robots.txt", "robots-ai.txt", robots.ToString(),
                "Only needed if robots.txt blocks these crawlers today. Put the lines above any broader Disallow rules."));
        }

        return kit;
    }

    private static KitItem ScriptKit(string label, string destination, string fileName, string json, string howTo) =>
        new(label, destination, fileName, $"<script type=\"application/ld+json\">\n{json}\n</script>\n", howTo);

    private static string OrganizationJsonLd(KitContext ctx) => new JsonObject
    {
        ["@context"] = "https://schema.org",
        ["@type"] = "BankOrCreditUnion",
        ["name"] = ctx.BrandName,
        ["url"] = ctx.BaseUrl,
        ["logo"] = "[logo URL, at least 112×112 px]",
        ["sameAs"] = new JsonArray("[Wikipedia URL]", "[Wikidata URL]", "[LinkedIn URL]", "[X / Instagram URL]"),
    }.ToJsonString(Json);

    private static string FaqJsonLd(IReadOnlyList<string> questions) => new JsonObject
    {
        ["@context"] = "https://schema.org",
        ["@type"] = "FAQPage",
        ["mainEntity"] = new JsonArray(Placeholders(questions).Select(q => (JsonNode)new JsonObject
        {
            ["@type"] = "Question",
            ["name"] = q,
            ["acceptedAnswer"] = new JsonObject { ["@type"] = "Answer", ["text"] = "[answer in 2–3 sentences]" },
        }).ToArray()),
    }.ToJsonString(Json);

    private static string FaqHtml(IReadOnlyList<string> questions)
    {
        var html = new StringBuilder("<section class=\"faq\">\n  <h2>[Frequently asked questions]</h2>\n");
        foreach (var q in Placeholders(questions))
        {
            html.Append("  <h3>").Append(Html.Encode(q)).Append("</h3>\n  <p>[answer in 2–3 sentences]</p>\n");
        }

        return html.Append("</section>\n").ToString();
    }

    private static string HeadTags(KitContext ctx, string lang) =>
        $"<html lang=\"{lang}\">\n" +
        $"<title>{Html.Encode(ctx.BrandName)} – [main product or topic, 10–70 characters]</title>\n" +
        "<meta name=\"description\" content=\"[what the page offers, with the brand, 50–170 characters]\" />\n" +
        $"<link rel=\"canonical\" href=\"{ctx.BaseUrl}\" />\n" +
        "<meta property=\"og:title\" content=\"[same as title]\" />\n" +
        "<meta property=\"og:description\" content=\"[same as description]\" />\n" +
        "<meta property=\"og:image\" content=\"[share image URL, 1200×630]\" />\n";

    private static KitItem ArticleBrief(KitContext ctx, IReadOnlyList<string> questions, string label)
    {
        var md = new StringBuilder();
        md.Append("# ").Append(label).Append(": ").Append(ctx.BrandName).Append("\n\n");
        md.Append("- **Site:** ").Append(ctx.BaseUrl).Append('\n');
        md.Append("- **Language:** ").Append(ctx.LanguageCode).Append('\n');
        md.Append("- **Goal:** be the source AI assistants quote when customers ask the questions below.\n\n");
        md.Append("## Structure\n\n");
        md.Append("1. One H1 that restates the main question in the customer's words.\n");
        md.Append("2. First paragraph: the direct answer in 2–3 sentences, naming ").Append(ctx.BrandName).Append(".\n");
        md.Append("3. One H2 per question below, each answered in short paragraphs or a list.\n");
        md.Append("4. Concrete facts engines can quote: fees, rates, limits, eligibility, dates. Keep them current.\n");
        md.Append("5. A visible \"last updated\" date, and links from the main navigation and related product pages.\n");
        md.Append("6. Server-rendered text (not only loaded by JavaScript), at least 300 words.\n\n");
        md.Append("## Questions to answer\n\n");
        foreach (var q in Placeholders(questions))
        {
            md.Append("### ").Append(q).Append("\n\n[answer]\n\n");
        }

        md.Append("## Before publishing\n\n- Add the FAQPage JSON-LD from this kit.\n- Run \"Run audit now\" in AISO after publishing; the suggestion shows as live once the page is seen.\n");
        return new KitItem(label, "Web CMS · new or existing page", "article-brief.md", md.ToString(),
            "Hand to the content team. AISO checks the result on the next audit.");
    }

    private static string RatingPromptNote() =>
        "# Ask happy users for an App Store rating\n\n" +
        "- Use Apple's built-in prompt (StoreKit `requestReview`, or `@Environment(\\.requestReview)` in SwiftUI).\n" +
        "- Trigger it after a success moment (a completed transfer or payment), never on launch or after an error.\n" +
        "- iOS shows the prompt at most 3 times a year per user; Apple decides when it actually appears.\n" +
        "- Reply to recent negative reviews in App Store Connect → Ratings and Reviews.\n";

    private static IEnumerable<string> Placeholders(IReadOnlyList<string> questions) =>
        questions.Count > 0 ? questions : ["[customer question 1]", "[customer question 2]"];
}
