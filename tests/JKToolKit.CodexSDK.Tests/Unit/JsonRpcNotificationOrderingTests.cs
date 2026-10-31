using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using JKToolKit.CodexSDK.AppServer;
using JKToolKit.CodexSDK.AppServer.Notifications;
using JKToolKit.CodexSDK.Infrastructure.JsonRpc;
using JKToolKit.CodexSDK.Infrastructure.JsonRpc.Messages;
using JKToolKit.CodexSDK.Infrastructure.Stdio;
using Microsoft.Extensions.Logging.Abstractions;

namespace JKToolKit.CodexSDK.Tests.Unit;

/// <summary>
/// Regression tests for app-server notifications reaching handlers out of wire order (codex 0.163:
/// deltas 'ODE','X','-' on the wire surfaced as 'ODE','-','X' from CodexTurnHandle.Events()).
/// </summary>
public sealed class JsonRpcNotificationOrderingTests
{
    private const int DeltaCount = 500;

    [Fact]
    public async Task Notifications_ReachHandlerInWireOrder_EvenWhenHandlerAwaitsRandomDelays()
    {
        var transport = new ScriptedTransport();
        await using var rpc = new JsonRpcConnection(transport, true, DeltaCount * 2, null, NullLogger.Instance);

        var seen = new List<int>();
        var concurrent = 0;
        var maxConcurrent = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var random = new Random(1234);
        rpc.OnNotification += async n =>
        {
            var now = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref maxConcurrent, now);
            int delay;
            lock (random) delay = random.Next(0, 3);
            if (delay > 0) await Task.Delay(delay); else await Task.Yield();
            lock (seen)
            {
                seen.Add(n.Params!.Value.GetProperty("seq").GetInt32());
                if (seen.Count == DeltaCount) done.TrySetResult();
            }
            Interlocked.Decrement(ref concurrent);
        };

