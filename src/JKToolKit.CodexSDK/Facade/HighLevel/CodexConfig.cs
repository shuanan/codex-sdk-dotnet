using JKToolKit.CodexSDK.AppServer;
using JKToolKit.CodexSDK.Exec;
using JKToolKit.CodexSDK.Infrastructure;

// ReSharper disable once CheckNamespace
namespace JKToolKit.CodexSDK;

/// <summary>Configuration for launching and identifying the local Codex runtime (Python <c>CodexConfig</c>).</summary>
public sealed record class CodexConfig
{
    /// <summary>Gets an explicit Codex executable path. When null, the SDK's normal resolution applies.</summary>
    public string? CodexBin { get; init; }
    /// <summary>Gets a complete launch command (program followed by arguments) that replaces the default <c>codex app-server</c>.</summary>
    public IReadOnlyList<string>? LaunchArgsOverride { get; init; }
    /// <summary>Gets <c>key=value</c> overrides passed as <c>--config</c> before <c>app-server</c>; ignored with <see cref="LaunchArgsOverride"/>.</summary>
    public IReadOnlyList<string> ConfigOverrides { get; init; } = Array.Empty<string>();
    /// <summary>Gets the app-server process working directory.</summary>
    public string? Cwd { get; init; }
    /// <summary>Gets environment variables added to the inherited process environment.</summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }
    /// <summary>Gets the client name sent during initialization.</summary>
    public string ClientName { get; init; } = ClientInfoDefaults.Name;
    /// <summary>Gets the client title sent during initialization.</summary>
    public string ClientTitle { get; init; } = ClientInfoDefaults.Title;
    /// <summary>Gets the client version sent during initialization.</summary>
    public string ClientVersion { get; init; } = ClientInfoDefaults.Version;
    /// <summary>Gets whether experimental API fields are enabled. Defaults to <see langword="true"/>, as in Python.</summary>
    public bool ExperimentalApi { get; init; } = true;
    /// <summary>
    /// Gets a handler for server approval requests. When null, requests are rejected as unhandled
    /// (Python's default handler accepts command and file-change approvals instead).
    /// </summary>
    public IAppServerApprovalHandler? ApprovalHandler { get; init; }
    /// <summary>Gets an optional hook for advanced low-level client options, applied last.</summary>
    public Action<CodexAppServerClientOptions>? ConfigureClient { get; init; }

    internal CodexAppServerClientOptions ToClientOptions()
    {
        var env = Env ?? new Dictionary<string, string>();
        CodexLaunch launch;
        if (LaunchArgsOverride is not null)
        {
            if (LaunchArgsOverride.Count == 0 || string.IsNullOrWhiteSpace(LaunchArgsOverride[0]))
                throw new ArgumentException("LaunchArgsOverride must start with a program.", nameof(LaunchArgsOverride));
            launch = new CodexLaunch { FileName = LaunchArgsOverride[0], Arguments = LaunchArgsOverride.Skip(1).ToArray() };
        }
        else
        {
            var args = ConfigOverrides.SelectMany(static kv => new[] { "--config", kv }).Append("app-server").ToArray();
            launch = CodexLaunch.CodexOnPath().WithArgs(args);
        }
        var options = new CodexAppServerClientOptions
        {
            Launch = launch with { WorkingDirectory = Cwd, Environment = new Dictionary<string, string>(env) },
            CodexExecutablePath = LaunchArgsOverride is null ? CodexBin : null,
            DefaultClientInfo = new AppServerClientInfo(ClientName, ClientTitle, ClientVersion),
            ExperimentalApi = ExperimentalApi,
            ApprovalHandler = ApprovalHandler
        };
        ConfigureClient?.Invoke(options);
        return options;
    }
}
