using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aiso.Infrastructure.Engines;

/// <summary>
/// Perplexity Sonar (OpenAI-compatible chat completions; every answer is web-grounded). Sources come from
/// <c>search_results[]</c> (url, title), falling back to the plain <c>citations[]</c> URL list.
/// </summary>
public sealed class PerplexitySonarClient(IHttpClientFactory httpClientFactory) : JsonEngineClientBase(httpClientFactory)
{
    public override AnswerEngineKind Kind => AnswerEngineKind.PerplexitySonar;

    public override HttpRequestMessage BuildRequest(AnswerEngineDefinition engine, string prompt)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var body = new JsonObject
        {
            ["model"] = engine.Model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
            ["max_tokens"] = engine.MaxOutputTokens,
        };
        if (engine.Location is { } location)
        {
            var userLocation = new JsonObject { ["country"] = location.Country };
            if (location.Region is not null) userLocation["region"] = location.Region;
            if (location.City is not null) userLocation["city"] = location.City;
            body["web_search_options"] = new JsonObject { ["user_location"] = userLocation };
        }

        return JsonPost($"{engine.ResolvedBaseUrl}/chat/completions", body, engine.ApiKey);
    }

    public override EngineAnswer ParseResponse(JsonElement root)
    {
        var texts = Array(root, "choices").Take(1)
            .Select(c => c.TryGetProperty("message", out var m) ? Str(m, "content") ?? string.Empty : string.Empty);

        var citations = Array(root, "search_results")
            .Where(r => Str(r, "url") is not null)
            .Select(r => new EngineCitation(Str(r, "url")!, Str(r, "title")))
            .ToList();

        if (citations.Count == 0)
        {
            citations = Array(root, "citations")
                .Where(c => c.ValueKind == JsonValueKind.String)
                .Select(c => new EngineCitation(c.GetString()!, null))
                .ToList();
        }

        return EngineAnswer.Create(texts, citations);
    }
}
