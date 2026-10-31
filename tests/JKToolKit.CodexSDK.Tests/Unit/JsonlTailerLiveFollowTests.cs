using System.Text;
using FluentAssertions;
using JKToolKit.CodexSDK.Abstractions;
using JKToolKit.CodexSDK.Exec;
using JKToolKit.CodexSDK.Exec.Notifications;
using JKToolKit.CodexSDK.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JKToolKit.CodexSDK.Tests.Unit;

/// <summary>
/// Regression tests for live exec follow yielding duplicate events (codex 0.163: SessionMetaEvent x3
/// for a log with a single session_meta line). Following a log that is written incrementally must
/// yield each JSONL record exactly once, in order.
/// </summary>
public sealed class JsonlTailerLiveFollowTests
{
    // Synthetic, redacted log with the same record shapes as a real codex 0.163 exec rollout.
    private static readonly string[] SessionLines =
    [
        """{"timestamp":"2026-10-31T13:12:20.128Z","ordinal":0,"type":"session_meta","payload":{"id":"00000000-0000-0000-0000-000000000001","timestamp":"2026-10-31T13:12:20.000Z","cwd":"/redacted","originator":"codex_exec","cli_version":"0.163.0"}}""",
        """{"timestamp":"2026-10-31T13:12:20.128Z","ordinal":1,"type":"event_msg","payload":{"type":"task_started","turn_id":"00000000-0000-0000-0000-0000000000a1","model_context_window":258400}}""",
        """{"timestamp":"2026-10-31T13:12:21.765Z","ordinal":2,"type":"response_item","payload":{"type":"message","id":"msg_redacted_1","role":"developer","content":[{"type":"input_text","text":"redacted"}]}}""",
        """{"timestamp":"2026-10-31T13:12:21.765Z","ordinal":3,"type":"response_item","payload":{"type":"message","id":"msg_redacted_2","role":"user","content":[{"type":"input_text","text":"redacted"}]}}""",
        """{"timestamp":"2026-10-31T13:12:21.768Z","ordinal":4,"type":"world_state","payload":{"full":true,"state":{}}}""",
        """{"timestamp":"2026-10-31T13:12:21.770Z","ordinal":5,"type":"turn_context","payload":{"turn_id":"00000000-0000-0000-0000-0000000000a1","cwd":"/redacted","approval_policy":"never","model":"redacted"}}""",
        """{"timestamp":"2026-10-31T13:12:21.770Z","ordinal":6,"type":"event_msg","payload":{"type":"thread_settings_applied","thread_id":"00000000-0000-0000-0000-000000000001"}}""",
        """{"timestamp":"2026-10-31T13:12:21.908Z","ordinal":7,"type":"response_item","payload":{"type":"message","id":"msg_redacted_3","role":"user","content":[{"type":"input_text","text":"Reply with exactly: CSHARP-CODEX-42"}]}}""",
        """{"timestamp":"2026-10-31T13:12:21.910Z","ordinal":8,"type":"event_msg","payload":{"type":"item_completed","thread_id":"00000000-0000-0000-0000-000000000001","turn_id":"00000000-0000-0000-0000-0000000000a1","item":{"type":"UserMessage","id":"item_1"}}}""",
        """{"timestamp":"2026-10-31T13:12:22.618Z","ordinal":9,"type":"event_msg","payload":{"type":"item_completed","thread_id":"00000000-0000-0000-0000-000000000001","turn_id":"00000000-0000-0000-0000-0000000000a1","item":{"type":"AgentMessage","id":"item_2"}}}""",
        """{"timestamp":"2026-10-31T13:12:22.621Z","ordinal":10,"type":"response_item","payload":{"type":"message","status":"completed","id":"msg_redacted_4","role":"assistant","content":[{"type":"output_text","text":"CSHARP-CODEX-42"}]}}""",
        """{"timestamp":"2026-10-31T13:12:22.623Z","ordinal":11,"type":"token_usage_record","payload":{"thread_id":"00000000-0000-0000-0000-000000000001","turn_id":"00000000-0000-0000-0000-0000000000a1"}}""",
        """{"timestamp":"2026-10-31T13:12:22.624Z","ordinal":12,"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":15,"output_tokens":5,"total_tokens":20}}}}""",
        """{"timestamp":"2026-10-31T13:12:22.696Z","ordinal":13,"type":"event_msg","payload":{"type":"task_complete","turn_id":"00000000-0000-0000-0000-0000000000a1","last_agent_message":"CSHARP-CODEX-42"}}""",
    ];

