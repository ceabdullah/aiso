using System.Net;
using System.Text;
using Aiso.Domain.Entities;
using Aiso.Domain.Enums;
using Aiso.Infrastructure.Llm;
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
                BrandKeywords = "TEB,Türk Ekonomi Bankası", PrimaryLanguageCode = "tr-TR",
            };
            target.TestPrompts.Add(new AutomatedTestPrompt { PromptText = "KOBİ kredisi için en iyi banka?", LanguageCode = "tr-TR" });
            target.TestPrompts.Add(new AutomatedTestPrompt { PromptText = "Best SME bank in Turkey?", LanguageCode = "en-US" });
            seed.TargetProfiles.Add(target);
            await seed.SaveChangesAsync(ct);
        }

        var workspace = Options.Create(new WorkspaceOptions { RootPath = _root });
        var llm = Options.Create(new LlmGatewayOptions { BaseUrl = "https://gateway.test", ApiKey = "k" });
        var answers = new Queue<string>(["TEB ve Garanti öne çıkıyor.", "Akbank is popular."]);

        var scraper = new NativeHtmlScraperService(
            new HttpClient(new StubHandler(_ => Html("<html lang='tr'><head><title>TEB Bireysel Bankacılık</title></head><body><h1>TEB</h1></body></html>"))),
            Options.Create(new ScraperOptions()), NullLogger<NativeHtmlScraperService>.Instance);
        var probe = new PromptVisibilityProbe(
            new HttpClient(new StubHandler(_ => Json($$"""{"content":[{"type":"text","text":"{{answers.Dequeue()}}"}]}"""))) { BaseAddress = new Uri("https://gateway.test/") },
            llm, NullLogger<PromptVisibilityProbe>.Instance);
        var agent = new StubAgent("""{"summary":"Özet.","items":[{"title":"FAQPage ekleyin","priority":"high","category":"structured-data"}]}""");
        var alerts = new CapturingNotifications();

        var pipeline = new AuditPipelineService(factory, scraper, probe, agent,
            new AisoMarkdownExporter(workspace, NullLogger<AisoMarkdownExporter>.Instance), alerts,
            workspace, llm, Options.Create(new ClaudeCliOptions()), NullLogger<AuditPipelineService>.Instance);

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
        Assert.Equal(2, log.PromptsEvaluated);
        Assert.Equal(1, log.PromptsMentioningBrand);
        Assert.Equal(50m, log.PromptVisibilityScore);
        Assert.Equal(Math.Round(0.4m * log.OnPageScore + 0.6m * 50m, 2), log.OverallScore);
        Assert.Equal(2, AuditMetricsDocument.TryParse(log.MetricsJson)!.Prompts.Count);

        var plan = await db.AisoRemediationPlans.SingleAsync(ct);
        Assert.Equal(RemediationStatus.Published, plan.Status);
        Assert.EndsWith("_AISO_remediation_plan.md", plan.ReportFilePath, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_root, plan.ReportFilePath!)));
        Assert.Contains("AISO İyileştirme Planı", plan.MarkdownContent, StringComparison.Ordinal);

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
