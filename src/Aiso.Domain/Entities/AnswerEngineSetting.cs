namespace Aiso.Domain.Entities;

/// <summary>
/// Admin-controlled state of one external AI answer engine (ChatGPT, Gemini, …).
/// The worker registers a row for every engine in its configuration and reports whether an API key is set;
/// administrators switch engines on or off and override the model from the dashboard.
/// API keys are never stored here: they stay in the worker's secret store / environment.
/// </summary>
public class AnswerEngineSetting : EntityBase
{
    /// <summary>Configuration key, e.g. <c>chatgpt</c>. Unique.</summary>
    public required string EngineKey { get; set; }

    public required string DisplayName { get; set; }

    /// <summary>Engine kind as configured (e.g. <c>OpenAiResponses</c>), for display.</summary>
    public required string Kind { get; set; }

    /// <summary>Admin switch. Initialised from configuration the first time the worker registers the engine.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Model from the worker's configuration, shown as the default.</summary>
    public string? DefaultModel { get; set; }

    /// <summary>Admin override of the model; null uses <see cref="DefaultModel"/>.</summary>
    public string? ModelOverride { get; set; }

    /// <summary>Admin override of web searches per answer (engines that support a limit); null uses configuration.</summary>
    public int? MaxSearchesOverride { get; set; }

    /// <summary>Reported by the worker: an API key is present in its configuration.</summary>
    public bool KeyConfigured { get; set; }

    /// <summary>When the worker last registered this engine.</summary>
    public DateTime? LastSyncedAtUtc { get; set; }

    public string? UpdatedBy { get; set; }
}
