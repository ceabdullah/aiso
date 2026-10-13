using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aiso.Domain.Entities;
using Aiso.Infrastructure.Engines;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aiso.Tests;

public class AnswerEngineClientTests
{
    private static readonly AnswerEngineDefinition Engine = new() { ApiKey = "k", Model = "m", MaxOutputTokens = 900 };

    private static JsonNode Body(HttpRequestMessage r) => JsonNode.Parse(r.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;

    [Fact]
    public void OpenAi_request_enables_web_search_and_response_yields_url_citations()
    {
        var client = new OpenAiResponsesClient(new StubHttpFactory(_ => throw new InvalidOperationException()));
        var engine = new AnswerEngineDefinition { Kind = AnswerEngineKind.OpenAiResponses, ApiKey = "sk", Model = "gpt-x" };
        using var request = client.BuildRequest(engine, "En iyi KOBİ bankası?");

        Assert.Equal("https://api.openai.com/v1/responses", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("web_search", Body(request)["tools"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("En iyi KOBİ bankası?", Body(request)["input"]!.GetValue<string>());

        const string response = """
            {"output":[
              {"type":"web_search_call","id":"ws_1","status":"completed"},
              {"type":"message","content":[{"type":"output_text","text":"TEB ve Garanti öne çıkıyor.",
                "annotations":[{"type":"url_citation","start_index":0,"end_index":3,"url":"https://www.teb.com.tr/kobi","title":"TEB KOBİ"},
                               {"type":"url_citation","url":"https://www.garantibbva.com.tr/","title":"Garanti"},
                               {"type":"url_citation","url":"https://www.teb.com.tr/kobi","title":"dup"}]}]}]}
            """;
        var answer = client.ParseResponse(JsonDocument.Parse(response).RootElement);

        Assert.Equal("TEB ve Garanti öne çıkıyor.", answer.Text);
        Assert.Equal(2, answer.Citations.Count);
        Assert.Equal("https://www.teb.com.tr/kobi", answer.Citations[0].Url);
    }

    [Fact]
    public void Gemini_request_uses_google_search_and_reads_grounding_chunks()
    {
        var client = new GeminiGroundedClient(new StubHttpFactory(_ => throw new InvalidOperationException()));
        var engine = new AnswerEngineDefinition { Kind = AnswerEngineKind.GeminiGrounded, ApiKey = "g", Model = "gemini-x" };
        using var request = client.BuildRequest(engine, "q");

        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-x:generateContent", request.RequestUri!.ToString());
        Assert.Equal("g", request.Headers.GetValues("x-goog-api-key").Single());
        Assert.NotNull(Body(request)["tools"]![0]!["google_search"]);

        const string response = """
            {"candidates":[{"content":{"parts":[{"text":"CEPTETEB "},{"text":"dijital hesap sunar."}]},
              "groundingMetadata":{"webSearchQueries":["dijital banka"],
                "groundingChunks":[{"web":{"uri":"https://vertexaisearch.cloud.google.com/grounding-api-redirect/abc","title":"cepteteb.com.tr"}},
                                   {"web":{"uri":"https://vertexaisearch.cloud.google.com/grounding-api-redirect/def","title":"enpara.com"}}]}}]}
            """;
        var answer = client.ParseResponse(JsonDocument.Parse(response).RootElement);

        Assert.Equal("CEPTETEB \ndijital hesap sunar.", answer.Text);
        Assert.Equal(["cepteteb.com.tr", "enpara.com"], answer.Citations.Select(c => c.Title));
    }

    [Fact]
    public void Perplexity_prefers_search_results_and_falls_back_to_citations()
    {
        var client = new PerplexitySonarClient(new StubHttpFactory(_ => throw new InvalidOperationException()));
        using var request = client.BuildRequest(new AnswerEngineDefinition { Kind = AnswerEngineKind.PerplexitySonar, ApiKey = "p", Model = "sonar-pro" }, "q");
        Assert.Equal("https://api.perplexity.ai/chat/completions", request.RequestUri!.ToString());
        Assert.Equal("user", Body(request)["messages"]![0]!["role"]!.GetValue<string>());

        var withResults = client.ParseResponse(JsonDocument.Parse("""
            {"choices":[{"message":{"role":"assistant","content":"TEB Mobil [1]"}}],
             "search_results":[{"title":"TEB Mobil","url":"https://apps.apple.com/tr/app/teb-mobil/id123","date":"2026-09-01"}],
             "citations":["https://ignored.example"]}
            """).RootElement);
        Assert.Equal("TEB Mobil [1]", withResults.Text);
        Assert.Equal("https://apps.apple.com/tr/app/teb-mobil/id123", Assert.Single(withResults.Citations).Url);

        var citationsOnly = client.ParseResponse(JsonDocument.Parse("""
            {"choices":[{"message":{"content":"x"}}],"citations":["https://a.example/1","https://b.example/2"]}
            """).RootElement);
        Assert.Equal(2, citationsOnly.Citations.Count);
    }

    [Fact]
    public void Anthropic_request_enables_web_search()
    {
        var claude = new AnswerEngineDefinition { Kind = AnswerEngineKind.AnthropicWebSearch, ApiKey = "a", Model = "claude-opus-5-5", MaxSearches = 3 };
        var json = JsonSerializer.Serialize(AnthropicEngineClient.BuildParams(claude, "q"));
        Assert.Contains("web_search_20260209", json, StringComparison.Ordinal);
        Assert.Contains("\"max_uses\":3", json, StringComparison.Ordinal);

        Assert.Equal("https://api.anthropic.com", claude.ResolvedBaseUrl);
    }

    [Fact]
    public void Anthropic_response_citations_are_extracted()
    {
        const string json = """
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5","stop_reason":"end_turn","stop_sequence":null,
             "usage":{"input_tokens":10,"output_tokens":20},
             "content":[
              {"type":"server_tool_use","id":"srvtoolu_1","name":"web_search","input":{"query":"kobi kredisi"}},
              {"type":"web_search_tool_result","tool_use_id":"srvtoolu_1","content":[{"type":"web_search_result","url":"https://www.teb.com.tr/kobi","title":"TEB","encrypted_content":"x","page_age":null}]},
              {"type":"text","text":"TEB KOBİ kredisi sunar.","citations":[{"type":"web_search_result_location","url":"https://www.teb.com.tr/kobi","title":"TEB","encrypted_index":"y","cited_text":"KOBİ"}]}]}
            """;
        var message = JsonSerializer.Deserialize<Anthropic.Models.Messages.Message>(json)!;
        var answer = AnthropicEngineClient.Parse(message);

        Assert.Equal("TEB KOBİ kredisi sunar.", answer.Text);
        Assert.Equal("https://www.teb.com.tr/kobi", Assert.Single(answer.Citations).Url);
    }
}

public class OwnedDomainMatcherTests
{
    [Theory]
    [InlineData("https://www.teb.com.tr/kobi", null, "teb.com.tr", true)]
    [InlineData("https://kampanya.teb.com.tr/x", null, "teb.com.tr", true)]
    [InlineData("https://notteb.com.tr/", null, "teb.com.tr", false)]
    [InlineData("https://apps.apple.com/tr/app/teb-mobil/id1", null, "apps.apple.com/tr/app/teb-mobil", true)]
    [InlineData("https://apps.apple.com/tr/app/enpara/id2", null, "apps.apple.com/tr/app/teb-mobil", false)]
    [InlineData("https://vertexaisearch.cloud.google.com/grounding-api-redirect/abc", "cepteteb.com.tr", "cepteteb.com.tr", true)]
    [InlineData("https://vertexaisearch.cloud.google.com/grounding-api-redirect/abc", "Some Article Title", "cepteteb.com.tr", false)]
    public void FindOwned_matches_hosts_subdomains_paths_and_gemini_titles(string url, string? title, string owned, bool expected) =>
        Assert.Equal(expected, OwnedDomainMatcher.FindOwned([new EngineCitation(url, title)], [owned]).Count == 1);
}

public class AnswerEngineProbeTests
{
    [Fact]
    public async Task ProbeAsync_asks_every_configured_engine_and_scores_mentions_and_citations()
    {
        var options = new AnswerEngineOptions();
        options.Engines["chatgpt"] = new() { DisplayName = "ChatGPT", Kind = AnswerEngineKind.OpenAiResponses, ApiKey = "o", Model = "gpt" };
        options.Engines["perplexity"] = new() { DisplayName = "Perplexity", Kind = AnswerEngineKind.PerplexitySonar, ApiKey = "p", Model = "sonar" };
        options.Engines["gemini"] = new() { DisplayName = "Gemini", Kind = AnswerEngineKind.GeminiGrounded, ApiKey = "", Model = "g" }; // no key → skipped
        options.Engines["claude"] = new() { DisplayName = "Claude", Kind = AnswerEngineKind.AnthropicWebSearch, Enabled = false, ApiKey = "c", Model = "m" }; // disabled → skipped

        string? openAiBody = null;
        var http = new StubHttpFactory(req => req.RequestUri!.Host switch
        {
            "api.openai.com" => Capture(req, out openAiBody, """{"output":[{"type":"message","content":[{"type":"output_text","text":"Akbank and Garanti.","annotations":[{"type":"url_citation","url":"https://www.teb.com.tr/x","title":"t"}]}]}]}"""),
            "api.perplexity.ai" => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("rate limited") },
            _ => throw new InvalidOperationException(req.RequestUri.ToString()),
        });

        var probe = new AnswerEngineProbe(Options.Create(options), ConfigurationOnlyEngineSettingsStore.Instance,
            [new OpenAiResponsesClient(http), new PerplexitySonarClient(http), new GeminiGroundedClient(http), new AnthropicEngineClient()],
            new DefaultSettings(), NullLogger<AnswerEngineProbe>.Instance);

        var engines = await probe.GetActiveEnginesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["chatgpt", "perplexity"], engines.Select(e => e.Key));

        var target = new TargetProfile { Code = "t", DisplayName = "TEB", BaseUrl = "https://www.teb.com.tr", BrandKeywords = "TEB", OwnedDomains = "teb.com.tr" };
        var prompts = new[] { new AutomatedTestPrompt { PromptText = "q1", LanguageCode = "en-US" }, new AutomatedTestPrompt { PromptText = "q2", LanguageCode = "en-US" } };

        var outcomes = await probe.ProbeAsync(target, prompts, engines, TestContext.Current.CancellationToken);

        Assert.Equal(4, outcomes.Count);
        var chatgpt = outcomes.Where(o => o.EngineKey == "chatgpt").ToList();
        Assert.All(chatgpt, o => { Assert.True(o.Succeeded); Assert.False(o.MentionsBrand); Assert.True(o.CitesOwnedDomain); Assert.True(o.IsVisible); });
        Assert.All(outcomes.Where(o => o.EngineKey == "perplexity"), o => Assert.Contains("429", o.Error, StringComparison.Ordinal));

        // The default search location (System Settings) reaches the engine request.
        var location = JsonNode.Parse(openAiBody!)!["tools"]![0]!["user_location"]!;
        Assert.Equal("TR", location["country"]!.GetValue<string>());
        Assert.Equal("Europe/Istanbul", location["timezone"]!.GetValue<string>());
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Capture(HttpRequestMessage request, out string? body, string json)
    {
        body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        return Json(json);
    }
}

/// <summary>Routes every named client to a stub handler.</summary>
internal sealed class StubHttpFactory(Func<HttpRequestMessage, HttpResponseMessage> respond) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new Handler(respond));

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}

/// <summary>System settings at their defaults (search location Turkey/Istanbul), without a database.</summary>
internal sealed class DefaultSettings : Aiso.Infrastructure.Settings.ISystemSettingsService
{
    public Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(Aiso.Domain.SettingKeys.Defaults.TryGetValue(key, out var d) ? d.Value : string.Empty);

