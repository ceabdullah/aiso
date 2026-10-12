using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aiso.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aiso.Infrastructure.Llm;

public sealed record PromptProbeResult(
    Guid PromptId,
    string LanguageCode,
    string PromptText,
    string Answer,
    bool MentionsBrand,
    IReadOnlyList<string> MatchedKeywords,
    string? Error);

/// <summary>
/// Generative-engine visibility probe: replays each <see cref="AutomatedTestPrompt"/> against the
/// LLM gateway and records whether the answer surfaces the target's brand keywords.
/// </summary>
public sealed class PromptVisibilityProbe(
    HttpClient httpClient,
    IOptions<LlmGatewayOptions> options,
    ILogger<PromptVisibilityProbe> logger)
{
    public const string HttpClientName = "aiso-llm-gateway";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<PromptProbeResult>> ProbeAsync(
        TargetProfile target,
        IEnumerable<AutomatedTestPrompt> prompts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(prompts);

        var keywords = target.GetBrandKeywords();
        var results = new List<PromptProbeResult>();

        foreach (var prompt in prompts)
        {
            try
            {
                var answer = await AskAsync(prompt.PromptText, prompt.LanguageCode, cancellationToken);
                var matched = MatchKeywords(answer, keywords, prompt.LanguageCode);
                results.Add(new PromptProbeResult(prompt.Id, prompt.LanguageCode, prompt.PromptText, answer, matched.Count > 0, matched, null));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Prompt probe failed for target {Target}, prompt {PromptId}.", target.Code, prompt.Id);
                results.Add(new PromptProbeResult(prompt.Id, prompt.LanguageCode, prompt.PromptText, string.Empty, false, [], ex.Message));
            }
        }

        return results;
    }

    /// <summary>Case- and culture-aware keyword match (Turkish dotted/dotless I handled via the prompt's culture).</summary>
    internal static List<string> MatchKeywords(string answer, IReadOnlyList<string> keywords, string languageCode)
    {
        CultureInfo culture;
        try
        {
            culture = CultureInfo.GetCultureInfo(languageCode);
        }
        catch (CultureNotFoundException)
        {
            culture = CultureInfo.InvariantCulture;
        }

        var compare = culture.CompareInfo;
        return keywords
            .Where(k => compare.IndexOf(answer, k, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0)
            .ToList();
    }

    private async Task<string> AskAsync(string promptText, string languageCode, CancellationToken cancellationToken)
    {
        var opts = options.Value;
        var request = new MessagesRequest(
            opts.Model,
            opts.MaxTokens,
            $"You are a helpful financial assistant. Answer in the language identified by '{languageCode}'. " +
            "Name specific institutions and products where relevant.",
            [new MessageContent("user", promptText)]);

        using var response = await httpClient.PostAsJsonAsync("v1/messages", request, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<MessagesResponse>(JsonOptions, cancellationToken)
            ?? throw new JsonException("Empty response from LLM gateway.");

        return string.Join('\n', body.Content.Where(c => c.Type == "text").Select(c => c.Text));
    }

    private sealed record MessagesRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("system")] string System,
        [property: JsonPropertyName("messages")] IReadOnlyList<MessageContent> Messages);

    private sealed record MessageContent(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record MessagesResponse(
        [property: JsonPropertyName("content")] IReadOnlyList<ContentBlock> Content);

    private sealed record ContentBlock(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string? Text);
}
