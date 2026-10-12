using System.Diagnostics;
using System.Text.Json;
using Aiso.Domain.Entities;
using Aiso.Infrastructure.Llm;
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
    long LatencyMs)
{
    public bool MentionsBrand => MatchedKeywords.Count > 0;
    public bool CitesOwnedDomain => OwnedCitations.Count > 0;
    public bool IsVisible => MentionsBrand || CitesOwnedDomain;
    public bool Succeeded => Error is null;
}

/// <summary>
/// Generative-engine visibility probe. Asks every active test prompt on every configured answer engine
/// (engines in parallel, prompts one after another per engine to respect vendor rate limits) and records
/// whether each answer names the brand or cites one of its domains.
/// </summary>
public sealed class AnswerEngineProbe(
    IOptions<AnswerEngineOptions> options,
    IOptions<LlmGatewayOptions> gateway,
    IEnumerable<IAnswerEngineClient> clients,
    ILogger<AnswerEngineProbe> logger)
{
    public IReadOnlyList<ActiveEngine> GetActiveEngines()
    {
        var byKind = clients.ToList();
        var active = new List<ActiveEngine>();
        foreach (var (key, definition) in options.Value.Engines.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(definition.DisplayName))
            {
                definition.DisplayName = key;
            }

            var client = byKind.FirstOrDefault(c => Serves(c, definition.Kind));
            if (client is null)
            {
                logger.LogWarning("No client for engine {Engine} of kind {Kind}.", key, definition.Kind);
            }
            else if (AnthropicEngineClient.Resolve(definition, gateway.Value).IsUsable)
            {
                active.Add(new ActiveEngine(key, definition, client));
            }
            else if (definition.Enabled)
            {
                logger.LogInformation("Engine {Engine} is enabled but has no API key or model; skipping.", key);
            }
        }

        return active;
    }

    public async Task<IReadOnlyList<EngineProbeOutcome>> ProbeAsync(
        TargetProfile target, IReadOnlyCollection<AutomatedTestPrompt> prompts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(prompts);

        var engines = GetActiveEngines();
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
                    results.Add(await AskOneAsync(target, engine, prompt, cancellationToken));
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
            return Score(target, engine, prompt, answer, null, sw.ElapsedMilliseconds);
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
            latencyMs);
    }

    private static bool Serves(IAnswerEngineClient client, AnswerEngineKind kind) =>
        client.Kind == kind || (client is AnthropicEngineClient && kind is AnswerEngineKind.AnthropicMessages or AnswerEngineKind.AnthropicWebSearch);
}
