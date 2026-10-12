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
              - {{target.Code}}.prompts.json : answers that AI answer engines (ChatGPT, Gemini, Perplexity, Claude, the internal model)
                gave to customer questions: whether the brand was mentioned, and which web sources each engine cited
            Earlier runs may exist under data/ for trend comparison; read them only if useful.

            Write every human-readable string in the language "{{languageCode}}".
            Recommend changes the target owners can make to raise visibility in AI/generative search answers
            (structured data, answer-ready content, crawlability, app-store listing quality, brand/entity clarity).
            Sources the engines cited instead of the brand's own domains show which pages and content formats they trust; say what
            the brand would need to publish to be cited for those questions. Call out engine-specific gaps when one engine lags the rest.
            The target is a black box: never suggest reading or changing its source code or databases.

            Respond with ONLY one JSON object (no Markdown fence, no prose) of this exact shape:
            {"summary":"<2-4 sentence executive summary>","items":[{"title":"<imperative action>","detail":"<why + how, max 3 sentences>","priority":"critical|high|medium|low","category":"structured-data|content|technical|app-store|brand-entity","owner":"<team, e.g. Web, Digital Marketing, Mobile>"}]}
            Return between 3 and 12 items, most impactful first. Do not create or modify any files.
            """);
    }
}
