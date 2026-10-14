using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aiso.Infrastructure.Engines;

/// <summary>
/// Plain OpenAI-compatible chat completions (NVIDIA build.nvidia.com by default, or any compatible provider via
/// <see cref="AnswerEngineDefinition.BaseUrl"/>). There is no web search: the answer comes from the model's training
/// data and cites nothing, so these engines simulate answer engines for demos and don't count toward the scores.
/// </summary>
public sealed class OpenAiCompatibleChatClient(IHttpClientFactory httpClientFactory) : JsonEngineClientBase(httpClientFactory)
{
    public override AnswerEngineKind Kind => AnswerEngineKind.OpenAiCompatibleChat;

    public override HttpRequestMessage BuildRequest(AnswerEngineDefinition engine, string prompt)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var body = new JsonObject
        {
            ["model"] = engine.Model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
            ["max_tokens"] = engine.MaxOutputTokens,
            ["stream"] = false,
        };
        if (engine.EnableThinking is { } thinking)
        {
            body["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = thinking };
        }

        return JsonPost($"{engine.ResolvedBaseUrl}/chat/completions", body, engine.ApiKey);
    }

    public override EngineAnswer ParseResponse(JsonElement root)
    {
        var texts = Array(root, "choices").Take(1)
            .Select(c => c.TryGetProperty("message", out var m) ? Str(m, "content") ?? string.Empty : string.Empty);
        return EngineAnswer.Create(texts, []);
    }
}
