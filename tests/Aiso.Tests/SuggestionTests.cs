using System.Text.Json;
using Aiso.Domain.Entities;
using Aiso.Domain.Enums;
using Aiso.Infrastructure.Persistence;
using Aiso.Infrastructure.Recommendations;
using Aiso.Infrastructure.Scraping;
using Aiso.Infrastructure.Security;
using Aiso.Infrastructure.Settings;
using Aiso.Web.Security;
using Aiso.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aiso.Tests;

public sealed class SuggestionTests
{
    private static readonly Guid PromptA = Guid.NewGuid();
    private static readonly DateTime Day1 = new(2026, 10, 1, 6, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Owned = ["teb.com.tr"];
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static AuditMetricsDocument Page(bool faq, bool org = true) => new(null,
    [
        new ScoreSignal("Indexable", 15, true, "no noindex directive"),
        new ScoreSignal("OrganizationSchema", 12, org, org ? "present" : "no Organization JSON-LD"),
        new ScoreSignal("FaqSchema", 10, faq, faq ? "present" : "no FAQPage JSON-LD"),
    ]);

    private static ProbeSnapshot Probe(string engine, bool visible, params string[] citedHosts) =>
        new(PromptA, "Aidatsız kredi kartı veren bankalar hangileri?", "tr-TR", engine.ToLowerInvariant(), engine, visible, false, citedHosts);

    [Fact]
    public void Failing_signal_becomes_a_to_do_with_its_evidence()
    {
        var s = VisibilityAdvisor.Analyze([new AuditSnapshot(Day1, Page(faq: false), [])], Owned);

        var faq = Assert.Single(s, x => x.Key == "signal:FaqSchema");
        Assert.Equal(SuggestionStatus.ToDo, faq.Status);
        Assert.Equal(SuggestionPriority.High, faq.Priority);
        Assert.Equal("Web", faq.Owner);
        Assert.Contains("no FAQPage JSON-LD", faq.Evidence, StringComparison.Ordinal);
        Assert.DoesNotContain(s, x => x.Key == "signal:Indexable"); // passing all along: nothing to say
    }

    [Fact]
    public void Fixed_signal_is_confirmed_when_engine_visibility_rises_afterwards()
    {
        var s = VisibilityAdvisor.Analyze(
        [
            new AuditSnapshot(Day1, Page(faq: false), [Probe("Gemini", false), Probe("ChatGPT", false)]),
            new AuditSnapshot(Day1.AddDays(7), Page(faq: true), [Probe("Gemini", true), Probe("ChatGPT", false)]),
        ], Owned);

        var faq = Assert.Single(s, x => x.Key == "signal:FaqSchema");
        Assert.Equal(SuggestionStatus.Confirmed, faq.Status);
        Assert.Contains("since 2026-10-08", faq.StatusDetail, StringComparison.Ordinal);
        Assert.Contains("0% before, 50% since", faq.StatusDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Fixed_signal_waits_on_engines_until_answers_improve()
    {
        var s = VisibilityAdvisor.Analyze(
        [
            new AuditSnapshot(Day1, Page(faq: false), [Probe("Gemini", false)]),
            new AuditSnapshot(Day1.AddDays(1), Page(faq: true), [Probe("Gemini", false)]),
        ], Owned);

        Assert.Equal(SuggestionStatus.LiveAwaitingEngines, Assert.Single(s, x => x.Key == "signal:FaqSchema").Status);
    }

    [Fact]
    public void Prompt_missing_everywhere_asks_for_an_answer_page_and_names_who_engines_cite()
    {
        var s = VisibilityAdvisor.Analyze([new AuditSnapshot(Day1, null,
            [Probe("Gemini", false, "rival.com.tr", "forum.example"), Probe("ChatGPT", false, "rival.com.tr")])], Owned);

        var gap = Assert.Single(s);
        Assert.Equal(SuggestionStatus.ToDo, gap.Status);
        Assert.Equal(SuggestionPriority.High, gap.Priority);
        Assert.StartsWith("Publish a page that answers", gap.Title, StringComparison.Ordinal);
        Assert.Contains("rival.com.tr, forum.example", gap.Detail, StringComparison.Ordinal); // most-cited first
        Assert.Contains("Turkish", gap.Detail, StringComparison.Ordinal);
        Assert.Equal(["ChatGPT", "Gemini"], gap.EngineTips.Select(t => t.Engine));
    }

    [Fact]
    public void Prompt_gap_on_one_engine_is_confirmed_once_that_engine_shows_the_brand()
    {
        var before = VisibilityAdvisor.Analyze([new AuditSnapshot(Day1, null, [Probe("Gemini", false), Probe("ChatGPT", true)])], Owned);
        Assert.StartsWith("Close the Gemini gap", Assert.Single(before).Title, StringComparison.Ordinal);

        var after = VisibilityAdvisor.Analyze(
        [
            new AuditSnapshot(Day1, null, [Probe("Gemini", false), Probe("ChatGPT", true)]),
            new AuditSnapshot(Day1.AddDays(14), null, [Probe("Gemini", true), Probe("ChatGPT", true)]),
        ], Owned);

        var done = Assert.Single(after);
        Assert.Equal(SuggestionStatus.Confirmed, done.Status);
        Assert.Contains("Gemini (since 2026-10-15)", done.StatusDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Faq_kit_uses_the_test_prompts_and_is_valid_json_ld()
    {
        var faq = Assert.Single(VisibilityAdvisor.Analyze([new AuditSnapshot(Day1, Page(faq: false), [])], Owned), x => x.Key == "signal:FaqSchema");
        var kit = ApplyKit.For(faq, new KitContext("TEB", "https://www.teb.com.tr", "tr-TR", ["Aidatsız kart var mı?", "Faiz <oranı> nedir?"]));

        var script = Assert.Single(kit, k => k.FileName == "faq.jsonld.html").Content;
        var json = script["<script type=\"application/ld+json\">".Length..script.LastIndexOf("</script>", StringComparison.Ordinal)];
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("FAQPage", doc.RootElement.GetProperty("@type").GetString());
        Assert.Equal("Faiz <oranı> nedir?", doc.RootElement.GetProperty("mainEntity")[1].GetProperty("name").GetString());
        Assert.DoesNotContain("<oranı>", json, StringComparison.Ordinal); // escaped, so it can't close the script tag
        Assert.Contains("Aidatsız", json, StringComparison.Ordinal);       // Turkish letters stay readable

        Assert.Contains("<h3>Aidatsız kart var mı?</h3>", Assert.Single(kit, k => k.FileName == "faq-section.html").Content, StringComparison.Ordinal);
        Assert.Contains("<h3>Faiz &lt;oran", Assert.Single(kit, k => k.FileName == "faq-section.html").Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Prompt_kit_has_a_brief_and_robots_lines_for_the_engines_that_miss_it()
    {
        var gap = Assert.Single(VisibilityAdvisor.Analyze([new AuditSnapshot(Day1, null, [Probe("Gemini", false), Probe("Perplexity", false)])], Owned));
        var kit = ApplyKit.For(gap, new KitContext("TEB", "https://www.teb.com.tr", "tr-TR", []));

        Assert.Contains("### Aidatsız kredi kartı veren bankalar hangileri?", Assert.Single(kit, k => k.FileName == "article-brief.md").Content, StringComparison.Ordinal);
        var robots = Assert.Single(kit, k => k.FileName == "robots-ai.txt").Content;
        Assert.Contains("User-agent: Googlebot", robots, StringComparison.Ordinal);
        Assert.Contains("User-agent: PerplexityBot", robots, StringComparison.Ordinal);
        Assert.DoesNotContain("OAI-SearchBot", robots, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_feed_reads_audit_history_and_skips_owned_citations()
    {
        var ct = TestContext.Current.CancellationToken;
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);
        var factory = new SqliteFactory(connection);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync(ct);
            var target = new TargetProfile
            {
                Code = "teb-portal", DisplayName = "TEB Core Banking Portal", BaseUrl = "https://www.teb.com.tr",
                OwnedDomains = "teb.com.tr", PrimaryLanguageCode = "tr-TR",
            };
            target.TestPrompts.Add(new AutomatedTestPrompt { PromptText = "Aidatsız kart var mı?", LanguageCode = "tr-TR" });
            target.TestPrompts.Add(new AutomatedTestPrompt { PromptText = "Is there a no-fee card?", LanguageCode = "en-US" });
            db.TargetProfiles.Add(target);

            var log = new LiveSearchAuditLog
            {
                TargetProfile = target, RunId = Guid.NewGuid(), Status = AuditStatus.Succeeded,
                MetricsJson = JsonSerializer.Serialize(Page(faq: false), WebJson),
            };
            log.ProbeResults.Add(new EngineProbeResult
            {
                PromptId = PromptA, EngineKey = "gemini", EngineName = "Gemini", LanguageCode = "tr-TR", PromptText = "Aidatsız kart var mı?",
                CitationsJson = """[{"url":"https://www.teb.com.tr/kart","title":null},{"url":"https://rival.com.tr/kart","title":null}]""",
            });
            db.LiveSearchAuditLogs.Add(log);
            await db.SaveChangesAsync(ct);
        }

        var dashboard = new DashboardService(factory, new SystemSettingsService(factory),
            new RoleMappingProvider(factory, new MemoryCache(new MemoryCacheOptions())),
            new EngineApiKeyProtector(new EphemeralDataProtectionProvider()), NullLogger<DashboardService>.Instance);
        var feed = await dashboard.GetSuggestionFeedAsync(ct);

        var channel = Assert.Single(feed.Channels);
        Assert.True(channel.HasEngineData);
        Assert.Equal(["Aidatsız kart var mı?"], channel.Kit.Questions); // primary language only
        Assert.Contains(feed.Items, i => i.Suggestion.Key == "signal:FaqSchema");
        var gap = Assert.Single(feed.Items, i => i.Suggestion.Key.StartsWith("prompt:", StringComparison.Ordinal)).Suggestion;
        Assert.Contains("They cited rival.com.tr instead", gap.Detail, StringComparison.Ordinal); // the owned teb.com.tr citation isn't listed
    }

    private sealed class SqliteFactory(SqliteConnection connection) : IDbContextFactory<AisoDbContext>
    {
        public AisoDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<AisoDbContext>().UseSqlite(connection).Options);
    }
}
