using System.Globalization;
using Aiso.Infrastructure.Scraping;

namespace Aiso.Infrastructure.Recommendations;

/// <summary>Where a suggestion stands, judged from later audits only (never from the internal LLM).</summary>
public enum SuggestionStatus
{
    /// <summary>The latest audit still shows the problem.</summary>
    ToDo,

    /// <summary>The page change is live, but AI answers haven't improved since.</summary>
    LiveAwaitingEngines,

    /// <summary>The change is live and AI answers improved after it, or the engines now show the brand.</summary>
    Confirmed,
}

public enum SuggestionPriority { Critical, High, Medium, Low }

/// <summary>One engine's answer to one test prompt in one audit, reduced to what the advisor needs.</summary>
/// <param name="CitedHosts">Hosts the engine cited that the brand does not own.</param>
public sealed record ProbeSnapshot(
    Guid PromptId,
    string PromptText,
    string LanguageCode,
    string EngineKey,
    string EngineName,
    bool MentionsBrand,
    bool CitesOwnedDomain,
    IReadOnlyList<string> CitedHosts)
{
    public bool IsVisible => MentionsBrand || CitesOwnedDomain;
}

/// <summary>One audit of one channel: the page signals and the engine answers.</summary>
public sealed record AuditSnapshot(DateTime AtUtc, AuditMetricsDocument? Page, IReadOnlyList<ProbeSnapshot> Probes);

public sealed record EngineTip(string Engine, string Tip);

public sealed record Suggestion(
    string Key,
    string Title,
    string Detail,
    SuggestionPriority Priority,
    string Category,
    IReadOnlyList<string> Engines,
    string Evidence,
    SuggestionStatus Status,
    string StatusDetail,
    IReadOnlyList<EngineTip> EngineTips)
{
    /// <summary>The team that ships this kind of change (same owners as the remediation plans).</summary>
    public string Owner => Category switch
    {
        "app-store" => "Mobile",
        "content" => "Digital Marketing",
        _ => "Web",
    };
}

/// <summary>
/// Turns a channel's audit history into concrete content suggestions, and checks earlier suggestions
/// against later audits: a page fix counts as live once the scraper sees it, and as confirmed once the
/// real answer engines show the brand more often afterwards. Deterministic and offline, so it also works
/// where the internal LLM gateway isn't reachable.
/// </summary>
public static class VisibilityAdvisor
{
    private sealed record SignalAdvice(string Title, string Detail, string Category);

    private static readonly Dictionary<string, SignalAdvice> SignalAdvices = new(StringComparer.Ordinal)
    {
        ["Indexable"] = new("Remove the noindex directive",
            "AI answer engines ground their answers on search indexes (Gemini on Google, ChatGPT largely on Bing). A page that tells search engines not to index it can't be found or cited.",
            "technical"),
        ["Title"] = new("Write a 10–70 character title that names the brand and the product",
            "Engines and search results show the title as the source label. A clear title like \"TEB Kredi Kartları – Aidatsız Kart Seçenekleri\" tells them what the page answers.",
            "content"),
        ["MetaDescription"] = new("Add a 50–170 character meta description that answers what the page offers",
            "Search snippets are often what an engine reads first when it picks sources. State the offer and the brand in one sentence.",
            "content"),
        ["HtmlLang"] = new("Declare the page language on the html element",
            "Set lang=\"tr\" (or the page's language) so engines match the page to questions asked in that language.",
            "technical"),
        ["SingleH1"] = new("Use exactly one H1 that states the page's topic",
            "Engines use the H1 to decide what a page is about. Several or no H1s blur that.",
            "content"),
        ["HeadingHierarchy"] = new("Break the content into H2 sections phrased like customer questions",
            "Engines lift answers passage by passage. Sections headed \"Which card has no annual fee?\" map straight onto what customers ask.",
            "content"),
        ["Canonical"] = new("Add a canonical URL",
            "A canonical link merges duplicate URLs, so citations and ranking signals add up on one page.",
            "technical"),
        ["OpenGraph"] = new("Add Open Graph tags (og:title, og:description, og:image)",
            "Some engines and link previews read Open Graph tags to describe a page.",
            "technical"),
        ["ImageAltText"] = new("Describe images with alt text",
            "Alt text is the only way text-based crawlers learn what an image (card visuals, app screens) shows.",
            "content"),
        ["OrganizationSchema"] = new("Add BankOrCreditUnion JSON-LD for the brand",
            "Include name, legalName, url, logo and sameAs links (Wikipedia, Wikidata, LinkedIn, official social accounts). It ties the site to the brand entity engines already know.",
            "structured-data"),
        ["FaqSchema"] = new("Add a visible FAQ section with FAQPage JSON-LD",
            "Answer the questions customers ask (see Test Prompts) in short, direct paragraphs on the page, and mark them up as FAQPage. The markup must match the visible text.",
            "structured-data"),
        ["ContentDepth"] = new("Serve at least 300 words of descriptive text in the HTML",
            "Many AI crawlers don't run JavaScript. Text that only appears after scripts run is invisible to them, so render the key content on the server.",
            "content"),
        ["Hreflang"] = new("Link the language versions with hreflang",
            "hreflang tells engines which page to cite for a Turkish versus an English question.",
            "technical"),
        ["StorefrontStructuredData"] = new("Check the App Store URL and listing",
            "The listing page didn't expose app data. Make sure the target URL points at the live listing (the real app ID) and the app is available in this storefront.",
            "app-store"),
        ["StorefrontRating"] = new("Lift the App Store rating to 4.0 or higher",
            "Engines quote store ratings when they compare banking apps. Ask satisfied users for a rating in the app at a good moment, and reply to reviews.",
            "app-store"),
    };

