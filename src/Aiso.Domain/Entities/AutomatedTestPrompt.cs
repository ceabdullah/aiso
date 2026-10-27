namespace Aiso.Domain.Entities;

/// <summary>
/// A natural-language question a customer might ask an AI assistant.
/// The platform replays these against the LLM gateway and measures whether the target brand is surfaced.
/// </summary>
public class AutomatedTestPrompt : EntityBase
{
    public Guid TargetProfileId { get; set; }
    public TargetProfile? TargetProfile { get; set; }

    public required string PromptText { get; set; }

    /// <summary>BCP-47 language code of the prompt (e.g. <c>tr-TR</c>, <c>en-US</c>).</summary>
    public required string LanguageCode { get; set; }

    /// <summary>Optional grouping label such as "credit-cards" or "mobile-banking".</summary>
    public string? Category { get; set; }

    public bool IsActive { get; set; } = true;
}
