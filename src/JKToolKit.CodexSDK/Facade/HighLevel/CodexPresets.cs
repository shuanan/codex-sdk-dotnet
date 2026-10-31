using JKToolKit.CodexSDK.AppServer;
using JKToolKit.CodexSDK.AppServer.Protocol.SandboxPolicy;
using JKToolKit.CodexSDK.Models;

// ReSharper disable once CheckNamespace
namespace JKToolKit.CodexSDK;

/// <summary>Preset filesystem access levels for threads and turns (Python <c>Sandbox</c>).</summary>
public enum CodexSandbox
{
    /// <summary>Allows file reads without writes.</summary>
    ReadOnly,
    /// <summary>Allows writes inside the workspace and configured writable roots.</summary>
    WorkspaceWrite,
    /// <summary>Removes filesystem access restrictions.</summary>
    FullAccess
}

/// <summary>High-level approval behavior for escalated permission requests (Python <c>ApprovalMode</c>).</summary>
public enum CodexApprovalMode
{
    /// <summary>Never ask for approval; escalations are denied (<c>approvalPolicy: "never"</c>).</summary>
    DenyAll,
    /// <summary>Ask on request and route approvals to the automatic reviewer (<c>on-request</c> + <c>auto_review</c>).</summary>
    AutoReview
}

/// <summary>Wire mappings shared by the high-level facade, mirroring the Python SDK's private helpers.</summary>
internal static class CodexPresetWire
{
    /// <summary>Thread lifecycle mode (Python <c>_sandbox_mode</c>).</summary>
    internal static CodexSandboxMode? Mode(CodexSandbox? sandbox) => sandbox switch
    {
        // Explicit cast: CodexSandboxMode converts implicitly from string, which would capture a bare null.
        null => (CodexSandboxMode?)null,
        CodexSandbox.ReadOnly => CodexSandboxMode.ReadOnly,
        CodexSandbox.WorkspaceWrite => CodexSandboxMode.WorkspaceWrite,
        CodexSandbox.FullAccess => CodexSandboxMode.DangerFullAccess,
        _ => throw new ArgumentOutOfRangeException(nameof(sandbox), sandbox, "Unknown sandbox preset.")
    };

    /// <summary>Turn override policy (Python <c>_sandbox_policy</c>, including its serialized defaults).</summary>
    internal static SandboxPolicy? Policy(CodexSandbox? sandbox) => sandbox switch
    {
        null => null,
        CodexSandbox.ReadOnly => new SandboxPolicy.ReadOnly { NetworkAccess = false },
        CodexSandbox.WorkspaceWrite => new SandboxPolicy.WorkspaceWrite(),
        CodexSandbox.FullAccess => new SandboxPolicy.DangerFullAccess(),
        _ => throw new ArgumentOutOfRangeException(nameof(sandbox), sandbox, "Unknown sandbox preset.")
    };

    /// <summary>Approval policy and reviewer (Python <c>_approval_mode_settings</c>).</summary>
    internal static (CodexApprovalPolicy? Policy, CodexApprovalsReviewer? Reviewer) Approval(CodexApprovalMode? mode) => mode switch
    {
        null => ((CodexApprovalPolicy?)null, (CodexApprovalsReviewer?)null),
        CodexApprovalMode.AutoReview => (CodexApprovalPolicy.OnRequest, CodexApprovalsReviewer.AutoReview),
        CodexApprovalMode.DenyAll => (CodexApprovalPolicy.Never, null),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown approval mode.")
    };
}
