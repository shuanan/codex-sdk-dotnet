using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using JKToolKit.CodexSDK.AppServer;
using JKToolKit.CodexSDK.AppServer.Notifications;
using static JKToolKit.CodexSDK.Tests.Unit.HighLevel.PythonGolden;

namespace JKToolKit.CodexSDK.Tests.Unit.HighLevel;

/// <summary>
/// Parity with the official Python SDK: for the same canned server traffic the C# facade must send the
/// same JSON-RPC requests (method + params shape) and produce the same TurnResult values that Python did.
/// Intentional differences are normalized explicitly in <see cref="ExpectedClient"/> and <see cref="ExpectedResults"/>.
/// </summary>
public sealed class PythonSdkGoldenParityTests
{
    [Fact]
    public Task Initialize() => Verify("initialize", async (codex, record) =>
    {
        record("metadata", Node(codex.Metadata.Raw));
        await Task.CompletedTask;
    });

    [Fact]
    public Task Account() => Verify("account", async (codex, record) =>
    {
        await codex.LoginApiKeyAsync("sk-test-123");
        record("login_api_key", null);
        record("account", Node((await codex.AccountAsync()).Raw));
        record("account_refresh", Node((await codex.AccountAsync(refreshToken: true)).Raw));
        await codex.LogoutAsync();
        record("logout", null);
    });

