using Aiso.Infrastructure.Llm;
using Aiso.Infrastructure.Notifications;
using Aiso.Infrastructure.Persistence;
using Aiso.Infrastructure.Reporting;
using Aiso.Infrastructure.Runtime;
using Aiso.Infrastructure.Scraping;
using Aiso.Infrastructure.Settings;
using Aiso.Infrastructure.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Aiso.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Database + settings only. Used by the web UI and migration node.</summary>
    public static IHostApplicationBuilder AddAisoCore(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddAisoDatabase();
        builder.Services.AddOptions<WorkspaceOptions>().Bind(builder.Configuration.GetSection(WorkspaceOptions.SectionName));
        builder.Services.AddSingleton<ISystemSettingsService, SystemSettingsService>();
        return builder;
    }

    /// <summary>Full audit pipeline: scraper, LLM probe, claude.exe runtime, exporter, notifications.</summary>
    public static IHostApplicationBuilder AddAisoPipeline(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddAisoCore();

        var config = builder.Configuration;
        builder.Services.AddOptions<LlmGatewayOptions>().Bind(config.GetSection(LlmGatewayOptions.SectionName));
        builder.Services.AddOptions<ClaudeCliOptions>().Bind(config.GetSection(ClaudeCliOptions.SectionName));
        builder.Services.AddOptions<ScraperOptions>().Bind(config.GetSection(ScraperOptions.SectionName));
        builder.Services.AddOptions<SmtpOptions>().Bind(config.GetSection(SmtpOptions.SectionName));

        builder.Services.AddHttpClient<NativeHtmlScraperService>(NativeHtmlScraperService.HttpClientName, (sp, http) =>
            {
                http.Timeout = Timeout.InfiniteTimeSpan; // resilience handler owns timeouts
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5,
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            })
            .AddStandardResilienceHandler(o =>
            {
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(90);
                o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
            });

        builder.Services.AddHttpClient<PromptVisibilityProbe>(PromptVisibilityProbe.HttpClientName, (sp, http) =>
            {
                var llm = sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value;
                if (!string.IsNullOrWhiteSpace(llm.BaseUrl))
                {
                    http.BaseAddress = new Uri(llm.BaseUrl.TrimEnd('/') + "/");
                }

                http.Timeout = Timeout.InfiniteTimeSpan;
                http.DefaultRequestHeaders.Add("x-api-key", llm.ApiKey);
                http.DefaultRequestHeaders.Add("anthropic-version", llm.AnthropicVersion);
            })
            .AddStandardResilienceHandler(o =>
            {
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(120);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(300);
                o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(240);
            });

        builder.Services.AddSingleton<IClaudeCliOrchestrator, ClaudeCliOrchestrator>();
        builder.Services.AddSingleton<AisoMarkdownExporter>();
        builder.Services.AddSingleton<IAisoNotificationService, AisoNotificationService>();
        return builder;
    }
}
