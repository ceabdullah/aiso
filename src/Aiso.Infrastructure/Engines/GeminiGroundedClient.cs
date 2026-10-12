using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aiso.Infrastructure.Engines;

/// <summary>
/// Gemini <c>generateContent</c> with Grounding with Google Search. Sources come from
/// <c>groundingMetadata.groundingChunks[].web</c>: <c>uri</c> is a Google redirect link and
/// <c>title</c> carries the source domain, which <see cref="OwnedDomainMatcher"/> understands.
/// </summary>
public sealed class GeminiGroundedClient(IHttpClientFactory httpClientFactory) : JsonEngineClientBase(httpClientFactory)
{
    public override AnswerEngineKind Kind => AnswerEngineKind.GeminiGrounded;

    public override HttpRequestMessage BuildRequest(AnswerEngineDefinition engine, string prompt)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = prompt }),
            }),
            ["tools"] = new JsonArray(new JsonObject { ["google_search"] = new JsonObject() }),
            ["generationConfig"] = new JsonObject { ["maxOutputTokens"] = engine.MaxOutputTokens },
        };
        var request = JsonPost($"{engine.ResolvedBaseUrl}/v1beta/models/{Uri.EscapeDataString(engine.Model)}:generateContent", body);
        request.Headers.Add("x-goog-api-key", engine.ApiKey);
        return request;
    }

    public override EngineAnswer ParseResponse(JsonElement root)
    {
        var candidate = Array(root, "candidates").FirstOrDefault();
        if (candidate.ValueKind != JsonValueKind.Object)
        {
            return EngineAnswer.Create([], []);
        }

        var texts = candidate.TryGetProperty("content", out var content)
            ? Array(content, "parts").Select(p => Str(p, "text") ?? string.Empty)
            : [];

        var citations = candidate.TryGetProperty("groundingMetadata", out var meta)
            ? Array(meta, "groundingChunks")
                .Where(c => c.TryGetProperty("web", out _))
                .Select(c => c.GetProperty("web"))
                .Where(w => Str(w, "uri") is not null)
                .Select(w => new EngineCitation(Str(w, "uri")!, Str(w, "title")))
            : [];

        return EngineAnswer.Create(texts, citations);
    }
}
