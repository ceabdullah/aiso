using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aiso.Domain.Entities;
using Aiso.Infrastructure.Engines;
using Aiso.Infrastructure.Scraping;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aiso.Infrastructure.Content;

/// <summary>Which vision model writes alt text. Bound from <c>Aiso:Vision</c>.</summary>
public sealed class VisionOptions
{
    public const string SectionName = "Aiso:Vision";

    /// <summary>Answer engine whose endpoint and API key are used (NVIDIA's OpenAI-compatible API by default).</summary>
    public string Engine { get; set; } = "nvidia";

    public string Model { get; set; } = "meta/llama-3.2-11b-vision-instruct";

    /// <summary>Tried in order when a model is gone (HTTP 404 or 410).</summary>
    public List<string> FallbackModels { get; set; } = [];

    public int MaxOutputTokens { get; set; } = 200;

    /// <summary>Time limit for one model's answer; then the next model is tried.</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Images up to this size are sent inline; larger ones by address, for the model's host to fetch.</summary>
    public int MaxInlineBytes { get; set; } = 180_000;

    /// <summary>Images larger than this aren't described.</summary>
    public int MaxImageBytes { get; set; } = 8_000_000;

    public IReadOnlyList<string> Models =>
        new[] { Model }.Concat(FallbackModels).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>The vision model's answer: alt text (or <see cref="ContentLoop.Decorative"/>), or why there is none.</summary>
public sealed record AltTextResult(string? AltText, string? Error);

/// <summary>
/// Describes an image from the brand's site with a vision model on an OpenAI-compatible API, using the API key of the
/// answer engine named in <see cref="VisionOptions.Engine"/>. The image is fetched by AISO with its crawler identity.
/// </summary>
public sealed class AltTextWriter(
    IOptions<VisionOptions> visionOptions,
    IOptions<ScraperOptions> scraperOptions,
    IEngineSettingsStore settingsStore,
    AnswerEngineProbe probe,
    IHttpClientFactory httpClientFactory,
    ILogger<AltTextWriter> logger)
{
    public async Task<AltTextResult> DescribeAsync(ContentItem item, string brand, string? pageTitle, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        var options = visionOptions.Value;
        var engine = probe.ResolveDefinitions(await settingsStore.GetOverridesAsync(ct))
            .Where(d => string.Equals(d.Key, options.Engine, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Definition)
            .FirstOrDefault();
        if (engine is null || string.IsNullOrWhiteSpace(engine.ApiKey))
        {
            return new(null, $"Alt text uses the API key of engine '{options.Engine}', which has none. Add it in AI Engines.");
        }

        if (!Uri.TryCreate(item.Question, UriKind.Absolute, out var imageUrl))
        {
            return new(null, "The image address isn't valid.");
        }

        (string? Reference, string? Error) image;
        try
        {
            using var fetch = CancellationTokenSource.CreateLinkedTokenSource(ct);
            fetch.CancelAfter(TimeSpan.FromSeconds(30));
            image = await ImageReferenceAsync(imageUrl, options, fetch.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new(null, $"The image didn't load: {ex.Message}");
        }

        if (image.Error is not null)
        {
            return new(null, image.Error);
        }

        var http = httpClientFactory.CreateClient(JsonEngineClientBase.HttpClientName);
        var prompt = ContentLoop.BuildAltPrompt(item, brand, pageTitle);
        var problems = new List<string>();
        string? englishAlt = null;
        foreach (var model in options.Models)
        {
            // Each model gets its own time limit: a hung model moves on to the next instead of failing the item.
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            try
            {
                using var request = BuildRequest(engine, model, options.MaxOutputTokens, image.Reference!, prompt);
                using var response = await http.SendAsync(request, attempt.Token);
                var body = await response.Content.ReadAsStringAsync(attempt.Token);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Vision model {Model} returned HTTP {Status}; trying the next one.", model, (int)response.StatusCode);
                    problems.Add(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone
                        ? $"{model} is not available" : $"{model} returned HTTP {(int)response.StatusCode}: {body[..Math.Min(body.Length, 200)]}");
                    continue;
                }

                using var json = JsonDocument.Parse(body);
                var text = json.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
                    && choices[0].TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.String ? content.GetString() ?? string.Empty : string.Empty;
                var alt = ContentLoop.CleanAltText(text, item.LanguageCode);
                logger.LogInformation("{Model} described {Image}: {Alt}", model, imageUrl, alt);
                if (alt.Length == 0)
                {
                    problems.Add($"{model} gave an empty answer");
                    continue;
                }

                // A Turkish page needs Turkish alt text: when a model answers in English, ask the next one.
                if (ContentLoop.LooksEnglish(alt, item.LanguageCode))
                {
                    englishAlt ??= alt;
                    problems.Add($"{model} answered in English");
                    continue;
                }

                return new(alt, null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
            {
                logger.LogWarning("Vision model {Model} failed: {Message}", model, ex.Message);
                problems.Add($"{model}: {(ex is TaskCanceledException ? "no answer in time" : ex.Message)}");
            }
        }

        // Better an English draft to translate in review than none.
        return englishAlt is not null ? new(englishAlt, null) : new(null, $"No alt text: {string.Join("; ", problems)}.");
    }

    /// <summary>A data URI for small images, else the image's own address.</summary>
    private async Task<(string? Reference, string? Error)> ImageReferenceAsync(Uri url, VisionOptions options, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient(NativeHtmlScraperService.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", scraperOptions.Value.UserAgent);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            return (null, $"The image didn't load: HTTP {(int)response.StatusCode}.");
        }

        var type = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (type is not ("image/jpeg" or "image/png" or "image/webp" or "image/gif"))
        {
            return (null, $"Not a photo-type image ({(type.Length == 0 ? "unknown type" : type)}); describe it by hand.");
        }

        var length = response.Content.Headers.ContentLength;
        if (length > options.MaxImageBytes)
        {
            return (null, "The image is too large to describe.");
        }

        // Without a declared size, read it: small images go inline, the rest by address.
        if (length is null || length <= options.MaxInlineBytes)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length <= options.MaxInlineBytes)
            {
                return ($"data:{type};base64,{Convert.ToBase64String(bytes)}", null);
            }
        }

        return (url.ToString(), null);
    }

    public static HttpRequestMessage BuildRequest(AnswerEngineDefinition engine, string model, int maxTokens, string image, string prompt)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = image } },
                    new JsonObject { ["type"] = "text", ["text"] = prompt }),
            }),
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0.2,
            ["stream"] = false,
        };
        var request = new HttpRequestMessage(HttpMethod.Post, $"{engine.ResolvedBaseUrl}/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", engine.ApiKey);
        return request;
    }
}