    public Task<int> GetIntAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(int.Parse(Aiso.Domain.SettingKeys.Defaults[key].Value, System.Globalization.CultureInfo.InvariantCulture));

    public Task<IReadOnlyList<GlobalSystemSetting>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GlobalSystemSetting>>([]);

    public Task SetAsync(string key, string value, string? updatedBy, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public class SearchLocationTests
{
    private static readonly SearchLocation Istanbul = SearchLocation.Create(" tr ", "Istanbul", "Istanbul", "Europe/Istanbul")!;
    private static readonly StubHttpFactory NoHttp = new(_ => throw new InvalidOperationException());

    private static JsonNode Body(HttpRequestMessage r) => JsonNode.Parse(r.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;

    [Fact]
    public void Create_normalises_the_country_and_needs_one()
    {
        Assert.Equal("TR", Istanbul.Country);
        Assert.Null(SearchLocation.Create("  ", "Istanbul", "Istanbul", null));
        Assert.Null(SearchLocation.Create("TR", "", null, " ")!.Region);
    }

    [Fact]
    public void OpenAi_sends_an_approximate_user_location_only_when_set()
    {
        var engine = new AnswerEngineDefinition { Kind = AnswerEngineKind.OpenAiResponses, ApiKey = "k", Model = "m", Location = Istanbul };
        using var withLocation = new OpenAiResponsesClient(NoHttp).BuildRequest(engine, "q");
        var location = Body(withLocation)["tools"]![0]!["user_location"]!;
        Assert.Equal("approximate", location["type"]!.GetValue<string>());
        Assert.Equal("Istanbul", location["city"]!.GetValue<string>());

        engine.Location = null;
        using var without = new OpenAiResponsesClient(NoHttp).BuildRequest(engine, "q");
        Assert.Null(Body(without)["tools"]![0]!["user_location"]);
    }

    [Fact]
    public void Perplexity_sends_web_search_options_user_location()
    {
        var engine = new AnswerEngineDefinition { Kind = AnswerEngineKind.PerplexitySonar, ApiKey = "k", Model = "sonar", Location = Istanbul };
        using var request = new PerplexitySonarClient(NoHttp).BuildRequest(engine, "q");
        var location = Body(request)["web_search_options"]!["user_location"]!;
        Assert.Equal("TR", location["country"]!.GetValue<string>());
        Assert.Equal("Istanbul", location["region"]!.GetValue<string>());
    }

    [Fact]
    public void Claude_sends_the_location_on_the_web_search_tool()
    {
        var engine = new AnswerEngineDefinition { Kind = AnswerEngineKind.AnthropicWebSearch, ApiKey = "k", Model = "claude-opus-5-5", Location = Istanbul };
        // RawBodyData is the JSON body the SDK sends.
        var tool = JsonNode.Parse(JsonSerializer.Serialize(AnthropicEngineClient.BuildParams(engine, "q")))!["RawBodyData"]!["tools"]![0]!;
        Assert.Equal("approximate", tool["user_location"]!["type"]!.GetValue<string>());
        Assert.Equal("TR", tool["user_location"]!["country"]!.GetValue<string>());
        Assert.Equal("Europe/Istanbul", tool["user_location"]!["timezone"]!.GetValue<string>());
    }
}
