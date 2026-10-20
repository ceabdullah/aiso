using System.Globalization;
using Aiso.Domain.Entities;

namespace Aiso.Worker.Pipeline;

/// <summary>Builds the instruction piped to <c>claude.exe</c>. The output contract is mirrored in CLAUDE.md.</summary>
public static class AgentPromptBuilder
{
    public static string Build(TargetProfile target, string runFolderRelative, string languageCode)
    {
        ArgumentNullException.ThrowIfNull(target);
        return string.Create(CultureInfo.InvariantCulture, $$"""
            You are the AISO remediation analyst for the target "{{target.DisplayName}}" ({{target.BaseUrl}}), channel type {{target.ChannelKind}}.
            Follow the workspace rules in CLAUDE.md.

            Analyze the scraped results in {{runFolderRelative}}/ :
              - {{target.Code}}.scrape.json  : on-page structural signals with a weighted score breakdown ("signals")
              - {{target.Code}}.prompts.json : answers that AI answer engines (ChatGPT, Gemini, Perplexity, Claude)
                gave to customer questions: whether the brand was mentioned, and which web sources each engine cited
              - {{target.Code}}.brand.json (if present): which banks the engines named ("shareOfVoice") and which kinds of
                sources they cited ("sources": own sites, competitor banks, comparison sites, news, complaints, Wikipedia, …)
              - {{target.Code}}.site.json (if present): a crawl of the brand's own site; "checks" says how many pages pass each signal,
                "seo" is a technical SEO audit (health 0-100; "issues": duplicate titles, missing viewport, sitemap and robots.txt
                problems, redirects, slow pages, ...) and each page's "topics" are its title and headings
              - {{target.Code}}.competitors.json (if present): the pages the engines cited instead of the brand, checked with the
                same signals; compare them with the brand's pages and say concretely what they have that the brand lacks
              - {{target.Code}}.app.json (if present): public App Store data (rating, ratings count, last update, recent reviews)
                and leading peer apps for comparison
            Earlier runs may exist under data/ for trend comparison; read them only if useful.

            {{BrandGuidance(target)}}
            Write every human-readable string in the language "{{languageCode}}".
            Recommend changes the target owners can make to raise visibility in AI/generative search answers
            (structured data, answer-ready content, crawlability, technical SEO, app-store listing quality, brand/entity clarity).
            AI engines search Google's and Bing's indexes, so technical SEO problems that stop pages ranking also stop them being cited;
            give those to the SEO owner, and name the customer questions no page targets.
            Sources the engines cited instead of the brand's own domains show which pages and content formats they trust; say what
            the brand would need to publish to be cited for those questions. Call out engine-specific gaps when one engine lags the rest.
            The target is a black box: never suggest reading or changing its source code or databases.

            Respond with ONLY one JSON object (no Markdown fence, no prose) of this exact shape:
            {"summary":"<2-4 sentence executive summary>","items":[{"title":"<imperative action>","detail":"<why + how, max 3 sentences>","priority":"critical|high|medium|low","category":"structured-data|content|technical|app-store|brand-entity","owner":"<team, e.g. Web, SEO, Digital Marketing, Mobile>"}]}
            Return between 3 and 12 items, most impactful first. Do not create or modify any files.
            """);
    }

    private static string BrandGuidance(TargetProfile target) => target.ChannelKind == Domain.Enums.TargetChannelKind.Brand
        ? "This is a brand-level channel with no single website: the questions are about the bank itself. Assess how the engines " +
          "portray it (tone, facts and claims, strengths and weaknesses they repeat, competitors they prefer and why) and which " +
          "sources shape that picture. Recommend brand-entity, PR, content and reputation actions; flag claims that look outdated or wrong."
        : "When competitor banks are named more often than the brand, say which ones and what the answers credit them for.";
}
