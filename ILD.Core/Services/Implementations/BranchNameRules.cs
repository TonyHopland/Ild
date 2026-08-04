namespace ILD.Core.Services.Implementations;

/// <summary>
/// What makes a work item's custom branch name (<c>BranchNameOverride</c>)
/// legal. The name is used <em>verbatim</em> as both a git branch and — through
/// <c>RepositoryManager.CreateWorktreeAsync</c>, which appends it to the
/// worktrees root — a directory path, so a deliberately typed name is either
/// accepted as written or refused with a reason. It is never sanitised into
/// something else: silently turning <c>release/1.0</c> into <c>release-1-0</c>
/// is worse than saying no.
/// </summary>
/// <remarks>
/// The rules are <c>git check-ref-format --branch</c>'s, plus two of our own:
/// a length cap matching <c>LoopRun.BranchName</c> (a name that cannot be
/// persisted on the run is no use), and a ban on backslashes, which git
/// tolerates but which would split the worktree path on a Windows host.
/// </remarks>
public static class BranchNameRules
{
    /// <summary>
    /// Longest name we accept, matching <c>LoopRun.BranchName</c>'s column and
    /// the work-item server's <c>BranchNameOverride</c> column.
    /// </summary>
    public const int MaxLength = 256;

    /// <summary>
    /// A custom branch name as it is stored and compared: trimmed, with blank
    /// collapsed to null so "no override" has exactly one representation.
    /// </summary>
    public static string? Normalize(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    /// <summary>
    /// Why <paramref name="branchName"/> cannot be used as a branch name, or
    /// null when it can. The message is shown to a human as-is, so it names the
    /// offending construct rather than quoting git.
    /// </summary>
    /// <param name="subject">
    /// What the message calls the field, so the same rules can report against
    /// the field the human actually typed into — a work item's custom branch
    /// name or its base branch. Sentence-leading, so it is capitalised.
    /// </param>
    public static string? Validate(string? branchName, string subject = "Branch name")
    {
        var name = Normalize(branchName);
        if (name is null)
            return $"{subject} cannot be empty.";
        if (name.Length > MaxLength)
            return $"{subject} cannot be longer than {MaxLength} characters.";

        foreach (var c in name)
        {
            if (char.IsControl(c))
                return $"{subject} cannot contain control characters.";
            if (c == ' ')
                return $"{subject} cannot contain spaces.";
            if (c is '~' or '^' or ':' or '?' or '*' or '[' or '\\')
                return $"{subject} cannot contain '{c}'.";
        }

        if (name.Contains(".."))
            return $"{subject} cannot contain '..'.";
        if (name.Contains("@{"))
            return $"{subject} cannot contain '@{{'.";
        if (name == "@")
            return $"{subject} cannot be '@'.";
        if (name.StartsWith('-'))
            return $"{subject} cannot start with '-'.";
        if (name.StartsWith('/') || name.EndsWith('/'))
            return $"{subject} cannot start or end with '/'.";
        if (name.Contains("//"))
            return $"{subject} cannot contain '//'.";
        if (name.EndsWith('.'))
            return $"{subject} cannot end with '.'.";

        // Per-segment rules: git refuses a path component that begins with a
        // dot or ends with .lock, whatever depth it sits at.
        foreach (var segment in name.Split('/'))
        {
            if (segment.StartsWith('.'))
                return $"No part of {LowerSubject(subject)} can start with '.'.";
            if (segment.EndsWith(".lock", StringComparison.Ordinal))
                return $"No part of {LowerSubject(subject)} can end with '.lock'.";
        }

        return null;
    }

    /// <summary>The subject mid-sentence, e.g. "a branch name".</summary>
    private static string LowerSubject(string subject)
        => "a " + char.ToLowerInvariant(subject[0]) + subject[1..];

    /// <summary>
    /// What a work item's base branch — the ref its runs are built from — is
    /// called in validation messages. It is a ref name only, never a directory,
    /// but git's ref-format rules are the same either way.
    /// </summary>
    public const string BaseBranchSubject = "Base branch";
}
