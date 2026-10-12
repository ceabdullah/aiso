using System.Text.Json.Serialization;

namespace Aiso.Infrastructure.Runtime;

/// <summary>Outcome of one <c>claude.exe -p</c> invocation.</summary>
public sealed record ClaudeCliResult(
    bool Success,
    int ExitCode,
    string ResultText,
    string? SessionId,
    int? NumTurns,
    decimal? TotalCostUsd,
    string StandardError,
    TimeSpan Duration,
    int Attempts);

/// <summary>Shape of the final JSON object printed by <c>claude -p --output-format json</c>.</summary>
internal sealed record ClaudeJsonOutput(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("subtype")] string? Subtype,
    [property: JsonPropertyName("is_error")] bool IsError,
    [property: JsonPropertyName("result")] string? Result,
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("num_turns")] int? NumTurns,
    [property: JsonPropertyName("total_cost_usd")] decimal? TotalCostUsd);
