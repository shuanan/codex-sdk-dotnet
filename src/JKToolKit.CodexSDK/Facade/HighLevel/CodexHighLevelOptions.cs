using System.Text.Json;
using JKToolKit.CodexSDK.AppServer;
using JKToolKit.CodexSDK.Models;

// ReSharper disable once CheckNamespace
namespace JKToolKit.CodexSDK;

/// <summary>Options for <see cref="Codex.ThreadStartAsync"/> (Python <c>thread_start</c> keyword arguments).</summary>
public sealed record class CodexThreadStartOptions
{
    /// <summary>Gets the approval mode. Defaults to <see cref="CodexApprovalMode.AutoReview"/>, as in Python.</summary>
    public CodexApprovalMode ApprovalMode { get; init; } = CodexApprovalMode.AutoReview;
    /// <summary>Gets replacement base instructions.</summary>
    public string? BaseInstructions { get; init; }
    /// <summary>Gets config overrides as a JSON object.</summary>
    public JsonElement? Config { get; init; }
    /// <summary>Gets the working directory.</summary>
    public string? Cwd { get; init; }
    /// <summary>Gets developer instructions.</summary>
    public string? DeveloperInstructions { get; init; }
    /// <summary>Gets whether the thread is ephemeral (not materialized on disk).</summary>
    public bool? Ephemeral { get; init; }
    /// <summary>Gets the model.</summary>
    public CodexModel? Model { get; init; }
    /// <summary>Gets the model provider.</summary>
    public string? ModelProvider { get; init; }
    /// <summary>Gets the personality (<c>none</c>, <c>friendly</c>, <c>pragmatic</c>).</summary>
    public string? Personality { get; init; }
    /// <summary>Gets the sandbox preset.</summary>
    public CodexSandbox? Sandbox { get; init; }
    /// <summary>Gets the service name.</summary>
    public string? ServiceName { get; init; }
    /// <summary>Gets the service tier.</summary>
    public CodexServiceTier? ServiceTier { get; init; }
    /// <summary>Gets the session start source.</summary>
    public ThreadSessionStartSource? SessionStartSource { get; init; }

    internal ThreadStartOptions ToWire()
    {
        var (policy, reviewer) = CodexPresetWire.Approval(ApprovalMode);
        return new ThreadStartOptions
        {
            ApprovalPolicy = policy, ApprovalsReviewer = reviewer, BaseInstructions = BaseInstructions,
            Config = Config, Cwd = Cwd, DeveloperInstructions = DeveloperInstructions, Ephemeral = Ephemeral,
            Model = Model, ModelProvider = ModelProvider, Personality = Personality,
            Sandbox = CodexPresetWire.Mode(Sandbox), ServiceName = ServiceName, ServiceTier = ServiceTier,
            SessionStartSource = SessionStartSource
        };
    }
}

/// <summary>Options for <see cref="Codex.ThreadResumeAsync"/> (Python <c>thread_resume</c> keyword arguments).</summary>
public sealed record class CodexThreadResumeOptions
{
    /// <summary>Gets an approval mode override; <see langword="null"/> keeps the thread's setting.</summary>
    public CodexApprovalMode? ApprovalMode { get; init; }
    /// <summary>Gets replacement base instructions.</summary>
    public string? BaseInstructions { get; init; }
    /// <summary>Gets config overrides as a JSON object.</summary>
    public JsonElement? Config { get; init; }
    /// <summary>Gets the working directory.</summary>
    public string? Cwd { get; init; }
    /// <summary>Gets developer instructions.</summary>
    public string? DeveloperInstructions { get; init; }
    /// <summary>
    /// Gets whether the response includes turn history (runtime response only, not model context).
    /// <see langword="false"/> sends <c>excludeTurns: true</c>; <see langword="null"/> or <see langword="true"/> keep the runtime default.
    /// </summary>
    public bool? IncludeTurns { get; init; }
    /// <summary>Gets the model.</summary>
    public CodexModel? Model { get; init; }
    /// <summary>Gets the model provider.</summary>
    public string? ModelProvider { get; init; }
    /// <summary>Gets the personality.</summary>
    public string? Personality { get; init; }
    /// <summary>Gets the sandbox preset.</summary>
    public CodexSandbox? Sandbox { get; init; }
    /// <summary>Gets the service tier.</summary>
    public CodexServiceTier? ServiceTier { get; init; }

    internal ThreadResumeOptions ToWire(string threadId)
    {
        var (policy, reviewer) = CodexPresetWire.Approval(ApprovalMode);
        return new ThreadResumeOptions
        {
            ThreadId = threadId, ApprovalPolicy = policy, ApprovalsReviewer = reviewer,
            BaseInstructions = BaseInstructions, Config = Config, Cwd = Cwd,
            DeveloperInstructions = DeveloperInstructions, ExcludeTurns = IncludeTurns == false,
            Model = Model, ModelProvider = ModelProvider, Personality = Personality,
            Sandbox = CodexPresetWire.Mode(Sandbox), ServiceTier = ServiceTier
        };
    }
}

