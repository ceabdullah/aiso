using Aiso.Domain;
using Aiso.Domain.Entities;
using Aiso.Domain.Enums;
using Aiso.Infrastructure.Persistence;
using Aiso.Infrastructure.Runtime;
using Aiso.Infrastructure.Security;
using Aiso.Infrastructure.Settings;
using Aiso.Web.Security;
using Aiso.Web.Services;
using Aiso.Worker.Pipeline;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aiso.Tests;

/// <summary>Live run progress, the Answers tab query and the engine trend (SQLite).</summary>
public sealed class DashboardLiveTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly Factory _factory;

    public DashboardLiveTests()
    {
        _connection.Open();
        _factory = new Factory(_connection);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private DashboardService Dashboard() => new(_factory, new SystemSettingsService(_factory),
        new RoleMappingProvider(_factory, new MemoryCache(new MemoryCacheOptions())),
        new EngineApiKeyProtector(new EphemeralDataProtectionProvider()), NullLogger<DashboardService>.Instance);

    [Fact]
    public void Run_status_round_trips_and_goes_stale()
    {
        var now = new DateTime(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);
        var json = new AuditRunStatus(now, now, "Asking AI engines", "TEB", 1, 3, 5, 24, "Gemini").ToJson();

        Assert.Equal(5, AuditRunStatus.TryParse(json, now.AddMinutes(1))!.AnswersDone);
        Assert.Null(AuditRunStatus.TryParse(json, now.AddMinutes(20)));  // worker stopped mid-run
        Assert.Null(AuditRunStatus.TryParse("", now));
        Assert.Null(AuditRunStatus.TryParse("{not json", now));
    }

    [Fact]
    public async Task Progress_reporter_writes_counts_the_dashboard_can_read_and_clears_them()
    {
        var ct = TestContext.Current.CancellationToken;
        var settings = new SystemSettingsService(_factory);
        using var progress = new RunProgressReporter(settings, NullLogger.Instance, targetsTotal: 2, answersTotal: 4);

        await progress.StageAsync("Asking AI engines", "TEB Core Banking Portal", ct);
        await Task.WhenAll(progress.AnswerAsync("Gemini", ct), progress.AnswerAsync("Claude", ct)); // engines answer in parallel

        var running = (await Dashboard().GetRunStateAsync(ct)).Running;
        Assert.NotNull(running);
        Assert.Equal(2, running.AnswersDone);
        Assert.Equal(4, running.AnswersTotal);
        Assert.Equal("TEB Core Banking Portal", running.Target);

        await progress.ClearAsync();
        Assert.Null((await Dashboard().GetRunStateAsync(ct)).Running);
    }

    [Fact]
    public async Task Answers_are_filtered_and_owned_sources_are_marked()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        var dashboard = Dashboard();

        var all = await dashboard.GetAnswersAsync(new AnswerFilter(null, null, null, "all", null), ct);
        Assert.Equal(3, all.Count);

        var missing = Assert.Single(await dashboard.GetAnswersAsync(new AnswerFilter(null, null, null, "missing", null), ct));
        Assert.Equal("claude", missing.EngineKey);
        Assert.Equal(["rival.com.tr"], missing.Sources.Select(s => s.Host));

        var visible = Assert.Single(await dashboard.GetAnswersAsync(new AnswerFilter("gemini", null, null, "visible", null), ct));
        Assert.Contains(visible.Sources, s => s.IsOwned && s.Host == "teb.com.tr"); // hosts are shown without www.
        Assert.Equal(["TEB"], visible.MatchedKeywords);

        Assert.Single(await dashboard.GetAnswersAsync(new AnswerFilter(null, null, null, "errors", null), ct));
        Assert.Single(await dashboard.GetAnswersAsync(new AnswerFilter(null, null, null, "all", "KOBİ kredisi"), ct));

        var facets = await dashboard.GetAnswerFacetsAsync(ct);
        Assert.Equal(["Claude", "Gemini"], facets.Engines.Select(e => e.Name));
    }

    [Fact]
    public async Task Engine_trend_counts_successful_answers_per_engine_and_day()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var trend = await Dashboard().GetEngineTrendAsync(30, ct);

        var gemini = Assert.Single(trend, s => s.EngineKey == "gemini");
        var day = Assert.Single(gemini.Days);
        Assert.Equal(1, day.Answers); // the failed Gemini answer is left out
        Assert.Equal(100m, day.Rate);
        Assert.Equal(0m, Assert.Single(Assert.Single(trend, s => s.EngineKey == "claude").Days).Rate);
    }

    [Fact]
    public async Task Summary_shows_each_engines_latest_answers_even_from_an_earlier_audit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var db = _factory.CreateDbContext())
        {
            var target = new TargetProfile { Code = "teb", DisplayName = "TEB", BaseUrl = "https://www.teb.com.tr", BrandKeywords = "TEB" };
            var earlier = new LiveSearchAuditLog { TargetProfile = target, RunId = Guid.NewGuid(), Status = AuditStatus.Succeeded };
            earlier.ProbeResults.Add(Probe("chatgpt", "ChatGPT", "q1", mentions: true, "TEB", null, null));
            earlier.ProbeResults.Add(Probe("chatgpt", "ChatGPT", "q2", mentions: false, null, null, null));
            var latest = new LiveSearchAuditLog { TargetProfile = target, RunId = Guid.NewGuid(), Status = AuditStatus.Succeeded };
            latest.ProbeResults.Add(Probe("nvidia", "NVIDIA (simulated)", "q1", mentions: false, null, null, null));
            // The worker registers every configured engine with its kind; that is how the dashboard knows NVIDIA is simulated.
            db.AnswerEngineSettings.Add(new AnswerEngineSetting { EngineKey = "nvidia", DisplayName = "NVIDIA (simulated)", Kind = "OpenAiCompatibleChat" });
            db.LiveSearchAuditLogs.AddRange(earlier, latest);
            await db.SaveChangesAsync(ct);
            earlier.CreatedAtUtc = DateTime.UtcNow.AddHours(-3); // the context stamps inserts with "now"; backdate the earlier audit
            await db.SaveChangesAsync(ct);
        }

        var card = Assert.Single((await Dashboard().GetSummaryAsync(ct)).Targets);
        var engines = card.Engines;
        Assert.Equal(50m, card.PromptVisibilityScore); // ChatGPT's latest answers, NVIDIA not counted
        Assert.Equal(30m, card.OverallScore);          // 0.4 × on-page 0 + 0.6 × 50

        var chatgpt = Assert.Single(engines, e => e.Engine == "ChatGPT"); // ChatGPT is off now, but its paid answers still show
        Assert.Equal(50m, chatgpt.Rate);
        Assert.True(chatgpt.AsOfUtc < DateTime.UtcNow.AddHours(-2));
        Assert.Single(engines, e => e.Engine == "NVIDIA (simulated)");
    }

    private async Task SeedAsync(CancellationToken ct)
    {
        await using var db = _factory.CreateDbContext();
        var target = new TargetProfile
        {
            Code = "teb-portal", DisplayName = "TEB Core Banking Portal", BaseUrl = "https://www.teb.com.tr",
            BrandKeywords = "TEB", OwnedDomains = "teb.com.tr",
        };
        var log = new LiveSearchAuditLog { TargetProfile = target, RunId = Guid.NewGuid(), Status = AuditStatus.PartiallySucceeded };
        log.ProbeResults.Add(Probe("gemini", "Gemini", "En iyi KOBİ kredisi?", mentions: true, "TEB",
            """[{"url":"https://www.teb.com.tr/kobi","title":"TEB"}]""", null));
        log.ProbeResults.Add(Probe("claude", "Claude", "Best SME bank?", mentions: false, null,
            """[{"url":"https://rival.com.tr/sme","title":null}]""", null));
        log.ProbeResults.Add(Probe("gemini", "Gemini", "Best SME bank?", mentions: false, null, null, "Gemini returned HTTP 429"));
        db.LiveSearchAuditLogs.Add(log);
        await db.SaveChangesAsync(ct);
    }

    private static EngineProbeResult Probe(string key, string name, string prompt, bool mentions, string? keywords, string? citations, string? error) => new()
    {
        PromptId = Guid.NewGuid(), EngineKey = key, EngineName = name, LanguageCode = "tr-TR", PromptText = prompt,
        MentionsBrand = mentions, CitesOwnedDomain = citations?.Contains("teb.com.tr", StringComparison.Ordinal) == true,
        MatchedKeywords = keywords, CitationsJson = citations, AnswerExcerpt = error is null ? $"{name} answer about {prompt}" : null, ErrorMessage = error,
    };

    private sealed class Factory(SqliteConnection connection) : IDbContextFactory<AisoDbContext>
    {
        public AisoDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AisoDbContext>().UseSqlite(connection).Options);
    }
}

public class AnswerTextTests
{
    [Fact]
    public void Markdown_becomes_plain_text_and_links_keep_their_text()
    {
        const string md = "### Yaygın bankalar\n\n**Kısa cevap:** TEB ve Enpara. ([apps.apple.com](https://apps.apple.com/tr/x?y=1))\n\n\n\nKod: `CEPTETEB`";
        Assert.Equal("Yaygın bankalar\n\nKısa cevap: TEB ve Enpara. (apps.apple.com)\n\nKod: CEPTETEB", AnswerText.ToPlain(md));
    }

    [Fact]
    public void Markup_stays_text()
    {
        // Answers are rendered as text, so a script tag is shown, never run; ToPlain must not try to "clean" it into HTML.
        Assert.Equal("<script>x</script>", AnswerText.ToPlain("<script>x</script>"));
    }
}
