using System.Collections.Concurrent;
using JKToolKit.CodexSDK.AppServer;
using JKToolKit.CodexSDK.AppServer.Notifications.V2AdditionalNotifications;

// ReSharper disable once CheckNamespace
namespace JKToolKit.CodexSDK;

/// <summary>
/// Async client for creating threads and running Codex turns, mirroring the official Python SDK's
/// <c>Codex</c> / <c>AsyncCodex</c> over the existing <see cref="CodexAppServerClient"/>.
/// </summary>
/// <example>
/// <code>
/// await using var codex = await Codex.StartAsync();
/// var thread = await codex.ThreadStartAsync(new() { Model = "gpt-5.5" });
/// var result = await thread.RunAsync("Say hello in one sentence.");
/// Console.WriteLine(result.FinalResponse);
/// </code>
/// </example>
public sealed class Codex : IAsyncDisposable
{
    private readonly CancellationTokenSource _disposed = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<AccountLoginCompletedNotification>> _logins = new(StringComparer.Ordinal);
    private readonly object _pumpLock = new();
    private Task? _loginPump;

    private Codex(CodexAppServerClient client) => Client = client;

    /// <summary>Gets the underlying low-level client (owned and disposed by this instance).</summary>
    public CodexAppServerClient Client { get; }

    /// <summary>Gets the server's <c>initialize</c> response (Python <c>metadata</c>).</summary>
    public AppServerInitializeResult Metadata =>
        Client.InitializeResult ?? throw new InvalidOperationException("The app-server connection is not initialized.");

    /// <summary>Launches the app-server, performs the <c>initialize</c> handshake, and returns the client.</summary>
    public static async Task<Codex> StartAsync(CodexConfig? config = null, CancellationToken ct = default)
    {
        var client = await CodexAppServerClient.StartAsync((config ?? new CodexConfig()).ToClientOptions(), ct).ConfigureAwait(false);
        return new Codex(client);
    }

    /// <summary>Wraps an already initialized client; the returned instance takes ownership of it.</summary>
    internal static Codex FromClient(CodexAppServerClient client) =>
        new(client ?? throw new ArgumentNullException(nameof(client)));

    /// <summary>Authenticates with an API key (<c>account/login/start</c> with <c>type: apiKey</c>).</summary>
    public async Task LoginApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _ = await Client.StartAccountLoginAsync(new AccountLoginStartOptions.ApiKey(apiKey), ct).ConfigureAwait(false);
    }

    /// <summary>Starts browser-based ChatGPT login and returns its live handle.</summary>
    public async Task<CodexChatgptLogin> LoginChatgptAsync(CancellationToken ct = default)
    {
        EnsureLoginPump();
        var result = await Client.StartAccountLoginAsync(new AccountLoginStartOptions.ChatGptBrowser(), ct).ConfigureAwait(false);
        return result is AccountLoginStartResult.ChatGptBrowser browser
            ? new CodexChatgptLogin(this, browser.LoginId, browser.AuthUrl)
            : throw new InvalidOperationException($"Unexpected ChatGPT login response: {result.Raw}");
    }

    /// <summary>Starts device-code ChatGPT login and returns its live handle.</summary>
    public async Task<CodexDeviceCodeLogin> LoginChatgptDeviceCodeAsync(CancellationToken ct = default)
    {
        EnsureLoginPump();
        var result = await Client.StartAccountLoginAsync(new AccountLoginStartOptions.ChatGptDeviceCode(), ct).ConfigureAwait(false);
        return result is AccountLoginStartResult.ChatGptDeviceCode device
            ? new CodexDeviceCodeLogin(this, device.LoginId, device.VerificationUrl, device.UserCode)
            : throw new InvalidOperationException($"Unexpected device-code login response: {result.Raw}");
    }

    /// <summary>Reads the current account state.</summary>
    public Task<AccountReadResult> AccountAsync(bool refreshToken = false, CancellationToken ct = default) =>
        Client.ReadAccountAsync(new AccountReadOptions { RefreshToken = refreshToken }, ct);

    /// <summary>Clears the current account session.</summary>
    public async Task LogoutAsync(CancellationToken ct = default) =>
        _ = await Client.LogoutAccountAsync(ct).ConfigureAwait(false);

    /// <summary>Creates a new conversation thread. The default approval mode is <see cref="CodexApprovalMode.AutoReview"/>.</summary>
    public async Task<CodexConversation> ThreadStartAsync(CodexThreadStartOptions? options = null, CancellationToken ct = default)
    {
        var thread = await Client.StartThreadAsync((options ?? new CodexThreadStartOptions()).ToWire(), ct).ConfigureAwait(false);
        return new CodexConversation(Client, thread.Id);
    }

    /// <summary>Resumes an existing conversation thread by identifier.</summary>
    public async Task<CodexConversation> ThreadResumeAsync(string threadId, CodexThreadResumeOptions? options = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        var thread = await Client.ResumeThreadAsync((options ?? new CodexThreadResumeOptions()).ToWire(threadId), ct).ConfigureAwait(false);
        return new CodexConversation(Client, thread.Id);
    }

    /// <summary>Creates a new thread from an existing thread.</summary>
    public async Task<CodexConversation> ThreadForkAsync(string threadId, CodexThreadForkOptions? options = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        var thread = await Client.ForkThreadAsync((options ?? new CodexThreadForkOptions()).ToWire(threadId), ct).ConfigureAwait(false);
        return new CodexConversation(Client, thread.Id);
    }

    /// <summary>Lists saved conversation threads.</summary>
    public Task<CodexThreadListPage> ThreadListAsync(ThreadListOptions? options = null, CancellationToken ct = default) =>
        Client.ListThreadsAsync(options ?? new ThreadListOptions(), ct);

    /// <summary>Archives a stored conversation thread.</summary>
    public Task<ThreadArchiveResult> ThreadArchiveAsync(string threadId, CancellationToken ct = default) =>
        Client.ArchiveThreadAsync(threadId, ct);

    /// <summary>Restores an archived conversation thread.</summary>
    public async Task<CodexConversation> ThreadUnarchiveAsync(string threadId, CancellationToken ct = default)
    {
        var thread = await Client.UnarchiveThreadAsync(threadId, ct).ConfigureAwait(false);
        return new CodexConversation(Client, thread.Id);
    }

    /// <summary>Returns a handle for an existing thread without a server round trip.</summary>
    public CodexConversation Thread(string threadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        return new CodexConversation(Client, threadId);
    }

    /// <summary>Lists available models.</summary>
    public Task<ModelListResult> ModelsAsync(bool includeHidden = false, CancellationToken ct = default) =>
        Client.ListModelsAsync(new ModelListOptions { IncludeHidden = includeHidden }, ct);

    /// <summary>Closes the app-server connection.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed.IsCancellationRequested) return;
        _disposed.Cancel();
        foreach (var login in _logins.Values) login.TrySetCanceled();
        await Client.DisposeAsync().ConfigureAwait(false);
        _disposed.Dispose();
    }

    internal Task<AccountLoginCompletedNotification> WaitForLoginAsync(string loginId, CancellationToken ct) =>
        Login(loginId).Task.WaitAsync(ct);

    private TaskCompletionSource<AccountLoginCompletedNotification> Login(string loginId) =>
        _logins.GetOrAdd(loginId, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

    /// <summary>
    /// Routes <c>account/login/completed</c> by login id. Once started, this pump is the sole reader of
    /// <see cref="CodexAppServerClient.Notifications"/>; completions arriving before a wait are retained.
    /// </summary>
    private void EnsureLoginPump()
    {
        ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, this);
        lock (_pumpLock)
        {
            _loginPump ??= Task.Run(async () =>
            {
                try
                {
                    await foreach (var notification in Client.Notifications(_disposed.Token).ConfigureAwait(false))
                        if (notification is AccountLoginCompletedNotification { LoginId: { } id } completed)
                            Login(id).TrySetResult(completed);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    foreach (var login in _logins.Values) login.TrySetException(ex);
                }
            });
        }
    }
}

