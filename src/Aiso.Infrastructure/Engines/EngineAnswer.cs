using System.Text.Json.Serialization;

namespace Aiso.Infrastructure.Engines;

/// <summary>A source the engine cited or consulted for its answer.</summary>
public sealed record EngineCitation(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("title")] string? Title);

/// <summary>The engine's answer text plus its cited sources, de-duplicated by URL.</summary>
public sealed record EngineAnswer(string Text, IReadOnlyList<EngineCitation> Citations)
{
    public static EngineAnswer Create(IEnumerable<string> textParts, IEnumerable<EngineCitation> citations) =>
        new(string.Join('\n', textParts.Where(t => !string.IsNullOrWhiteSpace(t))).Trim(),
            citations.Where(c => !string.IsNullOrWhiteSpace(c.Url))
                .GroupBy(c => c.Url, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList());
}

public interface IAnswerEngineClient
{
    AnswerEngineKind Kind { get; }

    Task<EngineAnswer> AskAsync(AnswerEngineDefinition engine, string prompt, CancellationToken cancellationToken);
}
