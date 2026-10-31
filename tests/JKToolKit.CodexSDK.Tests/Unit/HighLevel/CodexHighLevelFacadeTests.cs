using System.Text.Json.Nodes;
using FluentAssertions;
using JKToolKit.CodexSDK.AppServer;

namespace JKToolKit.CodexSDK.Tests.Unit.HighLevel;

public sealed class CodexHighLevelFacadeTests
{
    [Fact]
    public void Config_DefaultLaunch_MirrorsPythonArgumentOrder()
    {
        var options = new CodexConfig
        {
            CodexBin = "/opt/codex", ConfigOverrides = ["model=gpt-5.5", "x=1"], Cwd = "/work",
            Env = new Dictionary<string, string> { ["A"] = "1" }, ClientName = "n", ClientTitle = "t", ClientVersion = "v"
        }.ToClientOptions();

        options.Launch.FileName.Should().BeNull("the SDK's normal executable resolution applies");
        options.CodexExecutablePath.Should().Be("/opt/codex");
        options.Launch.Arguments.Should().Equal("--config", "model=gpt-5.5", "--config", "x=1", "app-server");
        options.Launch.WorkingDirectory.Should().Be("/work");
        options.Launch.Environment.Should().Contain("A", "1");
        options.DefaultClientInfo.Should().Be(new AppServerClientInfo("n", "t", "v"));
        options.ExperimentalApi.Should().BeTrue("Python enables experimentalApi by default");
    }

    [Fact]
    public void Config_LaunchArgsOverride_ReplacesCommandAndIgnoresCodexBin()
    {
        var options = new CodexConfig { CodexBin = "/opt/codex", LaunchArgsOverride = ["python3", "server.py", "--flag"] }.ToClientOptions();

        options.Launch.FileName.Should().Be("python3");
        options.Launch.Arguments.Should().Equal("server.py", "--flag");
        options.CodexExecutablePath.Should().BeNull();
    }

    [Fact]
    public void Config_RejectsEmptyLaunchArgsOverride()
    {
        var act = () => new CodexConfig { LaunchArgsOverride = [] }.ToClientOptions();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ThreadStartOptions_DefaultApprovalMode_IsAutoReview()
    {
        var wire = new CodexThreadStartOptions().ToWire();
        wire.ApprovalPolicy!.Value.Value.Should().Be("on-request");
        wire.ApprovalsReviewer.Should().Be(CodexApprovalsReviewer.AutoReview);
        wire.Sandbox.Should().BeNull();
    }

    [Fact]
    public void RunInput_ConvertsTextItemsAndTrustLabelledInput()
    {
        CodexRunInput text = "hi";
        text.Items.Should().ContainSingle();
        text.ToolOutput.Should().BeNull();

        CodexRunInput external = CodexInput.ExternalMessage("github", "body");
        external.Items.Should().BeEmpty();
        external.ToolOutput!.Name.Should().Be("github");

        var act = () => CodexRunInput.FromItems(TurnInputItem.Text("a"), null!);
        act.Should().Throw<ArgumentException>();
        var blank = () => CodexRunInput.ExternalMessage(" ", "x");
        blank.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Steer_RejectsExternalMessage()
    {
        var (codex, _) = await PythonGolden.StartAsync("steer");
        await using (codex)
        {
            var thread = await codex.ThreadStartAsync();
            await using var turn = await thread.TurnAsync("wait");
            var act = () => turn.SteerAsync(CodexRunInput.ExternalMessage("tool", "x"));
            await act.Should().ThrowAsync<ArgumentException>();
            await turn.SteerAsync("more please");
            (await turn.RunAsync()).FinalResponse.Should().Be("steered answer");
        }
    }

    [PythonFact]
    public async Task StartAsync_LaunchesProcessFromLaunchArgsOverride_EndToEnd()
    {
        var server = Path.Combine(AppContext.BaseDirectory, "Fixtures", "python-sdk-golden", "fake_server.py");
        var log = Path.Combine(Path.GetTempPath(), $"codex-fake-{Guid.NewGuid():N}.jsonl");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await using (var codex = await Codex.StartAsync(
                PythonGolden.PythonClientConfig with { LaunchArgsOverride = [PythonFactAttribute.Python!, server, log] }, timeout.Token))
            {
                codex.Metadata.UserAgent.Should().StartWith("codex_cli_rs/0.166.0");
                var thread = await codex.ThreadStartAsync(ct: timeout.Token);
                var result = await thread.RunAsync("phases", ct: timeout.Token);
                result.FinalResponse.Should().Be("The answer is 42.");
                result.Usage!.Total!.TotalTokens.Should().Be(300);
            }

            var received = File.ReadAllLines(log).Select(l => JsonNode.Parse(l)!)
                .Where(e => (string)e["dir"]! == "recv").Select(e => (string?)e["msg"]!["method"]).ToArray();
            received.Should().Equal("initialize", "initialized", "thread/start", "turn/start");
        }
        finally
        {
            File.Delete(log);
        }
    }
}

/// <summary>Runs only when a Python 3 interpreter is on PATH (the fake app-server is a stdlib-only script).</summary>
internal sealed class PythonFactAttribute : FactAttribute
{
    public static readonly string? Python = Find();

    public PythonFactAttribute()
    {
        if (Python is null) Skip = "Python 3 is not on PATH.";
    }

    private static string? Find()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "python.exe", "python3.exe" } : new[] { "python3", "python" };
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            // Skip the Windows Store "python.exe" app-execution alias, which is not a real interpreter.
            .Where(dir => !dir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
            .SelectMany(dir => names.Select(name => Path.Combine(dir, name)))
            .FirstOrDefault(File.Exists);
    }
}
