using System.Diagnostics;
using System.Text.Json;
using Aiso.Domain;
using Aiso.Domain.Entities;
using Aiso.Infrastructure.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aiso.Infrastructure.Engines;

/// <summary>A configured engine ready to be asked.</summary>
public sealed record ActiveEngine(string Key, AnswerEngineDefinition Definition, IAnswerEngineClient Client);

/// <summary>One prompt answered by one engine, scored for brand visibility.</summary>
public sealed record EngineProbeOutcome(
    string EngineKey,
    string EngineName,
    Guid PromptId,
    string LanguageCode,
    string PromptText,
    string Answer,
    IReadOnlyList<EngineCitation> Citations,
    IReadOnlyList<EngineCitation> OwnedCitations,
    IReadOnlyList<string> MatchedKeywords,
    string? Error,
    long LatencyMs,
    bool SearchesWeb = true)
{
    public bool MentionsBrand => MatchedKeywords.Count > 0;
    public bool CitesOwnedDomain => OwnedCitations.Count > 0;
    public bool IsVisible => MentionsBrand || CitesOwnedDomain;
    public bool Succeeded => Error is null;
}

/// <summary>
/// Generative-engine visibility probe. Asks every active test prompt on every configured external answer engine
/// (engines in parallel, prompts one after another per engine to respect vendor rate limits) and records
/// whether each answer names the brand or cites one of its domains.
/// </summary>
public sealed class AnswerEngineProbe(
    IOptions<AnswerEngineOptions> options,
    IEngineSettingsStore settingsStore,
    IEnumerable<IAnswerEngineClient> clients,
    ISystemSettingsService settings,
    ILogger<AnswerEngineProbe> logger)
{
    /// <summary>Registers the configured engines with the admin store (key status, default model).</summary>
    public Task SyncSettingsAsync(CancellationToken cancellationToken) =>
        settingsStore.SyncAsync(options.Value.Engines, cancellationToken);

    /// <summary>
    /// Engines to ask in this run: configured, switched on by an administrator, and holding an API key and model.
    /// Admin model and search-limit overrides are applied to a copy; configuration is never mutated.
    /// </summary>
    public async Task<IReadOnlyList<ActiveEngine>> GetActiveEnginesAsync(CancellationToken cancellationToken)
    {
        var overrides = await settingsStore.GetOverridesAsync(cancellationToken);
        var location = await GetSearchLocationAsync(cancellationToken);
        var byKind = clients.ToList();
        var active = new List<ActiveEngine>();

        var definitions = ResolveDefinitions(overrides);
        foreach (var (key, definition) in definitions)
        {
            definition.Location = location;
            var client = byKind.FirstOrDefault(c => c.Kind == definition.Kind);
            if (client is null)
            {
                logger.LogWarning("No client for engine {Engine} of kind {Kind}.", key, definition.Kind);
            }
            else if (!definition.Enabled)
            {
                logger.LogInformation("Engine {Engine} is switched off.", key);
            }
            else if (definition.IsUsable)
            {
                active.Add(new ActiveEngine(key, definition, client));
            }
            else
            {
                logger.LogInformation("Engine {Engine} is switched on but has no API key or model; skipping.", key);
            }
        }

        return active;
    }

    /// <summary>
    /// Configured engines with admin overrides applied, ordered by key. Plain chat engines (simulated) without a key of
    /// their own share the key of another plain chat engine on the same endpoint, so one NVIDIA key serves every
    /// NVIDIA-hosted model.
    /// </summary>
    public IReadOnlyList<(string Key, AnswerEngineDefinition Definition)> ResolveDefinitions(IReadOnlyDictionary<string, EngineOverride> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        var definitions = options.Value.Engines.OrderBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => (e.Key, Definition: Apply(e.Key, e.Value, overrides.GetValueOrDefault(e.Key))))
            .ToList();

        foreach (var (_, definition) in definitions.Where(d => !d.Definition.SearchesWeb && string.IsNullOrWhiteSpace(d.Definition.ApiKey)))
        {
            definition.ApiKey = definitions
                .Where(d => !d.Definition.SearchesWeb && !string.IsNullOrWhiteSpace(d.Definition.ApiKey)
                            && string.Equals(d.Definition.ResolvedBaseUrl, definition.ResolvedBaseUrl, StringComparison.OrdinalIgnoreCase))
                .Select(d => d.Definition.ApiKey)
                .FirstOrDefault();
        }

        return definitions;
    }

    /// <summary>The search location from System Settings; read every run so dashboard changes apply without a restart.</summary>
    public async Task<SearchLocation?> GetSearchLocationAsync(CancellationToken cancellationToken) =>
        SearchLocation.Create(
            await settings.GetStringAsync(SettingKeys.SearchCountry, cancellationToken),
            await settings.GetStringAsync(SettingKeys.SearchRegion, cancellationToken),
            await settings.GetStringAsync(SettingKeys.SearchCity, cancellationToken),
            await settings.GetStringAsync(SettingKeys.SearchTimezone, cancellationToken));

    internal static AnswerEngineDefinition Apply(string key, AnswerEngineDefinition configured, EngineOverride? admin) => new()
    {
        DisplayName = string.IsNullOrWhiteSpace(configured.DisplayName) ? key : configured.DisplayName,
        Kind = configured.Kind,
        Enabled = admin?.IsEnabled ?? configured.Enabled,
        BaseUrl = configured.BaseUrl,
        // A key in the worker's environment / secret store wins; otherwise the key entered in the dashboard.
        ApiKey = !string.IsNullOrWhiteSpace(configured.ApiKey) ? configured.ApiKey : admin?.ApiKey,
        Model = admin?.Model ?? configured.Model,
        MaxOutputTokens = configured.MaxOutputTokens,
        MaxSearches = admin?.MaxSearches is > 0 and var n ? n : configured.MaxSearches,
        TimeoutSeconds = configured.TimeoutSeconds,
        EnableThinking = configured.EnableThinking,
    };

    public Task<IReadOnlyList<EngineProbeOutcome>> ProbeAsync(
        TargetProfile target, IReadOnlyCollection<AutomatedTestPrompt> prompts, IReadOnlyList<ActiveEngine> engines,
        CancellationToken cancellationToken) =>
        ProbeAsync(target, prompts, engines, null, cancellationToken);

    /// <param name="onAnswer">Called after each answer, e.g. to report progress. Engines run in parallel, so it can be called concurrently.</param>
    public async Task<IReadOnlyList<EngineProbeOutcome>> ProbeAsync(
        TargetProfile target, IReadOnlyCollection<AutomatedTestPrompt> prompts, IReadOnlyList<ActiveEngine> engines,
        Func<EngineProbeOutcome, Task>? onAnswer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(engines);

        if (engines.Count == 0 || prompts.Count == 0)
        {
            return [];
        }

        using var gate = new SemaphoreSlim(Math.Max(1, options.Value.MaxParallelEngines));
        var perEngine = await Task.WhenAll(engines.Select(async engine =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var results = new List<EngineProbeOutcome>();
                foreach (var prompt in prompts)
                {
                    var outcome = await AskOneAsync(target, engine, prompt, cancellationToken);
                    results.Add(outcome);
                    if (onAnswer is not null)
                    {
                        await onAnswer(outcome);
                    }
                }

                return results;
            }
            finally
            {
                gate.Release();
            }
        }));

        return perEngine.SelectMany(r => r).ToList();
    }

    private async Task<EngineProbeOutcome> AskOneAsync(TargetProfile target, ActiveEngine engine, AutomatedTestPrompt prompt, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var answer = await engine.Client.AskAsync(engine.Definition, prompt.PromptText, ct);
            // An empty answer is a failure, not "brand not mentioned" (e.g. a reasoning model spent its whole token budget thinking).
            var error = string.IsNullOrWhiteSpace(answer.Text) && answer.Citations.Count == 0
                ? $"{engine.Definition.DisplayName} returned an empty answer; it may have used its token budget on reasoning."
                : null;
            return Score(target, engine, prompt, answer, error, sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException
                                   || ex.GetType().Namespace?.StartsWith("Anthropic", StringComparison.Ordinal) == true)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            logger.LogWarning(ex, "Engine {Engine} failed for target {Target}, prompt {PromptId}.", engine.Key, target.Code, prompt.Id);
            return Score(target, engine, prompt, new EngineAnswer(string.Empty, []), ex.Message, sw.ElapsedMilliseconds);
        }
    }

    public static EngineProbeOutcome Score(
        TargetProfile target, ActiveEngine engine, AutomatedTestPrompt prompt, EngineAnswer answer, string? error, long latencyMs)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(answer);

        return new EngineProbeOutcome(
            engine.Key,
            engine.Definition.DisplayName,
            prompt.Id,
            prompt.LanguageCode,
            prompt.PromptText,
            answer.Text,
            answer.Citations,
            OwnedDomainMatcher.FindOwned(answer.Citations, target.GetOwnedDomains()),
            BrandMatcher.MatchKeywords(answer.Text, target.GetBrandKeywords(), prompt.LanguageCode),
            error,
            latencyMs,
            engine.Definition.SearchesWeb);
    }
}
