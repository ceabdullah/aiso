using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aiso.Infrastructure.Engines;

/// <summary>
/// OpenAI Responses API with the hosted <c>web_search</c> tool. Answer text is in <c>output[].content[]</c>
/// items of type <c>output_text</c>; sources are their <c>url_citation</c> annotations.
/// </summary>
public sealed class OpenAiResponsesClient(IHttpClientFactory httpClientFactory) : JsonEngineClientBase(httpClientFactory)
{
    public override AnswerEngineKind Kind => AnswerEngineKind.OpenAiResponses;

    public override HttpRequestMessage BuildRequest(AnswerEngineDefinition engine, string prompt)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var body = new JsonObject
        {
            ["model"] = engine.Model,
            ["input"] = prompt,
            ["tools"] = new JsonArray(new JsonObject { ["type"] = "web_search" }),
            ["max_output_tokens"] = engine.MaxOutputTokens,
        };
        return JsonPost($"{engine.ResolvedBaseUrl}/v1/responses", body, engine.ApiKey);
    }

    public override EngineAnswer ParseResponse(JsonElement root)
    {
        var texts = new List<string>();
        var citations = new List<EngineCitation>();
        foreach (var item in Array(root, "output").Where(i => Str(i, "type") == "message"))
        {
            foreach (var part in Array(item, "content").Where(c => Str(c, "type") == "output_text"))
            {
                texts.Add(Str(part, "text") ?? string.Empty);
                citations.AddRange(Array(part, "annotations")
                    .Where(a => Str(a, "type") == "url_citation" && Str(a, "url") is not null)
                    .Select(a => new EngineCitation(Str(a, "url")!, Str(a, "title"))));
            }
        }

        return EngineAnswer.Create(texts, citations);
    }
}
