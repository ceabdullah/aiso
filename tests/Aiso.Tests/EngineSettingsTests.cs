using Aiso.Domain.Entities;
using Aiso.Infrastructure.Engines;
using Aiso.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aiso.Tests;

/// <summary>Admin on/off switches and model overrides, stored in AISO_ANSWER_ENGINE (SQLite).</summary>
public sealed class EngineSettingsTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly Factory _factory;

    public EngineSettingsTests()
    {
        _connection.Open();
        _factory = new Factory(_connection);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private static AnswerEngineOptions Config()
    {
        var o = new AnswerEngineOptions();
        o.Engines["chatgpt"] = new() { DisplayName = "ChatGPT", Kind = AnswerEngineKind.OpenAiResponses, ApiKey = "o", Model = "gpt-a" };
        o.Engines["perplexity"] = new() { DisplayName = "Perplexity", Kind = AnswerEngineKind.PerplexitySonar, ApiKey = "p", Model = "sonar" };
        o.Engines["claude"] = new() { DisplayName = "Claude", Kind = AnswerEngineKind.AnthropicWebSearch, ApiKey = "", Model = "claude-opus-5-5" };
        return o;
    }

    private AnswerEngineProbe Probe(AnswerEngineOptions options) =>
        new(Options.Create(options), new EfEngineSettingsStore(_factory, TimeProvider.System),
            [new OpenAiResponsesClient(new StubHttpFactory(_ => throw new InvalidOperationException())),
             new PerplexitySonarClient(new StubHttpFactory(_ => throw new InvalidOperationException())),
             new AnthropicEngineClient()],
            NullLogger<AnswerEngineProbe>.Instance);

    [Fact]
    public async Task Sync_registers_engines_with_key_status_and_keeps_admin_choices()
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = Probe(Config());
        await probe.SyncSettingsAsync(ct);

        await using (var db = await _factory.CreateDbContextAsync(ct))
        {
            var rows = await db.AnswerEngineSettings.OrderBy(e => e.EngineKey).ToListAsync(ct);
            Assert.Equal(["chatgpt", "claude", "perplexity"], rows.Select(r => r.EngineKey));
            Assert.False(rows.Single(r => r.EngineKey == "claude").KeyConfigured);
            Assert.Equal("gpt-a", rows.Single(r => r.EngineKey == "chatgpt").DefaultModel);

            // Administrator switches Perplexity off and overrides ChatGPT's model.
            rows.Single(r => r.EngineKey == "perplexity").IsEnabled = false;
            rows.Single(r => r.EngineKey == "chatgpt").ModelOverride = "gpt-b";
            await db.SaveChangesAsync(ct);
        }

        // A later sync (worker restart with a new default model) must not undo the admin's choices.
        var config = Config();
        config.Engines["chatgpt"].Model = "gpt-c";
        await Probe(config).SyncSettingsAsync(ct);

        var active = await Probe(config).GetActiveEnginesAsync(ct);

        var chatgpt = Assert.Single(active); // Perplexity off by admin, Claude has no key
        Assert.Equal("chatgpt", chatgpt.Key);
        Assert.Equal("gpt-b", chatgpt.Definition.Model);

        await using var check = await _factory.CreateDbContextAsync(ct);
        var perplexity = await check.AnswerEngineSettings.SingleAsync(e => e.EngineKey == "perplexity", ct);
        Assert.False(perplexity.IsEnabled);
        Assert.Equal("gpt-c", (await check.AnswerEngineSettings.SingleAsync(e => e.EngineKey == "chatgpt", ct)).DefaultModel);
    }

    [Fact]
    public async Task Switching_an_engine_back_on_makes_it_active_again()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = Config();
        config.Engines["perplexity"].Enabled = false; // starts off from configuration
        await Probe(config).SyncSettingsAsync(ct);
        Assert.DoesNotContain(await Probe(config).GetActiveEnginesAsync(ct), e => e.Key == "perplexity");

        await using (var db = await _factory.CreateDbContextAsync(ct))
        {
            (await db.AnswerEngineSettings.SingleAsync(e => e.EngineKey == "perplexity", ct)).IsEnabled = true;
            await db.SaveChangesAsync(ct);
        }

        Assert.Contains(await Probe(config).GetActiveEnginesAsync(ct), e => e.Key == "perplexity");
    }

    [Fact]
    public void Apply_uses_admin_search_limit_and_falls_back_to_configuration()
    {
        var configured = new AnswerEngineDefinition { Kind = AnswerEngineKind.AnthropicWebSearch, ApiKey = "k", Model = "m", MaxSearches = 5 };
        Assert.Equal(2, AnswerEngineProbe.Apply("claude", configured, new EngineOverride(true, null, 2)).MaxSearches);
        Assert.Equal(5, AnswerEngineProbe.Apply("claude", configured, new EngineOverride(true, null, null)).MaxSearches);
        Assert.Equal("m", AnswerEngineProbe.Apply("claude", configured, null).Model);
        Assert.Equal(5, configured.MaxSearches); // configuration object untouched
    }

    private sealed class Factory(SqliteConnection connection) : IDbContextFactory<AisoDbContext>
    {
        public AisoDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AisoDbContext>().UseSqlite(connection).Options);

        public Task<AisoDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