    // Why an engine may miss a brand, and what to check. Kept to what each vendor documents.
    private static readonly Dictionary<string, string> EngineTipsByKey = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chatgpt"] = "ChatGPT search relies on Bing's index and OpenAI's OAI-SearchBot crawler. Check the page is indexed in Bing Webmaster Tools and robots.txt doesn't block OAI-SearchBot.",
        ["gemini"] = "Gemini grounds its answers on Google Search. The page has to rank in Google for this question; check the query in Google Search Console.",
        ["perplexity"] = "Perplexity uses its own crawler, PerplexityBot, and favours recent, clearly structured pages. Allow PerplexityBot in robots.txt and keep the page's date current.",
        ["claude"] = "Claude's web search reads results through Anthropic's Claude-SearchBot crawler. Allow Claude-SearchBot in robots.txt.",
    };

    public static IReadOnlyList<Suggestion> Analyze(IReadOnlyList<AuditSnapshot> history, IReadOnlyList<string> ownedDomains)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(ownedDomains);
        var audits = history.OrderBy(a => a.AtUtc).ToList();
        if (audits.Count == 0)
        {
            return [];
        }

        var site = ownedDomains.Count > 0 ? ownedDomains[0] : "your site";
        return [.. SignalSuggestions(audits), .. PromptSuggestions(audits, site)];
    }

    private static IEnumerable<Suggestion> SignalSuggestions(List<AuditSnapshot> audits)
    {
        var withPage = audits.Where(a => a.Page is { Signals.Count: > 0 }).ToList();
        if (withPage.Count == 0)
        {
            yield break;
        }

        foreach (var signal in withPage[^1].Page!.Signals)
        {
            if (!SignalAdvices.TryGetValue(signal.Name, out var advice))
            {
                continue;
            }

            var priority = signal.Name == "Indexable" ? SuggestionPriority.Critical
                : signal.Weight >= 10 ? SuggestionPriority.High
                : signal.Weight >= 6 ? SuggestionPriority.Medium
                : SuggestionPriority.Low;
            var evidence = $"{signal.Name}: {signal.Detail}";

            if (!signal.Passed)
            {
                yield return new Suggestion($"signal:{signal.Name}", advice.Title, advice.Detail, priority, advice.Category, [],
                    evidence, SuggestionStatus.ToDo, "Still missing in the latest audit.", []);
                continue;
            }

            // Passing now: was it failing earlier in the window? Then it was fixed, and later audits show whether it paid off.
            var lastFail = withPage.FindLastIndex(a => a.Page!.Signals.Any(s => s.Name == signal.Name && !s.Passed));
            if (lastFail < 0)
            {
                continue;
            }

            var fixedAt = withPage[lastFail + 1].AtUtc;
            var before = audits.Where(a => a.AtUtc <= withPage[lastFail].AtUtc).SelectMany(a => a.Probes).ToList();
            var after = audits.Where(a => a.AtUtc >= fixedAt).SelectMany(a => a.Probes).ToList();
            var (status, detail) = JudgeFix(fixedAt, before, after);
            yield return new Suggestion($"signal:{signal.Name}", advice.Title, advice.Detail, priority, advice.Category, [],
                evidence, status, detail, []);
        }
    }

    private static (SuggestionStatus, string) JudgeFix(DateTime fixedAt, List<ProbeSnapshot> before, List<ProbeSnapshot> after)
    {
        var since = $"Live on the page since {Day(fixedAt)}.";
        if (before.Count == 0 || after.Count == 0)
        {
            return (SuggestionStatus.LiveAwaitingEngines,
                $"{since} No AI engine answers on both sides of the change yet, so its effect isn't measured. Add engine API keys and keep auditing.");
        }

        var rateBefore = Rate(before);
        var rateAfter = Rate(after);
        var change = string.Create(CultureInfo.InvariantCulture,
            $"AI answer visibility {rateBefore:0}% before, {rateAfter:0}% since ({after.Count} answers).");
        return rateAfter > rateBefore
            ? (SuggestionStatus.Confirmed, $"{since} {change}")
            : (SuggestionStatus.LiveAwaitingEngines, $"{since} {change} Engines re-crawl on their own schedule; this can take days to weeks.");
    }

    private static IEnumerable<Suggestion> PromptSuggestions(List<AuditSnapshot> audits, string site)
    {
        var byPrompt = audits
            .SelectMany(a => a.Probes.Select(p => (a.AtUtc, Probe: p)))
            .GroupBy(x => x.Probe.PromptId);

        foreach (var prompt in byPrompt)
        {
            var latestAt = prompt.Max(x => x.AtUtc);
            var latest = prompt.Where(x => x.AtUtc == latestAt).Select(x => x.Probe).ToList();
            var text = latest[0].PromptText;
            var language = LanguageName(latest[0].LanguageCode);
            var missing = latest.Where(p => !p.IsVisible).ToList();
            var showing = latest.Where(p => p.IsVisible).Select(p => p.EngineName).Distinct().Order(StringComparer.Ordinal).ToList();

            // Engines that missed this prompt in an earlier audit and show the brand now.
            var recovered = new List<(string EngineName, DateTime Since)>();
            foreach (var p in latest.Where(p => p.IsVisible))
            {
                var history = prompt.Where(x => x.Probe.EngineKey == p.EngineKey).ToList();
                if (history.Where(x => !x.Probe.IsVisible).Select(x => (DateTime?)x.AtUtc).Max() is { } lastMiss)
                {
                    recovered.Add((p.EngineName, history.Where(x => x.AtUtc > lastMiss).Min(x => x.AtUtc)));
                }
            }

            recovered.Sort((a, b) => string.CompareOrdinal(a.EngineName, b.EngineName));
            var progress = recovered.Count == 0 ? null
                : "Now shows the brand: " + string.Join(", ", recovered.Select(r => $"{r.EngineName} (since {Day(r.Since)})")) + ".";

            var evidence = string.Create(CultureInfo.InvariantCulture,
                $"Latest audit {Day(latestAt)}: {showing.Count} of {latest.Select(p => p.EngineKey).Distinct().Count()} engines name the brand or cite {site}.");

            if (missing.Count == 0)
            {
                if (progress is not null)
                {
                    yield return new Suggestion($"prompt:{prompt.Key}", $"Visible for “{text}”",
                        $"All engines asked now name the brand or cite {site} for this {language} question.",
                        SuggestionPriority.Medium, "content", recovered.Select(r => r.EngineName).ToList(),
                        evidence, SuggestionStatus.Confirmed, progress, []);
                }

                continue;
            }

            var missingNames = missing.Select(p => p.EngineName).Distinct().Order(StringComparer.Ordinal).ToList();
            var cited = missing.SelectMany(p => p.CitedHosts)
                .GroupBy(h => h, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Take(3).Select(g => g.Key).ToList();
            var citedText = cited.Count == 0 ? "" : $" They cited {string.Join(", ", cited)} instead; see what those pages cover that yours doesn't.";
            var tips = missing.Select(p => p.EngineKey).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(EngineTipsByKey.ContainsKey)
                .Select(k => new EngineTip(missing.First(p => string.Equals(p.EngineKey, k, StringComparison.OrdinalIgnoreCase)).EngineName, EngineTipsByKey[k]))
                .OrderBy(t => t.Engine, StringComparer.Ordinal)
                .ToList();

            if (showing.Count == 0)
            {
                yield return new Suggestion($"prompt:{prompt.Key}", $"Publish a page that answers “{text}”",
                    $"No engine named the brand or cited {site} for this {language} question.{citedText} " +
                    $"Answer the question in the first paragraph of a dedicated page (or an FAQ entry) on {site}, in {language}, and link it from the main navigation.",
                    SuggestionPriority.High, "content", missingNames, evidence, SuggestionStatus.ToDo,
                    progress ?? "Not visible on any engine in the latest audit.", tips);
            }
            else
            {
                yield return new Suggestion($"prompt:{prompt.Key}", $"Close the {string.Join(" and ", missingNames)} gap for “{text}”",
                    $"{string.Join(", ", showing)} already name the brand for this {language} question; {string.Join(", ", missingNames)} don't.{citedText}",
                    SuggestionPriority.Medium, "content", missingNames, evidence, SuggestionStatus.ToDo,
                    progress ?? $"Missing on {string.Join(", ", missingNames)} in the latest audit.", tips);
            }
        }
    }

    private static decimal Rate(List<ProbeSnapshot> probes) => 100m * probes.Count(p => p.IsVisible) / probes.Count;

    private static string Day(DateTime utc) => utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string LanguageName(string code)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(code);
            return (culture.IsNeutralCulture ? culture : culture.Parent).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }
}
