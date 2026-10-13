namespace Aiso.Infrastructure.Engines;

public enum AnswerEngineKind
{
    /// <summary>Claude with the server-side web search tool.</summary>
    AnthropicWebSearch = 1,

    /// <summary>OpenAI Responses API with the <c>web_search</c> tool (ChatGPT-style answers).</summary>
    OpenAiResponses = 2,

    /// <summary>Gemini <c>generateContent</c> with Grounding with Google Search.</summary>
    GeminiGrounded = 3,

    /// <summary>Perplexity Sonar chat completions (always web-grounded).</summary>
    PerplexitySonar = 4,
}

/// <summary>
/// One external AI answer engine to probe. Bound from <c>Aiso:Engines:{key}</c>.
/// The internal model behind the LiteLLM gateway is not an answer engine: it only runs <c>claude.exe</c>.
/// </summary>
public sealed class AnswerEngineDefinition
{
    public string DisplayName { get; set; } = string.Empty;

    public AnswerEngineKind Kind { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// API root. Defaults to the vendor's public endpoint; point it at a LiteLLM pass-through route to
    /// keep all traffic on the corporate gateway.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>Secret. Supply via environment, e.g. <c>Aiso__Engines__chatgpt__ApiKey</c>.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Vendor model ID. Model line-ups change often; confirm against the vendor's model list.</summary>
    public string Model { get; set; } = string.Empty;

    public int MaxOutputTokens { get; set; } = 4096;

    /// <summary>Upper bound on web searches per answer, where the engine supports it.</summary>
    public int MaxSearches { get; set; } = 5;

    public int TimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// Where the engine should search from, as a customer's device would. Set per run from System Settings;
    /// engines that have no location option (Gemini's Google Search grounding) ignore it.
    /// </summary>
    public SearchLocation? Location { get; set; }

    public string ResolvedBaseUrl => (string.IsNullOrWhiteSpace(BaseUrl) ? DefaultBaseUrl(Kind) : BaseUrl).TrimEnd('/');

    public bool IsUsable => Enabled && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(Model)
                            && !string.IsNullOrWhiteSpace(ResolvedBaseUrl);

    public static string DefaultBaseUrl(AnswerEngineKind kind) => kind switch
    {
        AnswerEngineKind.AnthropicWebSearch => "https://api.anthropic.com",
        AnswerEngineKind.OpenAiResponses => "https://api.openai.com",
        AnswerEngineKind.GeminiGrounded => "https://generativelanguage.googleapis.com",
        AnswerEngineKind.PerplexitySonar => "https://api.perplexity.ai",
        _ => string.Empty,
    };
}

/// <summary>Bound from the <c>Aiso</c> section; only <see cref="Engines"/> is read.</summary>
public sealed class AnswerEngineOptions
{
    public const string SectionName = "Aiso";

    /// <summary>Key = stable engine key stored with every result (<c>chatgpt</c>, <c>gemini</c>, …).</summary>
    public Dictionary<string, AnswerEngineDefinition> Engines { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Maximum engines queried at the same time. Prompts run one after another per engine.</summary>
    public int MaxParallelEngines { get; set; } = 5;
}
