using Aiso.Infrastructure.Engines;

namespace Aiso.Infrastructure.Recommendations;

/// <summary>A competitor whose mentions and sites are tracked. Bound from <c>Aiso:Competitors:Brands</c>.</summary>
public sealed class CompetitorBrand
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Other names that count as a mention (the name itself always counts).</summary>
    public List<string> Aliases { get; set; } = [];

    /// <summary>The competitor's own domains, for classifying cited sources.</summary>
    public List<string> Domains { get; set; } = [];
}

public sealed class CompetitorOptions
{
    public const string SectionName = "Aiso:Competitors";

    public List<CompetitorBrand> Brands { get; set; } = [];

    /// <summary>The configured competitors, or the main Turkish banks and digital banks when none are configured.</summary>
    public IReadOnlyList<CompetitorBrand> Effective => Brands.Count > 0 ? Brands : Defaults;

    public static readonly IReadOnlyList<CompetitorBrand> Defaults =
    [
        new() { Name = "Garanti BBVA", Aliases = ["Garanti"], Domains = ["garantibbva.com.tr", "garantibbvainvestorrelations.com"] },
        new() { Name = "Akbank", Domains = ["akbank.com"] },
        new() { Name = "İş Bankası", Aliases = ["Türkiye İş Bankası", "İşbank", "Isbank", "Is Bankasi"], Domains = ["isbank.com.tr"] },
        new() { Name = "Yapı Kredi", Aliases = ["Yapi Kredi"], Domains = ["yapikredi.com.tr"] },
        new() { Name = "Ziraat Bankası", Aliases = ["Ziraat"], Domains = ["ziraatbank.com.tr"] },
        new() { Name = "Halkbank", Domains = ["halkbank.com.tr"] },
        new() { Name = "VakıfBank", Aliases = ["Vakifbank"], Domains = ["vakifbank.com.tr"] },
        new() { Name = "QNB", Aliases = ["QNB Finansbank", "Finansbank"], Domains = ["qnb.com.tr"] },
        new() { Name = "DenizBank", Aliases = ["MobilDeniz"], Domains = ["denizbank.com"] },
        new() { Name = "Enpara", Aliases = ["Enpara.com"], Domains = ["enpara.com"] },
        new() { Name = "ING", Aliases = ["ING Türkiye"], Domains = ["ing.com.tr"] },
        new() { Name = "Kuveyt Türk", Aliases = ["Kuveyt Turk"], Domains = ["kuveytturk.com.tr"] },
        new() { Name = "Papara", Domains = ["papara.com"] },
    ];
}

/// <summary>How many analysed answers named a bank; <see cref="IsOwn"/> marks the audited brand.</summary>
public sealed record BrandMention(string Brand, int Answers, bool IsOwn);

/// <summary>Cited sources of one kind (news, comparison sites, …) with the most cited hosts.</summary>
public sealed record SourceGroup(string Category, int Citations, IReadOnlyList<string> TopHosts);

/// <summary>
/// Which banks the engines name (share of voice) and which kinds of sources they rely on, over one audit's answers.
/// Web-searching engines only when there are any; otherwise the simulated ones, flagged by <see cref="FromSimulatedOnly"/>.
/// </summary>
public sealed record BrandAnalysis(int AnswersAnalyzed, bool FromSimulatedOnly, IReadOnlyList<BrandMention> ShareOfVoice, IReadOnlyList<SourceGroup> Sources);

public static class BrandAnalyzer
{
    public const string Owned = "Own sites";
    public const string CompetitorBanks = "Competitor banks";

