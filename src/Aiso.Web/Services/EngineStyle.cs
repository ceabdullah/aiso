namespace Aiso.Web.Services;

/// <summary>
/// Fixed colour slot per answer engine (categorical palette, defined in app.css for both themes).
/// Colour follows the engine, never its position in a list, so filtering never repaints the survivors.
/// </summary>
public static class EngineStyle
{
    public static string Css(string engineKey) => engineKey.ToLowerInvariant() switch
    {
        "gemini" => "eng-gemini",
        "claude" => "eng-claude",
        "chatgpt" => "eng-chatgpt",
        "perplexity" => "eng-perplexity",
        // Simulated NVIDIA-hosted models take the next categorical slots.
        "nvidia" => "eng-nvidia",
        "nv-gptoss" => "eng-gptoss",
        "nv-deepseek" => "eng-deepseek",
        "nv-glm" => "eng-glm",
        _ => "eng-other",
    };
}
