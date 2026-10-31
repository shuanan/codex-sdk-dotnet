using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using JKToolKit.CodexSDK.AppServer;
using JKToolKit.CodexSDK.Infrastructure.JsonRpc;
using JKToolKit.CodexSDK.Infrastructure.Stdio;
using Microsoft.Extensions.Logging.Abstractions;

namespace JKToolKit.CodexSDK.Tests.Unit.HighLevel;

/// <summary>
/// Replays server traffic recorded from the official Python SDK (Fixtures/python-sdk-golden/golden.json)
/// to the C# <see cref="Codex"/> facade, recording every message the facade writes.
/// </summary>
internal static class PythonGolden
{
    private static readonly Lazy<JsonArray> Scenarios = new(() => JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "python-sdk-golden", "golden.json")))!.AsArray());

    public static JsonObject Scenario(string name) =>
        Scenarios.Value.Select(s => s!.AsObject()).Single(s => (string)s["name"]! == name);

    /// <summary>Python's client name/title/version, so the initialize request is comparable.</summary>
    public static CodexConfig PythonClientConfig => new()
    {
        ClientName = "codex_python_sdk", ClientTitle = "Codex Python SDK", ClientVersion = "0.0.0.dev0"
    };

    public static async Task<(Codex Codex, ReplayTransport Transport)> StartAsync(string scenario, CodexConfig? config = null)
    {
        var transport = new ReplayTransport(Scenario(scenario));
        var options = (config ?? PythonClientConfig).ToClientOptions();
        var serializer = CodexAppServerClient.CreateDefaultSerializerOptions();
        // Same wiring as CodexAppServerClient.StartStdioAsync: no "jsonrpc" header, default serializer.
        var rpc = new JsonRpcConnection(transport, includeJsonRpcHeader: false, options.NotificationBufferCapacity, serializer, NullLogger.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var client = await CodexAppServerClient.CreateInitializedAsync(
            options, new FakeProcess(), rpc, NullLogger.Instance, serializer, timeout.Token);
        return (Codex.FromClient(client), transport);
    }

    /// <summary>Sorted-key JSON so object property order never matters in comparisons.</summary>
    public static string Canon(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(",", o.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Key) + ":" + Canon(p.Value))) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Canon)) + "]",
        _ => node.ToJsonString()
    };

    /// <summary>A process that "exits" when disposed, so the client's exit watcher and shutdown complete.</summary>
    private sealed class FakeProcess : IStdioProcess
    {
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Completion => _exited.Task;
        public int? ProcessId => 1;
        public int? ExitCode => _exited.Task.IsCompleted ? 0 : null;
        public IReadOnlyList<string> StderrTail => Array.Empty<string>();
        public ValueTask DisposeAsync()
        {
            _exited.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A transport that answers the n-th client request with the server messages recorded after Python's n-th request.</summary>
internal sealed class ReplayTransport : IJsonRpcMessageTransport
{
    private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();
    private readonly List<(int After, bool Response, JsonObject Message)> _script;
    private readonly object _gate = new();
    private int _requests = -1;

    public ReplayTransport(JsonObject scenario)
    {
        _script = scenario["replay"]!.AsArray().Select(e => e!.AsObject())
            .Select(e => ((int)e["after_request"]!, (bool)e["response"]!, e["msg"]!.AsObject())).ToList();
    }

    /// <summary>Gets every message the client wrote, in order.</summary>
    public List<JsonObject> Sent { get; } = [];

    public Task Completion => _incoming.Reader.Completion;

    public Task SendAsync(string message, CancellationToken ct)
    {
        var parsed = JsonNode.Parse(message)!.AsObject();
        lock (_gate)
        {
            Sent.Add(parsed);
            if (parsed["id"] is not { } id || parsed["method"] is null) return Task.CompletedTask;
            var index = ++_requests;
            foreach (var (after, response, recorded) in _script.Where(e => e.After == index))
            {
                var outgoing = recorded.DeepClone().AsObject();
                if (response) outgoing["id"] = id.DeepClone();
                _incoming.Writer.TryWrite(outgoing.ToJsonString());
            }
        }
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<string> ReceiveAsync(CancellationToken ct) => _incoming.Reader.ReadAllAsync(ct);

    public ValueTask DisposeAsync()
    {
        _incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
