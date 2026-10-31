using System.Text.Json;
using JKToolKit.CodexSDK.AppServer;
using JKToolKit.CodexSDK.AppServer.ThreadRead;

// ReSharper disable once CheckNamespace
namespace JKToolKit.CodexSDK;

/// <summary>Collected result of a completed turn, shaped like the Python SDK's <c>TurnResult</c>.</summary>
public sealed record class CodexRunResult
{
    /// <summary>Gets the turn identifier.</summary>
    public required string Id { get; init; }
    /// <summary>Gets the owning thread identifier.</summary>
    public required string ThreadId { get; init; }
    /// <summary>Gets the terminal status (<c>completed</c>, <c>interrupted</c>, <c>failed</c>, or a future value).</summary>
    public string? Status { get; init; }
    /// <summary>Gets the terminal error, if any.</summary>
    public CodexTurnError? Error { get; init; }
    /// <summary>Gets the Unix timestamp (seconds) when the turn started, if reported.</summary>
    public long? StartedAt { get; init; }
    /// <summary>Gets the Unix timestamp (seconds) when the turn completed, if reported.</summary>
    public long? CompletedAt { get; init; }
    /// <summary>Gets the turn duration in milliseconds, if reported.</summary>
    public long? DurationMs { get; init; }
    /// <summary>
    /// Gets the text of the last <c>final_answer</c> agent message, else the last agent message without a phase;
    /// <see langword="null"/> when neither exists (commentary alone is not a final answer).
    /// </summary>
    public string? FinalResponse { get; init; }
    /// <summary>Gets the completed items in arrival order.</summary>
    public IReadOnlyList<CodexThreadItem> Items { get; init; } = Array.Empty<CodexThreadItem>();
    /// <summary>Gets the latest token usage for the turn, if reported.</summary>
    public CodexTurnUsage? Usage { get; init; }
    /// <summary>Gets the underlying collected result (diff, terminal payload, partial flag).</summary>
    public required CodexTurnResult Turn { get; init; }

    internal static CodexRunResult From(CodexTurnResult turn)
    {
        var terminal = turn.TerminalTurn;
        return new CodexRunResult
        {
            Id = turn.TurnId, ThreadId = turn.ThreadId, Status = turn.Status,
            Error = CodexTurnError.Parse(turn.Error),
            StartedAt = Number(terminal, "startedAt"), CompletedAt = Number(terminal, "completedAt"),
            DurationMs = Number(terminal, "durationMs"),
            FinalResponse = FinalResponseFrom(turn.Items), Items = turn.Items, Usage = turn.Usage, Turn = turn
        };
    }

    /// <summary>Python <c>_final_assistant_response_from_items</c>.</summary>
    internal static string? FinalResponseFrom(IReadOnlyList<CodexThreadItem> items)
    {
        string? lastUnlabelled = null;
        for (var i = items.Count - 1; i >= 0; i--)
        {
            var raw = items[i].Raw;
            if (items[i].Type != "agentMessage" || raw.ValueKind != JsonValueKind.Object) continue;
            var text = raw.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var hasPhase = raw.TryGetProperty("phase", out var phase) && phase.ValueKind != JsonValueKind.Null;
            if (hasPhase && phase.ValueKind == JsonValueKind.String && phase.GetString() == "final_answer") return text;
            if (!hasPhase && lastUnlabelled is null) lastUnlabelled = text;
        }
        return lastUnlabelled;
    }

    private static long? Number(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : null;
}

/// <summary>Thrown by high-level run methods when the server reports a <c>failed</c> turn (Python raises <c>RuntimeError</c>).</summary>
public sealed class CodexTurnFailedException : Exception
{
    /// <summary>Initializes a new instance for a failed turn result.</summary>
    public CodexTurnFailedException(CodexRunResult result)
        : base(result?.Error is { Message.Length: > 0 } error ? error.Message : $"turn failed with status {result?.Status}")
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    /// <summary>Gets the failed turn's collected result.</summary>
    public CodexRunResult Result { get; }
}
