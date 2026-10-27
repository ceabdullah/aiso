namespace Aiso.Domain.Entities;

/// <summary>
/// Admin-controlled state of one external AI answer engine (ChatGPT, Gemini, …).
/// The worker registers a row for every engine in its configuration and reports whether an API key is set;
/// administrators switch engines on or off and override the model from the dashboard.
/// An API key entered in the dashboard is stored only as Data Protection ciphertext; a key set in the worker's
/// environment takes precedence over it.
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

    /// <summary>API key entered in the dashboard, encrypted with ASP.NET Core Data Protection. Never plaintext.</summary>
    public string? ApiKeyCiphertext { get; set; }

    /// <summary>Last four characters of the dashboard key, to recognise it without revealing it.</summary>
    public string? ApiKeyHint { get; set; }

    public DateTime? ApiKeyUpdatedAtUtc { get; set; }

    public string? ApiKeyUpdatedBy { get; set; }

    /// <summary>Reported by the worker: an API key is present in its own configuration (environment / secret store).</summary>
    public bool KeyConfigured { get; set; }

    /// <summary>When the worker last registered this engine.</summary>
    public DateTime? LastSyncedAtUtc { get; set; }

    public string? UpdatedBy { get; set; }
}
