using Aiso.Domain.Entities;
using Aiso.Infrastructure.Engines;
using Aiso.Infrastructure.Persistence;
using Aiso.Infrastructure.Security;
using Aiso.Infrastructure.Settings;
using Aiso.Web.Security;
using Aiso.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
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
        new(Options.Create(options), Store(),
            [new OpenAiResponsesClient(new StubHttpFactory(_ => throw new InvalidOperationException())),
             new PerplexitySonarClient(new StubHttpFactory(_ => throw new InvalidOperationException())),
             new AnthropicEngineClient()],
            new SystemSettingsService(_factory),             NullLogger<AnswerEngineProbe>.Instance);

    private readonly EngineApiKeyProtector _protector = new(new EphemeralDataProtectionProvider());

    private EfEngineSettingsStore Store() => new(_factory, _protector, TimeProvider.System, NullLogger<EfEngineSettingsStore>.Instance);

    private DashboardService Dashboard() => new(_factory, new SystemSettingsService(_factory),
        new RoleMappingProvider(_factory, new MemoryCache(new MemoryCacheOptions())), _protector, NullLogger<DashboardService>.Instance);

    [Fact]
    public async Task Dashboard_key_is_stored_encrypted_used_by_the_probe_and_removable()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = Config(); // claude has no environment key
        await Probe(config).SyncSettingsAsync(ct);
        Assert.DoesNotContain(await Probe(config).GetActiveEnginesAsync(ct), e => e.Key == "claude");

        var dashboard = Dashboard();
        var engines = await dashboard.GetEnginesAsync(ct);
        engines.Single(e => e.EngineKey == "claude").NewApiKey = "  sk-ant-test-1234ABCD  ";
        await dashboard.SaveEnginesAsync(engines, @"CORPdmin", ct);

        await using (var db = await _factory.CreateDbContextAsync(ct))
        {
            var row = await db.AnswerEngineSettings.SingleAsync(e => e.EngineKey == "claude", ct);
            Assert.DoesNotContain("sk-ant-test", row.ApiKeyCiphertext, StringComparison.Ordinal); // never plaintext in the database
            Assert.Equal("ABCD", row.ApiKeyHint);
            Assert.Equal(@"CORPdmin", row.ApiKeyUpdatedBy);
        }

        var claude = Assert.Single(await Probe(config).GetActiveEnginesAsync(ct), e => e.Key == "claude");
        Assert.Equal("sk-ant-test-1234ABCD", claude.Definition.ApiKey); // trimmed and decrypted for the worker

        var reloaded = await dashboard.GetEnginesAsync(ct);
        var card = reloaded.Single(e => e.EngineKey == "claude");
        Assert.True(card.HasUiKey);
        Assert.Null(card.NewApiKey); // the UI never receives the key back

        card.RemoveApiKey = true;
        await dashboard.SaveEnginesAsync(reloaded, @"CORPdmin", ct);
        Assert.DoesNotContain(await Probe(config).GetActiveEnginesAsync(ct), e => e.Key == "claude");
    }

    [Fact]
    public async Task Environment_key_wins_over_dashboard_key()
    {
        var ct = TestContext.Current.CancellationToken;
        await Probe(Config()).SyncSettingsAsync(ct);
        var dashboard = Dashboard();
        var engines = await dashboard.GetEnginesAsync(ct);
        engines.Single(e => e.EngineKey == "chatgpt").NewApiKey = "sk-from-the-dashboard";
        await dashboard.SaveEnginesAsync(engines, "admin", ct);

        var chatgpt = Assert.Single(await Probe(Config()).GetActiveEnginesAsync(ct), e => e.Key == "chatgpt");
        Assert.Equal("o", chatgpt.Definition.ApiKey);
    }

    [Fact]
    public async Task Unreadable_ciphertext_disables_the_engine_instead_of_failing_the_run()
    {
        var ct = TestContext.Current.CancellationToken;
        await Probe(Config()).SyncSettingsAsync(ct);
        await using (var db = await _factory.CreateDbContextAsync(ct))
        {
            // Encrypted with a different key ring, e.g. after the key folder was lost.
            var foreign = new EngineApiKeyProtector(new EphemeralDataProtectionProvider()).Protect("sk-lost");
            (await db.AnswerEngineSettings.SingleAsync(e => e.EngineKey == "claude", ct)).ApiKeyCiphertext = foreign;
            await db.SaveChangesAsync(ct);
        }

        var active = await Probe(Config()).GetActiveEnginesAsync(ct);
        Assert.DoesNotContain(active, e => e.Key == "claude");
        Assert.Contains(active, e => e.Key == "chatgpt");
    }

    [Theory]
    [InlineData("short", false)]
    [InlineData("has space inside", false)]
    [InlineData("  sk-valid-key-0000  ", true)]
    public void Validate_rejects_malformed_keys(string key, bool valid) =>
        Assert.Equal(valid, EngineApiKeyProtector.Validate(key, out _) is null);

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