    [Fact]
    public async Task LiveFollow_WriterAppendsBetweenMetadataReads_YieldsEachLineOnce()
    {
        // Appending between the tailer's path-size read and its handle-length read made the two
        // lengths differ, which was misread as rotation and replayed the file from offset 0.
        await WithTempFileAsync(async path =>
        {
            var fs = new AppendDuringMetadataFileSystem(path, SessionLines.Skip(1).Select(l => l + "\n"));
            await File.WriteAllTextAsync(path, SessionLines[0] + "\n");

            var lines = await FollowUntilAsync(CreateTailer(fs), path, SessionLines.Length);

            lines.Should().Equal(SessionLines);
            fs.Appended.Should().Be(SessionLines.Length - 1);
        });
    }

    [Fact]
    public async Task LiveFollow_CreationTimeMovesOnEveryWrite_YieldsEachLineOnce()
    {
        // Platforms without a birth time report a "creation" time derived from mtime/ctime, so it
        // changes on every append. That alone must not be treated as the file being replaced.
        await WithTempFileAsync(async path =>
        {
            var fs = new WriteTimeAsCreationTimeFileSystem();
            await File.WriteAllTextAsync(path, SessionLines[0] + "\n");

            var writer = WriteIncrementallyAsync(path, SessionLines.Skip(1), seed: 7);
            var lines = await FollowUntilAsync(CreateTailer(fs), path, SessionLines.Length);
            await writer;

            lines.Should().Equal(SessionLines);
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task LiveFollow_IncrementalChunkedWrites_ParsedEventsMatchNonFollowRead(int seed)
    {
        await WithTempFileAsync(async path =>
        {
            await File.WriteAllTextAsync(path, string.Empty);
            var tailer = CreateTailer(new RealFileSystem());
            var parser = new JsonlEventParser(NullLogger<JsonlEventParser>.Instance);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var live = new List<CodexEvent>();
            var writer = WriteIncrementallyAsync(path, SessionLines, seed);

            await foreach (var evt in parser.ParseAsync(tailer.TailAsync(path, EventStreamOptions.Default, cts.Token), cts.Token))
            {
                live.Add(evt);
                if (evt is TaskCompleteEvent)
                {
                    break;
                }
            }

            await writer;

            // Keep following briefly: a replay would surface as extra events after task_complete.
            using var quiet = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            try
            {
                await foreach (var evt in parser.ParseAsync(tailer.TailAsync(path, EventStreamOptions.Default with { FromByteOffset = new FileInfo(path).Length }, quiet.Token), quiet.Token))
                {
                    live.Add(evt);
                }
            }
            catch (OperationCanceledException)
            {
                // expected
            }

            var snapshot = new List<CodexEvent>();
            await foreach (var evt in parser.ParseAsync(tailer.TailAsync(path, new EventStreamOptions { Follow = false }, cts.Token), cts.Token))
            {
                snapshot.Add(evt);
            }

            snapshot.OfType<SessionMetaEvent>().Should().ContainSingle();
            live.Select(e => (e.GetType(), e.Type, e.Timestamp))
                .Should().Equal(snapshot.Select(e => (e.GetType(), e.Type, e.Timestamp)));
        });
    }

    [Fact]
    public async Task LiveFollow_ManyLinesWrittenInRandomPieces_YieldsEachLineOnceInOrder()
    {
        await WithTempFileAsync(async path =>
        {
            await File.WriteAllTextAsync(path, string.Empty);
            var expected = Enumerable.Range(0, 400)
                .Select(i => $$$"""{"timestamp":"2026-10-31T13:12:20.128Z","ordinal":{{{i}}},"type":"event_msg","payload":{"type":"agent_message","message":"{{{new string('x', i % 97)}}}"}}""")
                .ToArray();

            var writer = WriteIncrementallyAsync(path, expected, seed: 42);
            var lines = await FollowUntilAsync(CreateTailer(new RealFileSystem()), path, expected.Length);
            await writer;

            lines.Should().Equal(expected);
        });
    }

    private static JsonlTailer CreateTailer(IFileSystem fs) => new(
        fs,
        NullLogger<JsonlTailer>.Instance,
        Options.Create(new CodexClientOptions { TailPollInterval = TimeSpan.FromMilliseconds(50) }));

    private static async Task<List<string>> FollowUntilAsync(JsonlTailer tailer, string path, int expectedCount)
    {
        var lines = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await foreach (var line in tailer.TailAsync(path, EventStreamOptions.Default, cts.Token))
        {
            lines.Add(line);
            if (lines.Count >= expectedCount)
            {
                break;
            }
        }

        // Anything yielded after the expected lines would be a replay; give it a chance to show up.
        using var quiet = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            await foreach (var line in tailer.TailAsync(path, EventStreamOptions.Default with { FromByteOffset = new FileInfo(path).Length }, quiet.Token))
            {
                lines.Add(line);
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }

        return lines;
    }

    /// <summary>
    /// Appends each line in 1-3 pieces (splitting mid-line and mid-JSON) with small random delays,
    /// flushing every piece, like a live Codex writer.
    /// </summary>
    private static Task WriteIncrementallyAsync(string path, IEnumerable<string> lines, int seed) => Task.Run(async () =>
    {
        var random = new Random(seed);
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        foreach (var line in lines)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            var offset = 0;
            while (offset < bytes.Length)
            {
                var size = random.Next(2) == 0 ? bytes.Length - offset : random.Next(1, bytes.Length - offset + 1);
                await stream.WriteAsync(bytes.AsMemory(offset, size));
                await stream.FlushAsync();
                offset += size;
                if (random.Next(3) == 0)
                {
                    await Task.Delay(random.Next(0, 4));
                }
            }
        }
    });

    private static async Task WithTempFileAsync(Func<string, Task> action)
    {
        var path = Path.Combine(Path.GetTempPath(), $"live-follow-{Guid.NewGuid():N}.jsonl");
        try
        {
            await action(path);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Appends the next pending line while the tailer reads the creation time, i.e. after it sampled the
    /// path size and before it samples the open handle's length.
    /// </summary>
    private sealed class AppendDuringMetadataFileSystem(string file, IEnumerable<string> pending) : IFileSystem
    {
        private readonly Queue<string> _pending = new(pending);
        private bool _sizeSampled;

        public int Appended { get; private set; }
        public bool FileExists(string path) => File.Exists(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public IEnumerable<string> GetFiles(string directory, string searchPattern) => throw new NotSupportedException();
        public Stream OpenRead(string path) => File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        public long GetFileSize(string path)
        {
            _sizeSampled = true;
            return new FileInfo(path).Length;
        }

        public DateTime GetFileCreationTimeUtc(string path)
        {
            if (_sizeSampled && _pending.Count > 0)
            {
                File.AppendAllText(file, _pending.Dequeue());
                Appended++;
            }

            _sizeSampled = false;
            return new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }
    }

    private sealed class WriteTimeAsCreationTimeFileSystem : IFileSystem
    {
        public bool FileExists(string path) => File.Exists(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public IEnumerable<string> GetFiles(string directory, string searchPattern) => throw new NotSupportedException();
        public Stream OpenRead(string path) => File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        public long GetFileSize(string path) => new FileInfo(path).Length;

        // Length is folded in so each append is guaranteed to move the value even within one timer tick.
        public DateTime GetFileCreationTimeUtc(string path)
        {
            var info = new FileInfo(path);
            return info.LastWriteTimeUtc.AddTicks(info.Length);
        }
    }
}
