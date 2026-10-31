using System.Runtime.CompilerServices;
using JKToolKit.CodexSDK.AppServer;
using JKToolKit.CodexSDK.AppServer.Notifications;

// ReSharper disable once CheckNamespace
namespace JKToolKit.CodexSDK;

/// <summary>A conversation thread used to run one or more turns (Python <c>Thread</c> / <c>AsyncThread</c>).</summary>
public sealed class CodexConversation
{
    private readonly CodexAppServerClient _client;

    internal CodexConversation(CodexAppServerClient client, string id)
    { _client = client; Id = id; }

    /// <summary>Gets the thread identifier.</summary>
    public string Id { get; }

    /// <summary>Runs a complete turn and collects its result. Throws <see cref="CodexTurnFailedException"/> for a failed turn.</summary>
    public async Task<CodexRunResult> RunAsync(CodexRunInput input, CodexTurnOptions? options = null, CancellationToken ct = default)
    {
        await using var turn = await TurnAsync(input, options, ct).ConfigureAwait(false);
        return await turn.RunAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Starts a turn and returns its handle for streaming, steering, interruption, or collection.</summary>
    public async Task<CodexConversationTurn> TurnAsync(CodexRunInput input, CodexTurnOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var handle = await _client.StartTurnAsync(Id, (options ?? new CodexTurnOptions()).ToWire(input), ct).ConfigureAwait(false);
        return new CodexConversationTurn(handle);
    }

    /// <summary>Reads this thread, optionally including its turn history.</summary>
    public Task<CodexThreadReadResult> ReadAsync(bool includeTurns = false, CancellationToken ct = default) =>
        _client.ReadThreadAsync(Id, new ThreadReadOptions { IncludeTurns = includeTurns }, ct);

    /// <summary>Sets this thread's name.</summary>
    public Task SetNameAsync(string name, CancellationToken ct = default) => _client.SetThreadNameAsync(Id, name, ct);

    /// <summary>Starts compaction of this thread's context.</summary>
    public Task CompactAsync(CancellationToken ct = default) => _client.CompactThreadAsync(Id, ct);
}

/// <summary>Controls and consumes a started turn (Python <c>TurnHandle</c> / <c>AsyncTurnHandle</c>).</summary>
public sealed class CodexConversationTurn : IAsyncDisposable
{
    internal CodexConversationTurn(CodexTurnHandle handle) => Handle = handle;

    /// <summary>Gets the turn identifier.</summary>
    public string Id => Handle.TurnId;

    /// <summary>Gets the owning thread identifier.</summary>
    public string ThreadId => Handle.ThreadId;

    /// <summary>Gets the underlying low-level turn handle.</summary>
    public CodexTurnHandle Handle { get; }

    /// <summary>
    /// Yields only this turn's notifications, ending after its <c>turn/completed</c>.
    /// Uses <see cref="CodexTurnHandle.Events"/>: concurrent enumerations compete for events.
    /// </summary>
    public async IAsyncEnumerable<AppServerNotification> StreamAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var notification in Handle.Events(ct).ConfigureAwait(false))
        {
            yield return notification;
            if (notification is TurnCompletedNotification completed && completed.TurnId == Id) yield break;
        }
    }

    /// <summary>
    /// Waits for completion and returns the collected result; independent of <see cref="StreamAsync"/> consumption.
    /// Throws <see cref="CodexTurnFailedException"/> when the turn failed.
    /// </summary>
    public async Task<CodexRunResult> RunAsync(CancellationToken ct = default)
    {
        var result = CodexRunResult.From(await Handle.RunAsync(ct).ConfigureAwait(false));
        return result.Status == "failed" ? throw new CodexTurnFailedException(result) : result;
    }

    /// <summary>Sends additional user input to this active turn and returns the accepted turn identifier.</summary>
    public Task<string> SteerAsync(CodexRunInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Handle.SteerAsync(input.RequireUserItems("Steering"), ct);
    }

    /// <summary>Requests interruption of this active turn.</summary>
    public Task InterruptAsync(CancellationToken ct = default) => Handle.InterruptAsync(ct);

    /// <summary>Disposes the underlying handle; this does not interrupt the server-side turn.</summary>
    public ValueTask DisposeAsync() => Handle.DisposeAsync();
}
