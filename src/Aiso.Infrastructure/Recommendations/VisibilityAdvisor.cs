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
        "content" or "brand-entity" => "Digital Marketing",
        "seo" => "SEO",
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
        var latestPage = audits.LastOrDefault(a => a.Page is not null)?.Page;
        return [.. SignalSuggestions(audits), .. SiteSuggestions(latestPage), .. SeoSuggestions(latestPage, audits[^1].Probes), .. AppSuggestions(latestPage), .. BrandSuggestions(latestPage), .. PromptSuggestions(audits, site, latestPage)];
    }

    private static IEnumerable<Suggestion> SignalSuggestions(List<AuditSnapshot> audits)
    {
        var withPage = audits.Where(a => a.Page is { Signals.Count: > 0 }).ToList();
        if (withPage.Count == 0)
        {
            yield break;
        }

        // An App Store channel's page belongs to Apple: only the listing (storefront) checks are the bank's to fix.
        var isApp = withPage[^1].Page!.App is not null || withPage[^1].Page!.Metrics?.Storefront is not null;
        foreach (var signal in withPage[^1].Page!.Signals)
        {
            if (!SignalAdvices.TryGetValue(signal.Name, out var advice) || (isApp && !signal.Name.StartsWith("Storefront", StringComparison.Ordinal)))
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
                // Pages the engines cited instead of the brand that pass this check make the gap concrete (and more urgent).
                var page = withPage[^1].Page!;
                var (comparison, competitorsPass) = CompetitorComparison(page, signal.Name);
                yield return new Suggestion($"signal:{signal.Name}", advice.Title, advice.Detail + comparison,
                    competitorsPass ? Raise(priority) : priority, advice.Category, [],
                    evidence + SiteCoverage(page, signal.Name), SuggestionStatus.ToDo, "Still missing in the latest audit.", []);
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

    /// <summary>" 4 of 5 pages the engines cited instead of yours pass this check (a.com, b.com)." and whether most do.</summary>
    private static (string Text, bool MostPass) CompetitorComparison(AuditMetricsDocument page, string signal)
    {
        var checkedPages = (page.Competitors ?? []).Where(c => c.Fetched && c.Signals.Any(s => s.Name == signal)).ToList();
        if (checkedPages.Count == 0)
        {
            return (string.Empty, false);
        }

        var passing = checkedPages.Where(c => c.Signals.Any(s => s.Name == signal && s.Passed)).ToList();
        if (passing.Count == 0)
        {
            return (string.Empty, false);
        }

        var hosts = string.Join(", ", passing.Select(c => c.Host).Distinct().Take(3));
        return (string.Create(CultureInfo.InvariantCulture,
            $" {passing.Count} of {checkedPages.Count} pages the AI engines cited instead of yours pass this check ({hosts})."),
            passing.Count * 2 >= checkedPages.Count);
    }

    /// <summary>" Across the site: 3 of 25 pages pass." when the site was crawled.</summary>
    private static string SiteCoverage(AuditMetricsDocument page, string signal) =>
        page.Site?.Checks.FirstOrDefault(c => c.Signal == signal) is { Total: > 1 } check
            ? string.Create(CultureInfo.InvariantCulture, $" · across the site: {check.Passed} of {check.Total} pages pass")
            : string.Empty;

    private static SuggestionPriority Raise(SuggestionPriority p) => p == SuggestionPriority.Critical ? p : p - 1;

    // Site-wide gaps the home page doesn't show: a check most crawled pages fail, and broken pages.
    private static IEnumerable<Suggestion> SiteSuggestions(AuditMetricsDocument? page)
    {
        if (page?.Site is not { PagesCrawled: > 1 } site)
        {
            yield break;
        }

        if (site.BrokenPages > 0)
        {
            var broken = site.Pages.Where(p => p.HttpStatus is null or >= 400).Take(3).Select(p => $"{p.Url} ({p.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "no answer"})");
            yield return new Suggestion("site:broken", string.Create(CultureInfo.InvariantCulture, $"Fix {site.BrokenPages} broken page(s) linked from the site"),
                "Crawlers that follow these links hit errors; engines don't cite pages they can't load, and broken links lower trust in the site.",
                SuggestionPriority.High, "technical", [], string.Join(" · ", broken), SuggestionStatus.ToDo, "Found in the latest crawl.", []);
        }

        var homeSignals = page.Signals.ToDictionary(s => s.Name, s => s.Passed, StringComparer.Ordinal);
        foreach (var check in site.Checks.Where(c => c.Total >= 3 && c.PassRate < 70m))
        {
            // The home page failing this check is already a suggestion of its own.
            if (!homeSignals.GetValueOrDefault(check.Signal, true) || !SignalAdvices.TryGetValue(check.Signal, out var advice))
            {
                continue;
            }

            yield return new Suggestion($"site:{check.Signal}", $"Across the site: {LowerFirst(advice.Title)}", advice.Detail,
                check.PassRate < 30m ? SuggestionPriority.High : SuggestionPriority.Medium, advice.Category, [],
                string.Create(CultureInfo.InvariantCulture, $"{check.Signal}: {check.Passed} of {check.Total} crawled pages pass. Failing: {string.Join(", ", check.FailingExamples)}"),
                SuggestionStatus.ToDo, "Found in the latest crawl.", []);
        }
    }

    // Technical SEO across the crawled site, and customer questions no page targets.
    private static IEnumerable<Suggestion> SeoSuggestions(AuditMetricsDocument? page, IReadOnlyList<ProbeSnapshot> probes)
    {
        if (page?.Site is not { Seo: { } seo } site)
        {
            yield break;
        }

        // Broken pages already have a suggestion of their own (site:broken).
        foreach (var issue in seo.Issues.Where(i => i.Key != "broken"))
        {
            if (SeoChecks.Find(issue.Key) is not { } check)
            {
                continue;
            }

            var priority = issue.Key == "noindex-in-sitemap" || check.Weight >= 8 ? SuggestionPriority.High
                : check.Weight >= 4 ? SuggestionPriority.Medium
                : SuggestionPriority.Low;
            if (!issue.SiteWide && issue.Share < 0.2m && priority != SuggestionPriority.Low)
            {
                priority++; // only a few pages affected
            }

            var scope = issue.SiteWide ? "Site-wide" : string.Create(CultureInfo.InvariantCulture, $"{issue.Pages} of {issue.Total} crawled pages");
            var examples = issue.Examples.Count > 0 ? $" · e.g. {string.Join(", ", issue.Examples)}" : string.Empty;
            yield return new Suggestion($"seo:{issue.Key}", check.Title, issue.Note is null ? check.Why : $"{check.Why} {issue.Note}.",
                priority, "seo", [], $"{check.Label}: {scope}{examples}", SuggestionStatus.ToDo, "Found in the latest crawl.", []);
        }

        var questions = probes.GroupBy(p => p.PromptText, StringComparer.Ordinal).Select(g => (Text: g.Key, g.First().LanguageCode)).ToList();
        if (questions.Count == 0 || !site.Pages.Any(p => p.Topics is not null))
        {
            yield break;
        }

        var uncovered = questions.Where(q => SeoChecks.BestPageFor(q.Text, q.LanguageCode, site.Pages) is null).ToList();
        if (uncovered.Count > 0)
        {
            yield return new Suggestion("seo:topic-gap", SeoChecks.TopicGap.Title, SeoChecks.TopicGap.Why,
                uncovered.Count * 2 >= questions.Count ? SuggestionPriority.High : SuggestionPriority.Medium, "seo", [],
                string.Create(CultureInfo.InvariantCulture, $"{uncovered.Count} of {questions.Count} questions: ") + string.Join(" · ", uncovered.Select(q => $"“{q.Text}”")),
                SuggestionStatus.ToDo, string.Create(CultureInfo.InvariantCulture, $"Checked against the {site.Pages.Count} crawled pages."), []);
        }
    }

    // App Store listing compared with leading peer apps (public Apple data).
    private static IEnumerable<Suggestion> AppSuggestions(AuditMetricsDocument? page)
    {
        if (page?.App is not { } data)
        {
            yield break;
        }

        if (data.App is not { } app)
        {
            yield return new Suggestion("app:link", "Fix the App Store link for this channel", data.Error ?? "The app could not be found.",
                SuggestionPriority.Critical, "app-store", [], data.Error ?? string.Empty, SuggestionStatus.ToDo, "App data unavailable in the latest audit.", []);
            yield break;
        }

        var peers = data.Peers.Where(p => p.Rating is not null && p.RatingCount is > 0).ToList();
        var peerList = string.Join(", ", peers.Take(3).Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Name} {p.Rating:0.0} ({p.RatingCount:N0})")));

        if (app.Rating is { } rating && peers.Count > 0 && rating < peers.Average(p => p.Rating!.Value) - 0.05m)
        {
            yield return new Suggestion("app:rating-gap", "Close the rating gap with leading banking apps",
                string.Create(CultureInfo.InvariantCulture, $"{app.Name} is rated {rating:0.00}; the leading banking apps average {peers.Average(p => p.Rating!.Value):0.00}. AI engines quote store ratings when they compare banking apps."),
                SuggestionPriority.Medium, "app-store", [], $"Peers: {peerList}", SuggestionStatus.ToDo, "From the latest App Store data.", []);
        }

        if (app.RatingCount is { } count && peers.Count > 0)
        {
            var median = peers.Select(p => p.RatingCount!.Value).Order().ElementAt(peers.Count / 2);
            if (count * 2 < median)
            {
                yield return new Suggestion("app:rating-count", "Ask more satisfied customers to rate the app",
                    string.Create(CultureInfo.InvariantCulture, $"{app.Name} has {count:N0} ratings; leading banking apps have around {median:N0}. More ratings make the score more credible to customers and to engines."),
                    SuggestionPriority.Medium, "app-store", [], $"Peers: {peerList}", SuggestionStatus.ToDo, "From the latest App Store data.", []);
            }
        }

        if (data.RecentAverage is { } recent && recent < 4.0m)
        {
            var complaints = string.Join(" · ", data.RecentReviews.Where(r => r.Rating <= 2).Take(3).Select(r => $"“{r.Title}”"));
            yield return new Suggestion("app:recent-reviews", "Answer and fix what recent low ratings complain about",
                string.Create(CultureInfo.InvariantCulture, $"The {data.RecentReviews.Count} most recent reviews average {recent:0.0}. Reply in App Store Connect and address the repeated issues in the next release."),
                SuggestionPriority.High, "app-store", [], complaints, SuggestionStatus.ToDo, "From the latest App Store reviews.", []);
        }

        if (app.LastUpdatedUtc is { } updated && DateTime.UtcNow - updated > TimeSpan.FromDays(90))
        {
            yield return new Suggestion("app:freshness", "Ship and describe app updates more often",
                $"The last update was on {Day(updated)}. Regular releases with clear release notes signal an actively maintained app.",
                SuggestionPriority.Low, "app-store", [], $"Version {app.Version}", SuggestionStatus.ToDo, "From the latest App Store data.", []);
        }

        if (app.DescriptionLength < 1500)
        {
            yield return new Suggestion("app:description", "Expand the App Store description with what customers ask",
                string.Create(CultureInfo.InvariantCulture, $"The description has {app.DescriptionLength} characters. Answer the common questions (fees, transfers, account opening) in plain sentences engines can quote."),
                SuggestionPriority.Medium, "app-store", [], string.Empty, SuggestionStatus.ToDo, "From the latest App Store data.", []);
        }
    }

    // "; those pages have: enpara.com (FAQPage, 1,240 words), …" from the fetched competitor pages.
    private static string CitedPageFeatures(AuditMetricsDocument? page, List<string> hosts)
    {
        var described = hosts
            .Select(h => (page?.Competitors ?? []).FirstOrDefault(c => c.Fetched && string.Equals(c.Host, h, StringComparison.OrdinalIgnoreCase)))
            .OfType<CompetitorPage>()
            .Select(c =>
            {
                var types = c.JsonLdTypes.Where(t => t is "FAQPage" or "HowTo" or "Product" or "FinancialProduct" or "BankOrCreditUnion" or "Organization" or "Table").Take(3);
                return $"{c.Host} ({string.Join(", ", types.Append(c.WordCount.ToString("N0", CultureInfo.InvariantCulture) + " words"))})";
            })
            .ToList();
        return described.Count == 0
            ? "; see what those pages cover that yours doesn't."
            : $"; those pages have: {string.Join("; ", described)}. Match their depth and structure.";
    }

    // Brand-level findings: banks named more often than the brand, and the kinds of sources shaping the answers.
    private static IEnumerable<Suggestion> BrandSuggestions(AuditMetricsDocument? page)
    {
        if (page?.Brand is not { AnswersAnalyzed: > 0 } brand)
        {
            yield break;
        }

        var n = brand.AnswersAnalyzed;
        var basis = brand.FromSimulatedOnly ? " (simulated models without web search; indicative only)" : string.Empty;
        var own = brand.ShareOfVoice.FirstOrDefault(m => m.IsOwn);
        var ahead = brand.ShareOfVoice.Where(m => !m.IsOwn && m.Answers > (own?.Answers ?? 0)).ToList();
        if (own is not null && ahead.Count > 0)
        {
            var top = ahead[0];
            yield return new Suggestion("brand:share-of-voice", $"Competitors are named more often than {own.Brand}",
                string.Create(CultureInfo.InvariantCulture,
                    $"{top.Brand} is named in {top.Answers} of {n} answers, {own.Brand} in {own.Answers}. Find out what the answers credit them for, and publish the facts and comparisons that make {own.Brand} the natural recommendation."),
                brand.FromSimulatedOnly ? SuggestionPriority.Low : own.Answers * 2 <= top.Answers ? SuggestionPriority.High : SuggestionPriority.Medium,
                "brand-entity", [],
                "Ahead: " + string.Join(", ", ahead.Take(5).Select(m => string.Create(CultureInfo.InvariantCulture, $"{m.Brand} {m.Answers}/{n}"))) + basis,
                SuggestionStatus.ToDo, "From the latest audit's answers.", []);
        }

        var groups = brand.Sources.ToDictionary(g => g.Category, StringComparer.Ordinal);
        var ownCited = groups.GetValueOrDefault(BrandAnalyzer.Owned)?.Citations ?? 0;

        if (groups.GetValueOrDefault("Complaints & reviews") is { } complaints)
        {
            yield return new Suggestion("brand:complaints", "Answer the complaints the engines read",
                $"Engines cite complaint and review sites when they describe the bank ({string.Join(", ", complaints.TopHosts)}). Resolve and reply to the visible complaints, and publish clear help content for the recurring issues.",
                SuggestionPriority.High, "brand-entity", [],
                string.Create(CultureInfo.InvariantCulture, $"{complaints.Citations} citation(s) of complaint and review sites") + basis, SuggestionStatus.ToDo, "From the latest audit's answers.", []);
        }

        if (groups.GetValueOrDefault("Wikipedia") is { } wiki)
        {
            yield return new Suggestion("brand:wikipedia", "Keep the bank's Wikipedia and Wikidata entries complete and current",
                "Engines use Wikipedia for facts about the bank (ownership, history, size, products). Check that the Turkish and English articles and the Wikidata item are accurate, current and link to the official sites.",
                SuggestionPriority.Medium, "brand-entity", [],
                string.Create(CultureInfo.InvariantCulture, $"{wiki.Citations} citation(s): {string.Join(", ", wiki.TopHosts)}") + basis, SuggestionStatus.ToDo, "From the latest audit's answers.", []);
        }

        if (groups.GetValueOrDefault("Comparison sites") is { } comparison && comparison.Citations > ownCited)
        {
            yield return new Suggestion("brand:comparison-sites", "Make sure comparison sites show the bank's current offers",
                string.Create(CultureInfo.InvariantCulture,
                    $"Engines cited comparison sites {comparison.Citations} time(s) and the bank's own sites {ownCited} time(s). Check the bank's rates, fees and products on {string.Join(", ", comparison.TopHosts)} are listed, correct and current."),
                SuggestionPriority.Medium, "content", [], string.Join(", ", comparison.TopHosts) + basis, SuggestionStatus.ToDo, "From the latest audit's answers.", []);
        }
    }

    private static string LowerFirst(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

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

    private static IEnumerable<Suggestion> PromptSuggestions(List<AuditSnapshot> audits, string site, AuditMetricsDocument? latestPage)
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
            var citedText = cited.Count == 0 ? "" : $" They cited {string.Join(", ", cited)} instead{CitedPageFeatures(latestPage, cited)}";
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
