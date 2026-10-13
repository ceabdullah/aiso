using Aiso.Infrastructure.Engines;
using Aiso.Infrastructure.Llm;
using Aiso.Infrastructure.Notifications;
using Aiso.Infrastructure.Persistence;
using Aiso.Infrastructure.Reporting;
using Aiso.Infrastructure.Runtime;
using Aiso.Infrastructure.Scraping;
using Aiso.Infrastructure.Security;
using Aiso.Infrastructure.Settings;
using Aiso.Infrastructure.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.AddAisoSecretProtection();
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

        builder.Services.AddOptions<AnswerEngineOptions>().Bind(config.GetSection(AnswerEngineOptions.SectionName));
        builder.Services.AddHttpClient(JsonEngineClientBase.HttpClientName, http => http.Timeout = Timeout.InfiniteTimeSpan)
            .AddStandardResilienceHandler(o =>
            {
                // Web-grounded answers can take a minute or more; per-engine timeouts are enforced by the clients.
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(240);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(600);
                o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(480);
                o.Retry.MaxRetryAttempts = 2;
            });
        builder.Services.AddSingleton<IAnswerEngineClient, AnthropicEngineClient>();
        builder.Services.AddSingleton<IAnswerEngineClient, OpenAiResponsesClient>();
        builder.Services.AddSingleton<IAnswerEngineClient, GeminiGroundedClient>();
        builder.Services.AddSingleton<IAnswerEngineClient, PerplexitySonarClient>();
        builder.Services.AddSingleton<IAnswerEngineClient, OpenAiCompatibleChatClient>();
        builder.Services.AddSingleton<IEngineSettingsStore, EfEngineSettingsStore>();
        builder.Services.AddSingleton<AnswerEngineProbe>();

        builder.Services.AddOptions<PlannerOptions>().Bind(config.GetSection(PlannerOptions.SectionName));
        if (new PlannerOptions { Provider = config[$"{PlannerOptions.SectionName}:Provider"] ?? "ClaudeCli" }.UsesChatModel)
        {
            builder.Services.AddSingleton<IClaudeCliOrchestrator, ChatModelPlanner>();
        }
        else
        {
            builder.Services.AddSingleton<IClaudeCliOrchestrator, ClaudeCliOrchestrator>();
        }
        builder.Services.AddSingleton<AisoMarkdownExporter>();
        builder.Services.AddSingleton<IAisoNotificationService, AisoNotificationService>();
        return builder;
    }
}