    [Fact]
    public Task ChatgptLoginHandles() => Verify("login_chatgpt", async (codex, record) =>
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var browser = await codex.LoginChatgptAsync();
        record("browser", new JsonObject { ["auth_url"] = browser.AuthUrl, ["login_id"] = browser.LoginId });
        var completed = await browser.WaitAsync(timeout.Token);
        var wait = new JsonObject { ["loginId"] = completed.LoginId, ["success"] = completed.Success };
        if (completed.Error is not null) wait["error"] = completed.Error;
        record("browser_wait", wait);
        var device = await codex.LoginChatgptDeviceCodeAsync();
        record("device", new JsonObject
        {
            ["login_id"] = device.LoginId, ["user_code"] = device.UserCode, ["verification_url"] = device.VerificationUrl
        });
        record("device_cancel", Node((await device.CancelAsync()).Raw));
    });

    [Fact]
    public async Task Harness_DetectsRequestDifferences()
    {
        // Self-test: the same scenario driven with one extra turn option must not pass.
        var act = () => Verify("run_text", async (codex, record) =>
        {
            var thread = await codex.ThreadStartAsync();
            record("result", Result(await thread.RunAsync("hello", new() { Effort = "high" })));
        });
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("effort");
    }

    [Fact]
    public Task ThreadStart() => Verify("thread_start", async (codex, record) =>
    {
        record("default", (await codex.ThreadStartAsync()).Id);
        record("options", (await codex.ThreadStartAsync(new()
        {
            Model = "gpt-5.5", Cwd = "/repo", Ephemeral = true, BaseInstructions = "Base rules.",
            DeveloperInstructions = "Dev rules.", Personality = "pragmatic", ServiceTier = "fast",
            ModelProvider = "openai", ServiceName = "golden", Config = Json("""{"model_reasoning_effort":"high"}""")
        })).Id);
        foreach (var (name, sandbox) in Sandboxes)
            record($"sandbox={name}", (await codex.ThreadStartAsync(new() { Sandbox = sandbox })).Id);
        record("approval_mode=deny_all", (await codex.ThreadStartAsync(new() { ApprovalMode = CodexApprovalMode.DenyAll })).Id);
        record("approval_mode=auto_review", (await codex.ThreadStartAsync(new() { ApprovalMode = CodexApprovalMode.AutoReview })).Id);
    });

    [Fact]
    public Task RunText() => Verify("run_text", async (codex, record) =>
    {
        var thread = await codex.ThreadStartAsync();
        record("result", Result(await thread.RunAsync("hello")));
    });

    [Fact]
    public Task RunListInputWithOptions() => Verify("run_list_options", async (codex, record) =>
    {
        var thread = await codex.ThreadStartAsync();
        var result = await thread.RunAsync(
            new[]
            {
                TurnInputItem.Text("describe"), TurnInputItem.ImageUrl("https://example.com/cat.png"),
                TurnInputItem.LocalImage("/tmp/dog.png"), TurnInputItem.Skill("review", "/skills/review/SKILL.md"),
                TurnInputItem.Mention("repo", "app://repo")
            },
            new CodexTurnOptions
            {
                OutputSchema = Json("""{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"],"additionalProperties":false}"""),
                Effort = "high", Model = "gpt-5.5", Cwd = "/repo", ApprovalMode = CodexApprovalMode.DenyAll,
                Sandbox = CodexSandbox.ReadOnly, Personality = "friendly", ServiceTier = "flex", Summary = "concise",
                Source = "golden-test", TurnServiceTier = "priority"
            });
        record("result", Result(result));
    });

    [Fact]
    public Task RunTurnSandboxAndApproval() => Verify("run_turn_sandbox", async (codex, record) =>
    {
        var thread = await codex.ThreadStartAsync();
        foreach (var (name, sandbox) in Sandboxes)
            record($"sandbox={name}", Result(await thread.RunAsync("go", new() { Sandbox = sandbox })));
        record("approval_mode=auto_review", Result(await thread.RunAsync("go", new() { ApprovalMode = CodexApprovalMode.AutoReview })));
    });

    [Fact]
    public Task FinalResponseSelection() => Verify("final_response", async (codex, record) =>
    {
        var thread = await codex.ThreadStartAsync();
        foreach (var text in new[] { "phases", "commentary", "unlabelled" })
            record(text, Result(await thread.RunAsync(text)));
    });

    [Fact]
    public Task FailedTurnThrows() => Verify("run_failed", async (codex, record) =>
    {
        var thread = await codex.ThreadStartAsync();
        var act = () => thread.RunAsync("fail");
        var error = (await act.Should().ThrowAsync<CodexTurnFailedException>()).Which;
        error.Result.Status.Should().Be("failed");
        // Python raises RuntimeError; the C# equivalent is CodexTurnFailedException with the same message.
        record("raised", new JsonObject { ["message"] = error.Message, ["type"] = "RuntimeError" });
    });

    [Fact]
    public Task ExternalMessage() => Verify("external_message", async (codex, record) =>
    {
        var thread = await codex.ThreadStartAsync();
        record("result", Result(await thread.RunAsync(CodexRunInput.ExternalMessage("github", "issue body"))));
    });

    [Fact]
    public Task StreamYieldsOnlyThisTurn() => Verify("stream", async (codex, record) =>
    {
        var thread = await codex.ThreadStartAsync();
        await using var turn = await thread.TurnAsync("stream");
        var events = new List<AppServerNotification>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (var e in turn.StreamAsync(timeout.Token)) events.Add(e);
        record("turn_id", turn.Id);
        record("methods", new JsonArray(events.Select(e => (JsonNode?)e.Method).ToArray()));
        record("deltas", new JsonArray(events.OfType<AgentMessageDeltaNotification>().Select(d => (JsonNode?)d.Delta).ToArray()));
        record("result_via_run", Result(await thread.RunAsync("stream")));
    });

    [Fact]
    public Task Steer() => Verify("steer", async (codex, record) =>
    {
        var thread = await codex.ThreadStartAsync();
        await using var turn = await thread.TurnAsync("wait");
        record("steer", new JsonObject { ["turnId"] = await turn.SteerAsync("more please") });
        record("result", Result(await turn.RunAsync()));
    });

    [Fact]
    public Task Interrupt() => Verify("interrupt", async (codex, record) =>
    {
        var thread = await codex.ThreadStartAsync();
        await using var turn = await thread.TurnAsync("wait");
        await turn.InterruptAsync();
        record("interrupt", new JsonObject());
        record("result", Result(await turn.RunAsync()));
    });

    [Fact]
    public Task Lifecycle() => Verify("lifecycle", async (codex, record) =>
    {
        record("resume", (await codex.ThreadResumeAsync("thread-x")).Id);
        record("resume_options", (await codex.ThreadResumeAsync("thread-x", new()
        {
            IncludeTurns = false, Model = "gpt-5.5", Cwd = "/repo", Sandbox = CodexSandbox.FullAccess,
            ApprovalMode = CodexApprovalMode.DenyAll, Personality = "none", ServiceTier = "fast",
            BaseInstructions = "b", DeveloperInstructions = "d", ModelProvider = "openai"
        })).Id);
        record("fork", (await codex.ThreadForkAsync("thread-x")).Id);
        record("fork_options", (await codex.ThreadForkAsync("thread-x", new()
        {
            Ephemeral = true, IncludeTurns = true, Sandbox = CodexSandbox.ReadOnly,
            ApprovalMode = CodexApprovalMode.AutoReview, Model = "gpt-5.5"
        })).Id);
        var page = await codex.ThreadListAsync();
        record("list", new JsonObject
        {
            ["ids"] = new JsonArray(page.Threads.Select(t => (JsonNode?)t.ThreadId).ToArray()), ["next_cursor"] = page.NextCursor
        });
        record("list_options", (await codex.ThreadListAsync(new()
        {
            Archived = true, Cursor = "c1", Limit = 5, ModelProviders = ["openai"], SearchTerm = "bug",
            SortDirection = "desc", SortKey = "updated_at", SourceKinds = ["cli", "appServer"]
        })).NextCursor);
        record("archive", Node((await codex.ThreadArchiveAsync("thread-a")).Raw));
        record("unarchive", (await codex.ThreadUnarchiveAsync("thread-a")).Id);
    });

    [Fact]
    public Task ThreadOperations() => Verify("thread_ops", async (codex, record) =>
    {
        var thread = await codex.ThreadStartAsync();
        record("read", (await thread.ReadAsync()).Thread.ThreadId);
        record("read_turns", (await thread.ReadAsync(includeTurns: true)).Thread.ThreadId);
        await thread.SetNameAsync("Golden");
        record("set_name", new JsonObject());
        await thread.CompactAsync();
        record("compact", new JsonObject());
    });

    [Fact]
    public Task Models() => Verify("models", async (codex, record) =>
    {
        record("models", new JsonArray((await codex.ModelsAsync()).Data.Select(m => (JsonNode?)m.Id).ToArray()));
        record("models_hidden", new JsonArray((await codex.ModelsAsync(includeHidden: true)).Data.Select(m => (JsonNode?)m.Id).ToArray()));
    });

    private static readonly (string Name, CodexSandbox Sandbox)[] Sandboxes =
        [("read_only", CodexSandbox.ReadOnly), ("workspace_write", CodexSandbox.WorkspaceWrite), ("full_access", CodexSandbox.FullAccess)];

    private static async Task Verify(string scenario, Func<Codex, Action<string, JsonNode?>, Task> drive)
    {
        var (codex, transport) = await StartAsync(scenario);
        var results = new JsonArray();
        await using (codex)
        {
            await drive(codex, (label, value) => results.Add(new JsonObject { ["label"] = label, ["value"] = value }));
        }

        var golden = Scenario(scenario);
        var actualClient = transport.Sent.Where(m => m["method"] is not null).Select(m => Canon(Shape(m))).ToList();
        actualClient.Should().Equal(ExpectedClient(golden), "C# must send the same JSON-RPC messages as Python");
        Canon(results).Should().Be(Canon(ExpectedResults(golden)), "C# must produce the same results as Python");
    }

    /// <summary>The comparable shape of one client message, with documented C# wire differences normalized.</summary>
    private static JsonObject Shape(JsonObject message)
    {
        var parameters = message["params"]?.DeepClone();
        // Documented difference 1: JK's TextUserInput always serializes "text_elements": [] (upstream default).
        if (parameters?["input"] is JsonArray input)
            foreach (var item in input.OfType<JsonObject>())
                if (item["text_elements"] is JsonArray { Count: 0 }) item.Remove("text_elements");
        return new JsonObject
        {
            ["kind"] = message.ContainsKey("id") ? "request" : "notification",
            ["keys"] = new JsonArray(message.Select(p => p.Key).Order(StringComparer.Ordinal).Select(k => (JsonNode?)k).ToArray()),
            ["method"] = (string?)message["method"],
            ["params"] = parameters
        };
    }

    private static List<string> ExpectedClient(JsonObject golden) =>
        golden["client"]!.AsArray().Select(entry =>
        {
            var copy = entry!.DeepClone().AsObject();
            // Documented difference 2: JK omits "excludeTurns": false (include_turns=True); same server default.
            if (copy["params"] is JsonObject p && p["excludeTurns"] is JsonValue v && !v.GetValue<bool>()) p.Remove("excludeTurns");
            return Canon(copy);
        }).ToList();

    private static JsonArray ExpectedResults(JsonObject golden)
    {
        var results = golden["results"]!.DeepClone().AsArray();
        // Documented difference 3: pydantic materializes the unreported cacheWriteInputTokens as 0; C# keeps null.
        foreach (var usage in results.Select(r => r!["value"]).SelectMany(Descendants).OfType<JsonObject>())
            if (usage["cacheWriteInputTokens"] is JsonValue zero && zero.GetValue<long>() == 0) usage.Remove("cacheWriteInputTokens");
        return results;
    }

    private static IEnumerable<JsonNode?> Descendants(JsonNode? node) => node switch
    {
        JsonObject o => new[] { node }.Concat(o.Select(p => p.Value).SelectMany(Descendants)),
        JsonArray a => a.SelectMany(Descendants),
        _ => []
    };

    /// <summary>Python's golden.py turn_result() rendering of a C# result.</summary>
    private static JsonObject Result(CodexRunResult r) => new()
    {
        ["id"] = r.Id, ["status"] = r.Status, ["error"] = r.Error is null ? null : Node(r.Error.Raw),
        ["started_at"] = r.StartedAt, ["completed_at"] = r.CompletedAt, ["duration_ms"] = r.DurationMs,
        ["final_response"] = r.FinalResponse, ["items_count"] = r.Items.Count,
        ["item_types"] = new JsonArray(r.Items.Select(i => (JsonNode?)i.Type).ToArray()),
        ["item_ids"] = new JsonArray(r.Items.Select(i => (JsonNode?)i.Id).ToArray()),
        ["usage"] = r.Usage is null ? null : new JsonObject
        {
            ["last"] = Breakdown(r.Usage.Last), ["total"] = Breakdown(r.Usage.Total), ["modelContextWindow"] = r.Usage.ModelContextWindow
        }
    };

    private static JsonObject? Breakdown(CodexTokenUsage? u)
    {
        if (u is null) return null;
        var o = new JsonObject();
        void Add(string name, long? value) { if (value is not null) o[name] = value; }
        Add("cacheWriteInputTokens", u.CacheWriteInputTokens); Add("cachedInputTokens", u.CachedInputTokens);
        Add("inputTokens", u.InputTokens); Add("outputTokens", u.OutputTokens);
        Add("reasoningOutputTokens", u.ReasoningOutputTokens); Add("totalTokens", u.TotalTokens);
        return o;
    }

    private static JsonNode? Node(JsonElement element) => JsonNode.Parse(element.GetRawText());

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