    public static BrandAnalysis? Analyze(
        IReadOnlyList<EngineProbeOutcome> probes, string ownBrand, IReadOnlyList<string> ownedDomains, IReadOnlyList<CompetitorBrand> competitors)
    {
        ArgumentNullException.ThrowIfNull(probes);
        ArgumentNullException.ThrowIfNull(ownedDomains);
        ArgumentNullException.ThrowIfNull(competitors);
        var answered = probes.Where(p => p.Succeeded).ToList();
        var web = answered.Where(p => p.SearchesWeb).ToList();
        var analysed = web.Count > 0 ? web : answered;
        if (analysed.Count == 0)
        {
            return null;
        }

        var mentions = new List<BrandMention> { new(ownBrand, analysed.Count(p => p.MentionsBrand), true) };
        mentions.AddRange(competitors
            .Select(c => new BrandMention(c.Name,
                analysed.Count(p => BrandMatcher.MatchKeywords(p.Answer, [c.Name, .. c.Aliases], p.LanguageCode).Count > 0), false))
            .Where(m => m.Answers > 0));

        var sources = analysed
            .SelectMany(p => p.Citations.Select(c => (Citation: c, Owned: p.OwnedCitations.Contains(c))))
            .Select(x => (Host: OwnedDomainMatcher.HostOf(x.Citation), x.Owned))
            .Where(x => x.Host is not null)
            .Select(x => (Host: x.Host!, Category: x.Owned ? Owned : Classify(x.Host!, competitors)))
            .GroupBy(x => x.Category, StringComparer.Ordinal)
            .Select(g => new SourceGroup(g.Key, g.Count(),
                g.GroupBy(x => x.Host, StringComparer.OrdinalIgnoreCase).OrderByDescending(h => h.Count()).ThenBy(h => h.Key, StringComparer.Ordinal)
                    .Take(4).Select(h => h.Key).ToList()))
            .OrderByDescending(s => s.Citations).ThenBy(s => s.Category, StringComparer.Ordinal)
            .ToList();

        return new BrandAnalysis(analysed.Count, web.Count == 0,
            mentions.OrderByDescending(m => m.Answers).ThenBy(m => m.IsOwn ? 0 : 1).ThenBy(m => m.Brand, StringComparer.Ordinal).ToList(),
            sources);
    }

    /// <summary>Kind of a cited site, from its host. Public for unit tests.</summary>
    public static string Classify(string host, IReadOnlyList<CompetitorBrand> competitors)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(competitors);
        var h = host.ToLowerInvariant();
        bool Is(params string[] domains) => domains.Any(d => h == d || h.EndsWith("." + d, StringComparison.Ordinal));

        if (competitors.Any(c => c.Domains.Any(d => Is(d))))
        {
            return CompetitorBanks;
        }

        return h switch
        {
            _ when Is("wikipedia.org", "wikidata.org") => "Wikipedia",
            _ when Is("sikayetvar.com", "trustpilot.com") || h.Contains("sikayet", StringComparison.Ordinal) => "Complaints & reviews",
            _ when Is("hangikredi.com", "enuygun.com", "paraanaliz.com", "hesapkurdu.com", "bankasor.com", "teklifimgelsin.com", "kredimaksimum.com", "finansfix.com", "encazip.com")
                => "Comparison sites",
            _ when Is("apps.apple.com", "play.google.com") => "App stores",
            _ when Is("tbb.org.tr", "bddk.org.tr", "tcmb.gov.tr", "spk.gov.tr") || h.EndsWith(".gov.tr", StringComparison.Ordinal) => "Official & regulators",
            _ when Is("x.com", "twitter.com", "linkedin.com", "youtube.com", "reddit.com", "eksisozluk.com", "instagram.com", "facebook.com") => "Social & forums",
            _ when Is("hurriyet.com.tr", "milliyet.com.tr", "sabah.com.tr", "sozcu.com.tr", "ntv.com.tr", "cnnturk.com", "bloomberght.com", "ekonomim.com",
                      "dunya.com", "aa.com.tr", "reuters.com", "bbc.com", "haberturk.com", "trthaber.com", "fintechistanbul.org")
                   || h.Contains("haber", StringComparison.Ordinal) || h.Contains("news", StringComparison.Ordinal) => "News & media",
            _ => "Other sites",
        };
    }
}
