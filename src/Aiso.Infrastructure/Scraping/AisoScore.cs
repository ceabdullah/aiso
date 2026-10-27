namespace Aiso.Infrastructure.Scraping;

/// <summary>
/// The overall AISO score, shared by the worker (stored per audit) and the dashboard (current view across engines).
/// Prompt visibility is the outcome that matters most for GEO, so it weighs more than on-page structure.
/// </summary>
public static class AisoScore
{
    public const decimal OnPageWeight = 0.4m;
    public const decimal PromptWeight = 0.6m;

    /// <summary>Overall score; on-page only when no AI engine answers were measured.</summary>
    public static decimal Overall(decimal onPageScore, decimal? promptVisibilityScore) =>
        promptVisibilityScore is { } prompt
            ? Math.Round(OnPageWeight * onPageScore + PromptWeight * prompt, 2)
            : onPageScore;
}
