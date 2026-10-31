# Python-SDK-style API (`Codex`)

`JKToolKit.CodexSDK.Codex` mirrors the public surface of OpenAI's official Python SDK
(`openai-codex`: `Codex` / `AsyncCodex`) as one async C# class. It is a thin layer over the
existing `CodexAppServerClient`, `CodexTurnHandle`, and `CodexTurnResult`; the
[high-level SDK guide](../high-level-sdk.md) (`CodexSdk`) remains available and unchanged.

## Quickstart

```csharp
using JKToolKit.CodexSDK;

await using var codex = await Codex.StartAsync();
var thread = await codex.ThreadStartAsync();
var result = await thread.RunAsync("Explain this repository in three bullets.");
Console.WriteLine(result.FinalResponse);
```

`thread.RunAsync(...)` returns a `CodexRunResult` (Python `TurnResult`) with the final
response, collected items, and token usage. Existing Codex authentication is reused.

## Authentication

```csharp
await using var codex = await Codex.StartAsync();

var login = await codex.LoginChatgptAsync();          // browser login
Console.WriteLine(login.AuthUrl);
Console.WriteLine((await login.WaitAsync()).Success);

var device = await codex.LoginChatgptDeviceCodeAsync(); // device-code login
Console.WriteLine($"{device.VerificationUrl} {device.UserCode}");
await device.WaitAsync();

await codex.LoginApiKeyAsync("sk-...");                 // API-key login
var account = await codex.AccountAsync();
await codex.LogoutAsync();
```

## Threads, turns, and options

```csharp
var thread = await codex.ThreadStartAsync(new()
{
    Model = "gpt-5.5", Cwd = repo, Sandbox = CodexSandbox.WorkspaceWrite,
    ApprovalMode = CodexApprovalMode.AutoReview   // the default, as in Python
});

var result = await thread.RunAsync(
    new[] { TurnInputItem.Text("Describe this image"), TurnInputItem.LocalImage("/tmp/cat.png") },
    new CodexTurnOptions { Effort = "high", OutputSchema = schema, Sandbox = CodexSandbox.ReadOnly });

await using var turn = await thread.TurnAsync("Refactor the parser");
await foreach (var e in turn.StreamAsync())          // only this turn's notifications
    if (e is AgentMessageDeltaNotification d) Console.Write(d.Delta);
var done = await turn.RunAsync();                    // throws CodexTurnFailedException on "failed"

var resumed = await codex.ThreadResumeAsync(thread.Id);
var forked = await codex.ThreadForkAsync(thread.Id, new() { Ephemeral = true });
var page = await codex.ThreadListAsync(new() { Limit = 20, SortKey = "updated_at" });
var models = await codex.ModelsAsync();
```

Untrusted content from another tool or agent: `CodexRunInput.ExternalMessage("github", text)`
(Python `ExternalMessage`) is sent as standalone `toolOutput` with an empty user input list.

## Python → C# mapping

| Python | C# |
| --- | --- |
| `Codex(config)` / `AsyncCodex(config)` | `await Codex.StartAsync(config)`; `await using` closes |
| `CodexConfig(codex_bin, launch_args_override, config_overrides, cwd, env, client_*, experimental_api)` | `CodexConfig { CodexBin, LaunchArgsOverride, ConfigOverrides, Cwd, Env, ClientName/Title/Version, ExperimentalApi }` |
| `codex.metadata` | `codex.Metadata` (`AppServerInitializeResult`) |
| `login_api_key`, `login_chatgpt`, `login_chatgpt_device_code`, `account`, `logout` | `LoginApiKeyAsync`, `LoginChatgptAsync`, `LoginChatgptDeviceCodeAsync`, `AccountAsync`, `LogoutAsync` |
| `thread_start`, `thread_resume`, `thread_fork`, `thread_list`, `thread_archive`, `thread_unarchive`, `models` | `ThreadStartAsync`, `ThreadResumeAsync`, `ThreadForkAsync`, `ThreadListAsync`, `ThreadArchiveAsync`, `ThreadUnarchiveAsync`, `ModelsAsync` |
| `Thread.id`, `run`, `turn`, `read`, `set_name`, `compact` | `CodexConversation.Id`, `RunAsync`, `TurnAsync`, `ReadAsync`, `SetNameAsync`, `CompactAsync` |
| `TurnHandle.id`, `stream`, `run`, `steer`, `interrupt` | `CodexConversationTurn.Id`, `StreamAsync`, `RunAsync`, `SteerAsync`, `InterruptAsync` |
| `TurnResult` | `CodexRunResult` (`Id, Status, Error, StartedAt, CompletedAt, DurationMs, FinalResponse, Items, Usage`) |
| `Sandbox.read_only/workspace_write/full_access` | `CodexSandbox.ReadOnly/WorkspaceWrite/FullAccess` |
| `ApprovalMode.deny_all/auto_review` | `CodexApprovalMode.DenyAll/AutoReview` |
| `TextInput/ImageInput/LocalImageInput/SkillInput/MentionInput` | `TurnInputItem.Text/ImageUrl/LocalImage/Skill/Mention` |
| `RuntimeError` for a failed turn | `CodexTurnFailedException` (carries the `CodexRunResult`) |

The Python `Thread` type is named `CodexConversation` to avoid clashing with
`System.Threading.Thread` and the existing `CodexThread` lifecycle record.

## Wire parity and intentional differences

Unit tests (`PythonSdkGoldenParityTests`) replay traffic recorded from the real Python SDK
against a fake app-server and assert the C# facade sends the same JSON-RPC requests and
returns the same results. The only differences:

- Text input items carry `"text_elements": []` (the existing `TurnInputItem.Text` wire shape;
  it is the upstream schema default).
- `IncludeTurns = true` omits `excludeTurns` instead of sending `excludeTurns: false`
  (same runtime default). `IncludeTurns = false` sends `excludeTurns: true` as Python does.
- Unreported usage counters stay `null`; Python's models materialize them as `0`.
- Items come from the existing collector, which replaces an item updated under the same id
  and also merges items listed in `turn/completed`; Python appends `item/completed` items only.
  Real servers send each item once and an empty `turn.items`, so results match.
- Server approval requests are rejected as unhandled unless `CodexConfig.ApprovalHandler`
  is set; Python's default handler accepts command and file-change approvals.
- The default launch is `codex [--config k=v]... app-server` (stdio is the default listener);
  Python appends `--listen stdio://`. Client identity defaults to this SDK's name and version.
- Python verifies runtime versions for newer turn fields and requires initialize metadata;
  the C# facade relies on the server to reject unsupported fields.
- Not mirrored: `thread_source` (thread start/fork), goal operations, `retry_on_overload`,
  approval-handler wrappers, and duplicate blocking (sync) methods.
- A started ChatGPT login makes `Codex` the sole reader of `Client.Notifications()`, so it
  can route `account/login/completed` by login id.
