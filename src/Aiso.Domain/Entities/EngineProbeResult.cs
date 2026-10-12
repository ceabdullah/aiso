namespace Aiso.Domain.Entities;

/// <summary>
/// One test prompt answered by one AI answer engine (ChatGPT, Gemini, Perplexity, Claude, internal model)
/// during an audit run. Visibility counts a brand mention in the answer or a citation of an owned domain.
/// </summary>
public class EngineProbeResult : EntityBase
{
    public Guid AuditLogId { get; set; }
    public LiveSearchAuditLog? AuditLog { get; set; }

    /// <summary>The prompt that was asked. Not a foreign key: prompts can be deleted while history is kept.</summary>
    public Guid PromptId { get; set; }

    /// <summary>Configuration key of the engine, e.g. <c>chatgpt</c>.</summary>
    public required string EngineKey { get; set; }

    /// <summary>Display name at the time of the run, e.g. <c>ChatGPT</c>.</summary>
    public required string EngineName { get; set; }

    public required string LanguageCode { get; set; }

    /// <summary>Snapshot of the prompt text as asked.</summary>
    public required string PromptText { get; set; }

    public bool MentionsBrand { get; set; }

    public bool CitesOwnedDomain { get; set; }

    /// <summary>Brand keywords found in the answer (comma separated).</summary>
    public string? MatchedKeywords { get; set; }

    /// <summary>JSON array of <c>{ url, title }</c> sources the engine cited (large text).</summary>
    public string? CitationsJson { get; set; }

    /// <summary>First part of the answer, for the dashboard; the full answer is in the data/ snapshot.</summary>
    public string? AnswerExcerpt { get; set; }

    public string? ErrorMessage { get; set; }

    public long LatencyMs { get; set; }

    public bool IsVisible => MentionsBrand || CitesOwnedDomain;
}