        for (var i = 0; i < DeltaCount; i++)
        {
            transport.Push(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "note", @params = new { seq = i } }));
        }

        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        seen.Should().Equal(Enumerable.Range(0, DeltaCount));
        maxConcurrent.Should().Be(1, "notification handlers must run one at a time");
    }

    [Fact]
    public async Task SlowNotificationHandler_DoesNotBlockResponsesOrServerRequests()
    {
        var transport = new ScriptedTransport();
        await using var rpc = new JsonRpcConnection(transport, true, 10, null, NullLogger.Instance);

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handled = new List<string>();
        rpc.OnNotification += async n =>
        {
            if (n.Method == "slow") await release.Task;
            lock (handled) handled.Add(n.Method);
        };
        rpc.OnServerRequest = req =>
        {
            using var doc = JsonDocument.Parse("""{"decision":"accept"}""");
            return ValueTask.FromResult(new JsonRpcResponse(req.Id, doc.RootElement.Clone(), Error: null));
        };

        transport.Push("""{"jsonrpc":"2.0","method":"slow","params":{}}""");
        transport.Push("""{"jsonrpc":"2.0","method":"after","params":{}}""");

        // A server request (approval) arriving while the handler is stuck must still be answered.
        transport.Push("""{"jsonrpc":"2.0","id":42,"method":"approval/request","params":{}}""");
        using (var approval = JsonDocument.Parse(await transport.NextSentAsync().WaitAsync(TimeSpan.FromSeconds(5))))
        {
            approval.RootElement.GetProperty("id").GetInt32().Should().Be(42);
            approval.RootElement.GetProperty("result").GetProperty("decision").GetString().Should().Be("accept");
        }

        // An RPC response arriving while the handler is stuck must still complete the request.
        var request = rpc.SendRequestAsync("ping", null, CancellationToken.None);
        using (var sent = JsonDocument.Parse(await transport.NextSentAsync().WaitAsync(TimeSpan.FromSeconds(5))))
        {
            transport.Push($$$"""{"jsonrpc":"2.0","id":{{{sent.RootElement.GetProperty("id").GetRawText()}}},"result":{"ok":true}}""");
        }
        (await request.WaitAsync(TimeSpan.FromSeconds(5))).GetProperty("ok").GetBoolean().Should().BeTrue();

        lock (handled) handled.Should().BeEmpty("the slow handler is still running and later notifications queue behind it");
        release.SetResult();

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            lock (handled) if (handled.Count == 2) break;
            await Task.Delay(10);
        }
        lock (handled) handled.Should().Equal("slow", "after");
    }

    [Fact]
    public async Task NotificationHandlerExceptions_AreSwallowed_AndLaterNotificationsStillDispatch()
    {
        var transport = new ScriptedTransport();
        await using var rpc = new JsonRpcConnection(transport, true, 10, null, NullLogger.Instance);
        var second = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        rpc.OnNotification += n =>
        {
            if (n.Method == "boom") throw new InvalidOperationException("boom");
            second.TrySetResult(n.Method);
            return ValueTask.CompletedTask;
        };

        transport.Push("""{"jsonrpc":"2.0","method":"boom","params":{}}""");
        transport.Push("""{"jsonrpc":"2.0","method":"ok","params":{}}""");

        (await second.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be("ok");
    }

    [Fact]
    public async Task NotificationsReceivedBeforeRemoteClose_AreAllDispatched()
    {
        var transport = new ScriptedTransport();
        await using var rpc = new JsonRpcConnection(transport, true, 10, null, NullLogger.Instance);
        var seen = new List<int>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rpc.OnNotification += async n =>
        {
            await Task.Yield();
            lock (seen)
            {
                seen.Add(n.Params!.Value.GetProperty("seq").GetInt32());
                if (seen.Count == 50) done.TrySetResult();
            }
        };

        for (var i = 0; i < 50; i++)
        {
            transport.Push(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "note", @params = new { seq = i } }));
        }
        transport.CloseIncoming();

        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        seen.Should().Equal(Enumerable.Range(0, 50));
    }

    [Fact]
    public async Task TurnEvents_YieldAgentMessageDeltasInWireOrder()
    {
        var transport = new ScriptedTransport(respond: true);
        await using var rpc = new JsonRpcConnection(transport, true, DeltaCount * 2, null, NullLogger.Instance);
        await using var client = new CodexAppServerClient(
            new CodexAppServerClientOptions { NotificationBufferCapacity = DeltaCount * 2 },
            new FakeProcess(),
            rpc,
            NullLogger.Instance,
            startExitWatcher: false);

        await using var turn = await client.StartTurnAsync("thread-1", new TurnStartOptions { Input = [TurnInputItem.Text("go")] });
        turn.TurnId.Should().Be("turn-1");

        var expected = Enumerable.Range(0, DeltaCount).Select(i => $"d{i};").ToArray();
        foreach (var delta in expected)
        {
            transport.Push(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                method = "item/agentMessage/delta",
                @params = new { threadId = "thread-1", turnId = "turn-1", itemId = "item-1", delta }
            }));
        }
        transport.Push(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "turn/completed",
            @params = new { threadId = "thread-1", turn = new { id = "turn-1", status = "completed" } }
        }));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var deltas = new List<string>();
        await foreach (var evt in turn.Events(cts.Token))
        {
            if (evt is AgentMessageDeltaNotification d) deltas.Add(d.Delta);
            if (evt is TurnCompletedNotification) break;
        }

        deltas.Should().Equal(expected);
        string.Concat(deltas).Should().Be(string.Concat(expected));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value &&
               Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private sealed class FakeProcess : IStdioProcess
    {
        public Task Completion { get; } = new TaskCompletionSource().Task;
        public int? ProcessId => 1;
        public int? ExitCode => null;
        public IReadOnlyList<string> StderrTail => Array.Empty<string>();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScriptedTransport(bool respond = false) : IJsonRpcMessageTransport
    {
        private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();
        private readonly Channel<string> _sent = Channel.CreateUnbounded<string>();

        public Task Completion => _incoming.Reader.Completion;

        public void Push(string line) => _incoming.Writer.TryWrite(line);

        public void CloseIncoming() => _incoming.Writer.TryComplete();

        public async Task<string> NextSentAsync() => await _sent.Reader.ReadAsync();

        public Task SendAsync(string message, CancellationToken ct)
        {
            if (respond)
            {
                using var doc = JsonDocument.Parse(message);
                if (doc.RootElement.TryGetProperty("id", out var id) && doc.RootElement.TryGetProperty("method", out _))
                {
                    _incoming.Writer.TryWrite($$$$"""{"id":{{{{id.GetRawText()}}}},"result":{"turn":{"id":"turn-1","status":"inProgress"}}}""");
                }
                return Task.CompletedTask;
            }

            _sent.Writer.TryWrite(message);
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<string> ReceiveAsync(CancellationToken ct) => _incoming.Reader.ReadAllAsync(ct);

        public ValueTask DisposeAsync()
        {
            _incoming.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
