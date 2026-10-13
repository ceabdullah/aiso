using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aiso.Infrastructure.Engines;

/// <summary>Shared plumbing for engines called over plain JSON/HTTP (vendors without an SDK in this stack).</summary>
public abstract class JsonEngineClientBase(IHttpClientFactory httpClientFactory) : IAnswerEngineClient
{
    public const string HttpClientName = "aiso-answer-engines";

    public abstract AnswerEngineKind Kind { get; }

    public async Task<EngineAnswer> AskAsync(AnswerEngineDefinition engine, string prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(engine);
        using var request = BuildRequest(engine, prompt);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(engine.TimeoutSeconds));

        var http = httpClientFactory.CreateClient(HttpClientName);
        using var response = await http.SendAsync(request, timeout.Token);
        var body = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"{engine.DisplayName} returned HTTP {(int)response.StatusCode}: {Truncate(body, 300)}", null, response.StatusCode);
        }

        return ParseResponse(JsonDocument.Parse(body).RootElement);
    }

    /// <summary>Builds the vendor request. Public for unit tests.</summary>
    public abstract HttpRequestMessage BuildRequest(AnswerEngineDefinition engine, string prompt);

    /// <summary>Extracts answer text and citations. Public for unit tests.</summary>
    public abstract EngineAnswer ParseResponse(JsonElement root);

    protected static HttpRequestMessage JsonPost(string url, JsonObject body, string? bearer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        // Ask for one JSON response explicitly; some OpenAI-compatible hosts (NVIDIA) otherwise may stream or hold the request.
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return request;
    }

    protected static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray()
            : [];

    protected static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
