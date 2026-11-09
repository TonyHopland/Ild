namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Resolves <c>{{Placeholder}}</c> tokens in user-authored prompt / description
/// templates. Centralises the regex, the canonical placeholder set, and
/// special-prefix rules (e.g. <c>WorkTree.File:&lt;rel&gt;</c>) that were
/// previously duplicated across the AI service, PR node executor and
/// individual agent adapters.
/// </summary>
public interface IPromptTemplateResolver
{
    /// <summary>
    /// Render <paramref name="template"/> against <paramref name="context"/>.
    /// Unknown placeholders are left untouched (the validator catches those at
    /// template-save time).
    /// </summary>
    string Render(string template, PromptContext context);

    /// <summary>
    /// Reads the files the <c>{{WorkTree.File:&lt;rel&gt;}}</c> placeholders of <paramref name="template"/> name, keyed by
    /// the relative path, for <see cref="PromptContext.WorkTreeFiles"/>. Each read is bounded; one that fails, times out or
    /// finds no file is left out and renders empty.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ReadWorkTreeFilesAsync(string template, string? worktreePath, CancellationToken ct = default);
}

/// <summary>
/// Bundle of values available to a prompt-template render. Construct directly
/// at call sites; any field can be omitted (defaults to empty / null).
/// </summary>
public sealed record PromptContext(
    string? WorkItemTitle = null,
    string? WorkItemDescription = null,
    string? PreviousNodeOutput = null,
    IReadOnlyList<string>? EventLogSummary = null,
    string? WorktreePath = null,
    string? ConversationFull = null,
    string? ConversationAI = null,
    string? ConversationHuman = null,
    IReadOnlyDictionary<string, string>? RunVariables = null,
    IReadOnlyDictionary<string, string>? GlobalVariables = null,
    IReadOnlyDictionary<string, string>? WorkTreeFiles = null);
