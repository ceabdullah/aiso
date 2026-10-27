using Aiso.Domain.Entities;
using Aiso.Domain.Enums;
using Aiso.Infrastructure.Engines;
using Aiso.Infrastructure.Persistence;
using Aiso.Infrastructure.Recommendations;
using Aiso.Infrastructure.Scraping;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aiso.Tests;

/// <summary>Brand-level analysis (share of voice, source mix), its suggestions, and the seeded brand channel.</summary>
public sealed class BrandTests
{
    private static EngineProbeOutcome Answer(string text, bool mentions, bool searchesWeb = true, params string[] cited)
    {
        var citations = cited.Select(u => new EngineCitation(u, null)).ToList();
        return new EngineProbeOutcome("chatgpt", "ChatGPT", Guid.NewGuid(), "tr-TR", "q", text, citations,
            citations.Where(c => c.Url.Contains("teb.com.tr", StringComparison.Ordinal)).ToList(), mentions ? ["TEB"] : [], null, 1, searchesWeb);
    }

    [Fact]
    public void Share_of_voice_and_sources_come_from_the_web_searching_answers()
    {
        var probes = new[]
        {
            Answer("Garanti BBVA ve Akbank öne çıkıyor.", false, true, "https://www.garantibbva.com.tr/x", "https://www.sikayetvar.com/teb"),
            Answer("TEB ve Garanti iyi seçenekler.", true, true, "https://www.teb.com.tr/a", "https://tr.wikipedia.org/wiki/TEB"),
            Answer("Ziraat ve İŞBANK.", false, true, "https://www.hangikredi.com/b"),
            Answer("Only TEB, from memory.", true, searchesWeb: false), // ignored: web answers exist
        };

        var a = BrandAnalyzer.Analyze(probes, "TEB", ["teb.com.tr"], CompetitorOptions.Defaults)!;

        Assert.Equal(3, a.AnswersAnalyzed);
        Assert.False(a.FromSimulatedOnly);
        Assert.Equal(("Garanti BBVA", 2), (a.ShareOfVoice[0].Brand, a.ShareOfVoice[0].Answers));
        Assert.Contains(a.ShareOfVoice, m => m.IsOwn && m.Answers == 1);
        Assert.Contains(a.ShareOfVoice, m => m.Brand == "İş Bankası" && m.Answers == 1); // alias, Turkish-aware case
        var kinds = a.Sources.ToDictionary(s => s.Category, s => s.Citations);
        Assert.Equal(1, kinds[BrandAnalyzer.Owned]);
        Assert.Equal(1, kinds[BrandAnalyzer.CompetitorBanks]);
        Assert.Equal(1, kinds["Complaints & reviews"]);
        Assert.Equal(1, kinds["Wikipedia"]);
        Assert.Equal(1, kinds["Comparison sites"]);
    }

    [Fact]
    public void Simulated_answers_are_used_only_when_nothing_searched_the_web()
    {
        var a = BrandAnalyzer.Analyze([Answer("TEB ve Akbank.", true, searchesWeb: false)], "TEB", ["teb.com.tr"], CompetitorOptions.Defaults)!;
        Assert.True(a.FromSimulatedOnly);
        Assert.Equal(1, a.AnswersAnalyzed);
        Assert.Null(BrandAnalyzer.Analyze([], "TEB", [], CompetitorOptions.Defaults));
    }

    [Theory]
    [InlineData("www.bloomberght.com", "News & media")]
    [InlineData("haber.example", "News & media")]
    [InlineData("bddk.org.tr", "Official & regulators")]
    [InlineData("eksisozluk.com", "Social & forums")]
    [InlineData("apps.apple.com", "App stores")]
    [InlineData("akbank.com", BrandAnalyzer.CompetitorBanks)]
    [InlineData("random.example", "Other sites")]
    public void Sources_are_classified_by_host(string host, string expected) =>
        Assert.Equal(expected, BrandAnalyzer.Classify(host, CompetitorOptions.Defaults));

    [Fact]
    public void Brand_suggestions_cover_share_of_voice_complaints_wikipedia_and_comparison_sites()
    {
        var brand = new BrandAnalysis(8, false,
            [new BrandMention("Garanti BBVA", 6, false), new BrandMention("Akbank", 4, false), new BrandMention("TEB", 2, true)],
            [new SourceGroup("Comparison sites", 5, ["hangikredi.com"]), new SourceGroup("Complaints & reviews", 2, ["sikayetvar.com"]),
             new SourceGroup("Wikipedia", 1, ["tr.wikipedia.org"]), new SourceGroup(BrandAnalyzer.Owned, 1, ["teb.com.tr"])]);
        var page = new AuditMetricsDocument(null, [], Brand: brand);

        var s = VisibilityAdvisor.Analyze([new AuditSnapshot(DateTime.UtcNow, page, [])], ["teb.com.tr"]);

        var voice = Assert.Single(s, x => x.Key == "brand:share-of-voice");
        Assert.Equal(SuggestionPriority.High, voice.Priority); // 2 vs 6
        Assert.Contains("Garanti BBVA is named in 6 of 8 answers, TEB in 2", voice.Detail, StringComparison.Ordinal);
        Assert.Equal("Digital Marketing", voice.Owner);
        Assert.Contains(s, x => x.Key == "brand:complaints" && x.Detail.Contains("sikayetvar.com", StringComparison.Ordinal));
        Assert.Contains(s, x => x.Key == "brand:wikipedia");
        Assert.Contains(s, x => x.Key == "brand:comparison-sites");
    }

    [Fact]
    public async Task Seeder_upgrades_the_app_link_and_adds_the_brand_channel_once()
    {
        var ct = TestContext.Current.CancellationToken;
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);
        AisoDbContext Db() => new(new DbContextOptionsBuilder<AisoDbContext>().UseSqlite(connection).Options);
        await using (var db = Db())
        {
            await db.Database.EnsureCreatedAsync(ct);
            db.TargetProfiles.Add(new TargetProfile
            {
                Code = "teb-ios", DisplayName = "TEB Mobile (App Store)", BaseUrl = "https://apps.apple.com/tr/app/teb-mobil/id000000000",
                ChannelKind = TargetChannelKind.MobileAppStore, OwnedDomains = "apps.apple.com/tr/app/teb-mobil,teb.com.tr",
            });
            await db.SaveChangesAsync(ct);
        }

        var seeder = new DatabaseSeeder(NullLogger<DatabaseSeeder>.Instance);
        await using (var db = Db()) { await seeder.SeedAsync(db, ct); }
        await using (var db = Db()) { await seeder.SeedAsync(db, ct); } // idempotent

        await using var check = Db();
        var app = await check.TargetProfiles.SingleAsync(t => t.Code == "teb-ios", ct);
        Assert.Equal("https://apps.apple.com/tr/app/cepteteb/id353385550", app.BaseUrl);
        Assert.Equal("apps.apple.com/tr/app/cepteteb,teb.com.tr", app.OwnedDomains);
        var brand = await check.TargetProfiles.Include(t => t.TestPrompts).SingleAsync(t => t.Code == "teb-brand", ct);
        Assert.Equal(TargetChannelKind.Brand, brand.ChannelKind);
        Assert.Equal(6, brand.TestPrompts.Count);
    }
}
