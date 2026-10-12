namespace Aiso.Infrastructure.Llm;

/// <summary>
/// Corporate LiteLLM gateway. The gateway exposes an Anthropic-compatible <c>/v1/messages</c>
/// endpoint; <see cref="Model"/> is a gateway alias that LiteLLM routes to the internal model.
/// Bound from <c>Aiso:Llm</c>. <see cref="ApiKey"/> must come from a secret store / environment
/// variable (<c>Aiso__Llm__ApiKey</c>) — never from a committed appsettings file.
/// </summary>
public sealed class LlmGatewayOptions
{
    public const string SectionName = "Aiso:Llm";

    public string BaseUrl { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Gateway model alias (e.g. <c>claude-3-5-sonnet-20241022</c> mapped to the internal model by LiteLLM).</summary>
    public string Model { get; set; } = "claude-3-5-sonnet-20241022";

    public string AnthropicVersion { get; set; } = "2023-06-01";

    public int MaxTokens { get; set; } = 1024;

    public int TimeoutSeconds { get; set; } = 120;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);
}
