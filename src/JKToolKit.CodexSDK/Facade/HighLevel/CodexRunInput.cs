using System.Text.Json;
using JKToolKit.CodexSDK.AppServer;

// ReSharper disable once CheckNamespace
namespace JKToolKit.CodexSDK;

/// <summary>
/// Turn input (Python <c>RunInput</c>): text, one or more <see cref="TurnInputItem"/> values, or an external message.
/// </summary>
/// <remarks>
/// Python's input item types map to existing items: <c>TextInput</c> → <see cref="TurnInputItem.Text"/>,
/// <c>ImageInput</c> → <see cref="TurnInputItem.ImageUrl"/>, <c>LocalImageInput</c> → <see cref="TurnInputItem.LocalImage"/>,
/// <c>SkillInput</c> → <see cref="TurnInputItem.Skill"/>, <c>MentionInput</c> → <see cref="TurnInputItem.Mention"/>.
/// </remarks>
public sealed class CodexRunInput
{
    private CodexRunInput(IReadOnlyList<TurnInputItem> items, TurnToolOutput? toolOutput)
    { Items = items; ToolOutput = toolOutput; }

    /// <summary>Gets the user input items (empty for an external message).</summary>
    public IReadOnlyList<TurnInputItem> Items { get; }

    /// <summary>Gets the standalone tool output for an external message, if any.</summary>
    public TurnToolOutput? ToolOutput { get; }

    /// <summary>Creates user text input.</summary>
    public static CodexRunInput Text(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new([TurnInputItem.Text(text)], null);
    }

    /// <summary>Creates input from one or more items.</summary>
    public static CodexRunInput FromItems(params IReadOnlyList<TurnInputItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Any(static i => i is null)) throw new ArgumentException("Input items cannot contain null.", nameof(items));
        return new(items.ToArray(), null);
    }

    /// <summary>
    /// Creates untrusted content from another agent, tool, or application (Python <c>ExternalMessage</c>).
    /// Sent as standalone <c>toolOutput</c> with tool-level authority and an empty user input list.
    /// </summary>
    public static CodexRunInput ExternalMessage(string toolName, string content, string? @namespace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(content);
        return new(Array.Empty<TurnInputItem>(), TurnToolOutput.Text(toolName, content, @namespace));
    }

    /// <summary>Creates an external message whose content is a list of Responses-compatible content items.</summary>
    public static CodexRunInput ExternalMessage(string toolName, JsonElement content, string? @namespace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        return new(Array.Empty<TurnInputItem>(),
            new TurnToolOutput { Name = toolName, Namespace = @namespace, Output = content.Clone() });
    }

    /// <summary>Converts text to user input.</summary>
    public static implicit operator CodexRunInput(string text) => Text(text);

    /// <summary>Converts one item to input.</summary>
    public static implicit operator CodexRunInput(TurnInputItem item) => FromItems(item);

    /// <summary>Converts items to input.</summary>
    public static implicit operator CodexRunInput(TurnInputItem[] items) => FromItems(items);

    /// <summary>Converts items to input.</summary>
    public static implicit operator CodexRunInput(List<TurnInputItem> items) => FromItems(items);

    /// <summary>Converts trust-labelled <see cref="CodexInput"/> to run input.</summary>
    public static implicit operator CodexRunInput(CodexInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var options = input.ToOptions();
        return new(options.Input, options.ToolOutput);
    }

    internal IReadOnlyList<TurnInputItem> RequireUserItems(string operation) =>
        ToolOutput is null ? Items : throw new ArgumentException($"{operation} accepts only user input, not an external message.");
}
