namespace Aiso.Infrastructure.Runtime;

/// <summary>Who writes the remediation plans. Bound from <c>Aiso:Planner</c>.</summary>
public sealed class PlannerOptions
{
    public const string SectionName = "Aiso:Planner";

    /// <summary>
    /// <c>ClaudeCli</c>: <c>claude.exe</c> through the corporate LiteLLM gateway (the Windows server).
    /// <c>OpenAiCompatible</c>: a chat model on an OpenAI-compatible API, e.g. NVIDIA (developer Macs, demos).
    /// </summary>
    public string Provider { get; set; } = "ClaudeCli";

    /// <summary>Answer engine whose endpoint and API key the chat planner uses (the key is entered in AI Engines).</summary>
    public string Engine { get; set; } = "nvidia";

    /// <summary>Chat model that writes the plans.</summary>
    public string Model { get; set; } = "nvidia/nemotron-3-super-120b-a12b";

    public int MaxOutputTokens { get; set; } = 4096;

    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>Sent as <c>chat_template_kwargs.enable_thinking</c> when set; null leaves the model's default.</summary>
    public bool? EnableThinking { get; set; } = false;

    /// <summary>Each data file is cut to this many characters before it is put in the prompt.</summary>
    public int MaxFileChars { get; set; } = 60_000;

    public bool UsesChatModel => string.Equals(Provider, "OpenAiCompatible", StringComparison.OrdinalIgnoreCase);
}
