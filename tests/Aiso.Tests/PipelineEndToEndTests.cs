using System.Net;
using System.Text;
using Aiso.Domain.Entities;
using Aiso.Domain.Enums;
using Aiso.Infrastructure.Engines;
using Aiso.Infrastructure.Notifications;
using Aiso.Infrastructure.Persistence;
using Aiso.Infrastructure.Reporting;
using Aiso.Infrastructure.Runtime;
using Aiso.Infrastructure.Scraping;
using Aiso.Infrastructure.Workspace;
using Aiso.Worker.Pipeline;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aiso.Tests;

/// <summary>
/// Runs <see cref="AuditPipelineService"/> end to end against SQLite, with HTTP and claude.exe stubbed:
/// verifies data/ snapshots, scores, Markdown export to reports/, persistence and alerting together.
/// </summary>
public sealed class PipelineEndToEndTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly string _root = Directory.CreateTempSubdirectory("aiso-e2e-").FullName;

    public PipelineEndToEndTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task RunAsync_produces_audit_snapshot_report_and_alert()
    {
        var ct = TestContext.Current.CancellationToken;
        IDbContextFactory<AisoDbContext> factory = new SqliteFactory(_connection);
        await using (var seed = await factory.CreateDbContextAsync(ct))
        {
            seed.Database.EnsureCreated();
            var target = new TargetProfile
            {
                Code = "teb-portal", DisplayName = "TEB Portal", BaseUrl = "https://www.teb.com.tr",
                BrandKeywords = "TEB,Türk Ekonomi Bankası", OwnedDomains = "teb.com.tr", PrimaryLanguageCode = "tr-TR",
            };
            target.TestPrompts.Add(new AutomatedTestPrompt { PromptText = "KOBİ kredisi için en iyi banka?", LanguageCode = "tr-TR" });
            target.TestPrompts.Add(new AutomatedTestPrompt { PromptText = "Best SME bank in Turkey?", LanguageCode = "en-US" });
            seed.TargetProfiles.Add(target);
            await seed.SaveChangesAsync(ct);
        }

        var workspace = Options.Create(new WorkspaceOptions { RootPath = _root });
        // Two engines: ChatGPT names the brand in Turkish only; Perplexity never names it but cites teb.com.tr once.
        var engines = new AnswerEngineOptions();
        engines.Engines["chatgpt"] = new() { DisplayName = "ChatGPT", Kind = AnswerEngineKind.OpenAiResponses, ApiKey = "o", Model = "gpt" };
        engines.Engines["perplexity"] = new() { DisplayName = "Perplexity", Kind = AnswerEngineKind.PerplexitySonar, ApiKey = "p", Model = "sonar" };
        var perplexityCalls = 0;
        var http = new StubHttpFactory(req =>
        {
            var body = System.Text.Json.Nodes.JsonNode.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!.ToJsonString(
                new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            return req.RequestUri!.Host == "api.openai.com"
                ? Json(body.Contains("KOBİ", StringComparison.Ordinal)
                    ? """{"output":[{"type":"message","content":[{"type":"output_text","text":"TEB ve Garanti öne çıkıyor.","annotations":[]}]}]}"""
                    : """{"output":[{"type":"message","content":[{"type":"output_text","text":"Akbank is popular.","annotations":[]}]}]}""")
                : Json(++perplexityCalls == 1
                    ? """{"choices":[{"message":{"content":"Bir banka."}}],"search_results":[{"title":"x","url":"https://www.teb.com.tr/kobi"}]}"""
                    : """{"choices":[{"message":{"content":"Another bank."}}],"search_results":[{"title":"y","url":"https://www.akbank.com/"}]}""");
        });

        var scraper = new NativeHtmlScraperService(
            new HttpClient(new StubHandler(_ => Html("<html lang='tr'><head><title>TEB Bireysel Bankacılık</title></head><body><h1>TEB</h1></body></html>"))),
            Options.Create(new ScraperOptions()), NullLogger<NativeHtmlScraperService>.Instance);
        var probe = new AnswerEngineProbe(Options.Create(engines), new EfEngineSettingsStore(factory,
            new Aiso.Infrastructure.Security.EngineApiKeyProtector(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()),
            TimeProvider.System, NullLogger<EfEngineSettingsStore>.Instance),
            [new OpenAiResponsesClient(http), new PerplexitySonarClient(http)], new Aiso.Infrastructure.Settings.SystemSettingsService(factory), NullLogger<AnswerEngineProbe>.Instance);
        var agent = new StubAgent("""{"summary":"Özet.","items":[{"title":"FAQPage ekleyin","priority":"high","category":"structured-data"}]}""");
        var alerts = new CapturingNotifications();

        var pipeline = new AuditPipelineService(factory, scraper, probe, agent,
            new AisoMarkdownExporter(workspace, NullLogger<AisoMarkdownExporter>.Instance), alerts,
            new Aiso.Infrastructure.Settings.SystemSettingsService(factory),
            workspace, Options.Create(new ClaudeCliOptions()), NullLogger<AuditPipelineService>.Instance);

        var summary = await pipeline.RunAsync(ct);

        Assert.Equal(1, summary.TargetsAudited);
        Assert.Equal(1, summary.PlansExported);
        Assert.True(File.Exists(Path.Combine(summary.RunFolder, "teb-portal.scrape.json")));
        Assert.True(File.Exists(Path.Combine(summary.RunFolder, "teb-portal.prompts.json")));
        Assert.True(File.Exists(Path.Combine(summary.RunFolder, "run-manifest.json")));
        Assert.Contains("Analyze the scraped results in data/", agent.LastPrompt, StringComparison.Ordinal);

        await using var db = await factory.CreateDbContextAsync(ct);
        var log = await db.LiveSearchAuditLogs.SingleAsync(ct);
        Assert.Equal(AuditStatus.Succeeded, log.Status);
        Assert.Equal(4, log.PromptsEvaluated);                // 2 prompts × 2 engines
        Assert.Equal(1, log.PromptsMentioningBrand);          // ChatGPT, Turkish prompt
        Assert.Equal(1, log.PromptsCitingDomain);             // Perplexity cited teb.com.tr once
        Assert.Equal(50m, log.PromptVisibilityScore);         // 2 of 4 answers visible
        Assert.Equal(Math.Round(0.4m * log.OnPageScore + 0.6m * 50m, 2), log.OverallScore);
        Assert.NotEmpty(AuditMetricsDocument.TryParse(log.MetricsJson)!.Signals);

        var rows = await db.EngineProbeResults.ToListAsync(ct);
        Assert.Equal(4, rows.Count);
        Assert.Equal(2, rows.Count(r => r.EngineName == "Perplexity"));
        Assert.Contains(rows, r => r.EngineName == "Perplexity" && r.CitesOwnedDomain && r.CitationsJson!.Contains("teb.com.tr", StringComparison.Ordinal));

        // The run registered both engines for the admin page, with key status.
        Assert.Equal(["chatgpt", "perplexity"], await db.AnswerEngineSettings.Where(e => e.KeyConfigured).OrderBy(e => e.EngineKey).Select(e => e.EngineKey).ToListAsync(ct));

        var plan = await db.AisoRemediationPlans.SingleAsync(ct);
        Assert.Equal(RemediationStatus.Published, plan.Status);
        Assert.EndsWith("_AISO_remediation_plan.md", plan.ReportFilePath, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_root, plan.ReportFilePath!)));
        Assert.Contains("AISO İyileştirme Planı", plan.MarkdownContent, StringComparison.Ordinal);
        Assert.Contains("## Yapay zekâ motoruna göre görünürlük", plan.MarkdownContent, StringComparison.Ordinal);
        Assert.Contains("| ChatGPT | 50% | 0% |", plan.MarkdownContent, StringComparison.Ordinal);
        Assert.Contains("| Perplexity | 50% | 50% |", plan.MarkdownContent, StringComparison.Ordinal);

        var row = Assert.Single(alerts.Rows);
        Assert.Equal(log.OverallScore, row.OverallScore);
        Assert.Equal(plan.ReportFilePath, row.ReportPath);
    }

    private static HttpResponseMessage Html(string html) =>
        new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class StubAgent(string result) : IClaudeCliOrchestrator
    {
        public string LastPrompt { get; private set; } = string.Empty;

        public Task<ClaudeCliResult> RunAsync(string prompt, CancellationToken cancellationToken)
        {
            LastPrompt = prompt;
            return Task.FromResult(new ClaudeCliResult(true, 0, result, "stub-session", 1, 0m, string.Empty, TimeSpan.Zero, 1));
        }
    }

    private sealed class CapturingNotifications : IAisoNotificationService
    {
        public List<VisibilityAlertRow> Rows { get; } = [];

        public Task<bool> SendVisibilityAlertAsync(IReadOnlyList<VisibilityAlertRow> rows, CancellationToken cancellationToken)
        {
            Rows.AddRange(rows);
            return Task.FromResult(true);
        }
    }

    private sealed class SqliteFactory(SqliteConnection connection) : IDbContextFactory<AisoDbContext>
    {
        public AisoDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<AisoDbContext>().UseSqlite(connection).Options);
    }
}
