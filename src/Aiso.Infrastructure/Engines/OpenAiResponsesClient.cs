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
        var webSearch = new JsonObject { ["type"] = "web_search" };
        if (engine.Location is { } location)
        {
            webSearch["user_location"] = UserLocation(location);
        }

        var body = new JsonObject
        {
            ["model"] = engine.Model,
            ["input"] = prompt,
            ["tools"] = new JsonArray(webSearch),
            ["max_output_tokens"] = engine.MaxOutputTokens,
        };
        return JsonPost($"{engine.ResolvedBaseUrl}/v1/responses", body, engine.ApiKey);
    }

    private static JsonObject UserLocation(SearchLocation location)
    {
        var json = new JsonObject { ["type"] = "approximate", ["country"] = location.Country };
        if (location.Region is not null) json["region"] = location.Region;
        if (location.City is not null) json["city"] = location.City;
        if (location.Timezone is not null) json["timezone"] = location.Timezone;
        return json;
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