/// <summary>Live browser-login attempt returned by <see cref="Codex.LoginChatgptAsync"/>.</summary>
public sealed class CodexChatgptLogin
{
    private readonly Codex _owner;
    internal CodexChatgptLogin(Codex owner, string loginId, string authUrl)
    { _owner = owner; LoginId = loginId; AuthUrl = authUrl; }

    /// <summary>Gets the login attempt identifier.</summary>
    public string LoginId { get; }
    /// <summary>Gets the URL the user opens to authenticate.</summary>
    public string AuthUrl { get; }
    /// <summary>Waits for this attempt's <c>account/login/completed</c> notification.</summary>
    public Task<AccountLoginCompletedNotification> WaitAsync(CancellationToken ct = default) => _owner.WaitForLoginAsync(LoginId, ct);
    /// <summary>Cancels this login attempt.</summary>
    public Task<AccountLoginCancelResult> CancelAsync(CancellationToken ct = default) => _owner.Client.CancelAccountLoginAsync(LoginId, ct);
}

/// <summary>Live device-code login attempt returned by <see cref="Codex.LoginChatgptDeviceCodeAsync"/>.</summary>
public sealed class CodexDeviceCodeLogin
{
    private readonly Codex _owner;
    internal CodexDeviceCodeLogin(Codex owner, string loginId, string verificationUrl, string userCode)
    { _owner = owner; LoginId = loginId; VerificationUrl = verificationUrl; UserCode = userCode; }

    /// <summary>Gets the login attempt identifier.</summary>
    public string LoginId { get; }
    /// <summary>Gets the URL where the user enters <see cref="UserCode"/>.</summary>
    public string VerificationUrl { get; }
    /// <summary>Gets the one-time code to enter.</summary>
    public string UserCode { get; }
    /// <summary>Waits for this attempt's <c>account/login/completed</c> notification.</summary>
    public Task<AccountLoginCompletedNotification> WaitAsync(CancellationToken ct = default) => _owner.WaitForLoginAsync(LoginId, ct);
    /// <summary>Cancels this login attempt.</summary>
    public Task<AccountLoginCancelResult> CancelAsync(CancellationToken ct = default) => _owner.Client.CancelAccountLoginAsync(LoginId, ct);
}