/// <summary>Options for <see cref="Codex.ThreadForkAsync"/> (Python <c>thread_fork</c> keyword arguments).</summary>
public sealed record class CodexThreadForkOptions
{
    /// <summary>Gets an approval mode override; <see langword="null"/> keeps the source thread's setting.</summary>
    public CodexApprovalMode? ApprovalMode { get; init; }
    /// <summary>Gets replacement base instructions.</summary>
    public string? BaseInstructions { get; init; }
    /// <summary>Gets config overrides as a JSON object.</summary>
    public JsonElement? Config { get; init; }
    /// <summary>Gets the working directory.</summary>
    public string? Cwd { get; init; }
    /// <summary>Gets developer instructions.</summary>
    public string? DeveloperInstructions { get; init; }
    /// <summary>Gets whether the fork is ephemeral.</summary>
    public bool? Ephemeral { get; init; }
    /// <summary>
    /// Gets whether the response includes turn history. <see langword="false"/> sends <c>excludeTurns: true</c>;
    /// <see langword="null"/> or <see langword="true"/> keep the runtime default.
    /// </summary>
    public bool? IncludeTurns { get; init; }
    /// <summary>Gets the model.</summary>
    public CodexModel? Model { get; init; }
    /// <summary>Gets the model provider.</summary>
    public string? ModelProvider { get; init; }
    /// <summary>Gets the sandbox preset.</summary>
    public CodexSandbox? Sandbox { get; init; }
    /// <summary>Gets the service tier.</summary>
    public CodexServiceTier? ServiceTier { get; init; }

    internal ThreadForkOptions ToWire(string threadId)
    {
        var (policy, reviewer) = CodexPresetWire.Approval(ApprovalMode);
        return new ThreadForkOptions
        {
            ThreadId = threadId, ApprovalPolicy = policy, ApprovalsReviewer = reviewer,
            BaseInstructions = BaseInstructions, Config = Config, Cwd = Cwd,
            DeveloperInstructions = DeveloperInstructions, Ephemeral = Ephemeral,
            ExcludeTurns = IncludeTurns == false, Model = Model, ModelProvider = ModelProvider,
            Sandbox = CodexPresetWire.Mode(Sandbox), ServiceTier = ServiceTier
        };
    }
}

/// <summary>Options for <see cref="CodexConversation.RunAsync"/> and <see cref="CodexConversation.TurnAsync"/>.</summary>
public sealed record class CodexTurnOptions
{
    /// <summary>Gets an approval mode override for this and later turns.</summary>
    public CodexApprovalMode? ApprovalMode { get; init; }
    /// <summary>Gets the working directory.</summary>
    public string? Cwd { get; init; }
    /// <summary>Gets the reasoning effort.</summary>
    public CodexReasoningEffort? Effort { get; init; }
    /// <summary>Gets the model.</summary>
    public CodexModel? Model { get; init; }
    /// <summary>Gets a JSON Schema the final response must satisfy.</summary>
    public JsonElement? OutputSchema { get; init; }
    /// <summary>Gets the personality.</summary>
    public string? Personality { get; init; }
    /// <summary>Gets the sandbox preset (sent as a turn <c>sandboxPolicy</c>).</summary>
    public CodexSandbox? Sandbox { get; init; }
    /// <summary>Gets the thread's default service tier.</summary>
    public CodexServiceTier? ServiceTier { get; init; }
    /// <summary>Gets a label for what initiated the turn (<c>turnTrigger</c>); grants no authority.</summary>
    public string? Source { get; init; }
    /// <summary>Gets the reasoning summary mode (<c>auto</c>, <c>concise</c>, <c>detailed</c>, <c>none</c>).</summary>
    public string? Summary { get; init; }
    /// <summary>Gets a service tier for this turn only (<c>serviceTierForTurn</c>).</summary>
    public CodexServiceTier? TurnServiceTier { get; init; }

    internal TurnStartOptions ToWire(CodexRunInput input)
    {
        var (policy, reviewer) = CodexPresetWire.Approval(ApprovalMode);
        return new TurnStartOptions
        {
            Input = input.Items, ToolOutput = input.ToolOutput, ApprovalPolicy = policy, ApprovalsReviewer = reviewer,
            Cwd = Cwd, Effort = Effort, Model = Model, OutputSchema = OutputSchema, Personality = Personality,
            SandboxPolicy = CodexPresetWire.Policy(Sandbox), ServiceTier = ServiceTier, TurnTrigger = Source,
            Summary = Summary, ServiceTierForTurn = TurnServiceTier
        };
    }
}
