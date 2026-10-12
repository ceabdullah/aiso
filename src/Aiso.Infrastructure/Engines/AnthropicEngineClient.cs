using Aiso.Infrastructure.Llm;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Options;

namespace Aiso.Infrastructure.Engines;

/// <summary>
/// Claude engines via the official Anthropic SDK:
/// <list type="bullet">
/// <item><see cref="AnswerEngineKind.AnthropicWebSearch"/> — Claude with the server-side web search tool;
/// sources come from <c>web_search_result_location</c> citations on the text blocks.</item>
/// <item><see cref="AnswerEngineKind.AnthropicMessages"/> — no tools; used for the internal model behind the
/// LiteLLM gateway, falling back to <c>Aiso:Llm</c> for URL, key and model.</item>
/// </list>
/// </summary>
public sealed class AnthropicEngineClient(IOptions<LlmGatewayOptions> gateway) : IAnswerEngineClient
{
    // Not used for dispatch: this client serves both Anthropic kinds (see AnswerEngineClientRegistry).
    public AnswerEngineKind Kind => AnswerEngineKind.AnthropicWebSearch;

    public async Task<EngineAnswer> AskAsync(AnswerEngineDefinition engine, string prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var resolved = Resolve(engine, gateway.Value);

        using var client = new AnthropicClient
        {
            ApiKey = resolved.ApiKey ?? string.Empty,
            BaseUrl = resolved.ResolvedBaseUrl,
            Timeout = TimeSpan.FromSeconds(engine.TimeoutSeconds),
        };

        var response = await client.Messages.Create(BuildParams(resolved, prompt), cancellationToken);
        return Parse(response);
    }

    /// <summary>Applies the gateway fallback for the internal engine. Public for unit tests.</summary>
    public static AnswerEngineDefinition Resolve(AnswerEngineDefinition engine, LlmGatewayOptions gateway)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(gateway);
        if (engine.Kind != AnswerEngineKind.AnthropicMessages)
        {
            return engine;
        }

        return new AnswerEngineDefinition
        {
            DisplayName = engine.DisplayName,
            Kind = engine.Kind,
            Enabled = engine.Enabled,
            BaseUrl = string.IsNullOrWhiteSpace(engine.BaseUrl) ? gateway.BaseUrl : engine.BaseUrl,
            ApiKey = string.IsNullOrWhiteSpace(engine.ApiKey) ? gateway.ApiKey : engine.ApiKey,
            Model = string.IsNullOrWhiteSpace(engine.Model) ? gateway.Model : engine.Model,
            MaxOutputTokens = engine.MaxOutputTokens,
            MaxSearches = engine.MaxSearches,
            TimeoutSeconds = engine.TimeoutSeconds,
        };
    }

    public static MessageCreateParams BuildParams(AnswerEngineDefinition engine, string prompt)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var parameters = new MessageCreateParams
        {
            Model = engine.Model,
            MaxTokens = engine.MaxOutputTokens,
            Messages = [new() { Role = Role.User, Content = prompt }],
        };

        if (engine.Kind == AnswerEngineKind.AnthropicWebSearch)
        {
            parameters = parameters with
            {
                Tools = [new ToolUnion(new WebSearchTool20260209 { MaxUses = engine.MaxSearches })],
            };
        }

        return parameters;
    }

    public static EngineAnswer Parse(Message response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var texts = new List<string>();
        var citations = new List<EngineCitation>();

        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var text))
            {
                texts.Add(text.Text);
                foreach (var citation in text.Citations ?? [])
                {
                    if (citation.TryPickCitationsWebSearchResultLocation(out var web))
                    {
                        citations.Add(new EngineCitation(web.Url, web.Title));
                    }
                }
            }
        }

        // A pause_turn (long server-side search loop) may end before cited text is written; the
        // partial answer is still scored, and its uncited text can still count as a brand mention.
        return EngineAnswer.Create(texts, citations);
    }
}
