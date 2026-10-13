using Anthropic;
using Anthropic.Models.Messages;

namespace Aiso.Infrastructure.Engines;

/// <summary>
/// Claude with the server-side web search tool, via the official Anthropic SDK. Sources come from
/// <c>web_search_result_location</c> citations on the text blocks.
/// </summary>
public sealed class AnthropicEngineClient : IAnswerEngineClient
{
    public AnswerEngineKind Kind => AnswerEngineKind.AnthropicWebSearch;

    public async Task<EngineAnswer> AskAsync(AnswerEngineDefinition engine, string prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(engine);

        using var client = new AnthropicClient
        {
            ApiKey = engine.ApiKey ?? string.Empty,
            BaseUrl = engine.ResolvedBaseUrl,
            Timeout = TimeSpan.FromSeconds(engine.TimeoutSeconds),
        };

        var response = await client.Messages.Create(BuildParams(engine, prompt), cancellationToken);
        return Parse(response);
    }

    public static MessageCreateParams BuildParams(AnswerEngineDefinition engine, string prompt)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return new MessageCreateParams
        {
            Model = engine.Model,
            MaxTokens = engine.MaxOutputTokens,
            Messages = [new() { Role = Role.User, Content = prompt }],
            Tools = [new ToolUnion(new WebSearchTool20260209
            {
                MaxUses = engine.MaxSearches,
                UserLocation = engine.Location is { } l
                    ? new UserLocation { Country = l.Country, Region = l.Region, City = l.City, Timezone = l.Timezone }
                    : null,
            })],
        };
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
