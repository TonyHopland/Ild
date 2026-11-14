using System.Text;
using System.Text.RegularExpressions;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Data.DTOs;
using ILD.Data.Stores.Interfaces;

namespace ILD.Core.Services.Implementations;

public sealed partial class WorktreeInspector : IWorktreeInspector
{
    public const int MaxOutputBytes = 128 * 1024;
    public const int MaxOutputLines = 2000;
    public const int MaxLogEntries = 100;
    public const int MaxPatternLength = 500;
    private const int MaxPathLength = 1024;

    private static readonly string[] DiffFlags = ["--no-ext-diff", "--no-textconv", "--no-color"];

    private readonly IWorkItemManager _workItems;
    private readonly IRepositoryManager _repos;
    private readonly IRunHost _runHost;
    private readonly ILoopRunStore _loopRuns;
    private readonly IProviderStore _providers;

    public WorktreeInspector(
        IWorkItemManager workItems, IRepositoryManager repos, IRunHost runHost, ILoopRunStore loopRuns, IProviderStore providers)
    {
        _workItems = workItems;
        _repos = repos;
        _runHost = runHost;
        _loopRuns = loopRuns;
        _providers = providers;
    }

    private sealed record Target(string WorktreePath, string? DefaultBranch);

    public Task<WorktreeReadResult> GetStatusAsync(string workItemId, CancellationToken ct = default)
        => WithWorktreeAsync(workItemId, t => GitAsync(t, ["status", "--porcelain=v1", "--untracked-files=all"], ct));

    public async Task<WorktreeReadResult> GetDiffAsync(string workItemId, string? path, bool statOnly, CancellationToken ct = default)
    {
        if (path is not null && PathProblem(path) is { } problem)
            return Invalid(problem);

        return await WithWorktreeAsync(workItemId, async t =>
        {
            if (path is not null
                && await _repos.ReadGitAsync(t.WorktreePath, ["ls-files", "--others", "--exclude-standard", "--", path], MaxOutputBytes, ct) is { ExitCode: 0, StdOut.Length: > 0 })
                return await GitAsync(t, ["diff", .. DiffFlags, .. StatFlag(statOnly), "--no-index", "--", "/dev/null", path], ct, okExitCodes: [0, 1]);

            if (await ForkPointAsync(t) is not { } baseSha)
                return Failed("The worktree's fork point could not be resolved, so there is nothing to diff against.");

            return await GitAsync(t, ["diff", .. DiffFlags, .. StatFlag(statOnly), baseSha, .. PathArgs(path)], ct);
        });
    }

    public async Task<WorktreeReadResult> ReadFileAsync(string workItemId, string path, CancellationToken ct = default)
    {
        if (PathProblem(path) is { } problem)
            return Invalid(problem);

        return await WithWorktreeAsync(workItemId, async t =>
        {
            CappedFileRead? file;
            try
            {
                file = await _repos.ReadWorktreeFileCappedAsync(t.WorktreePath, path, MaxOutputBytes);
            }
            catch (WorktreeFileUnreadableException ex)
            {
                return Failed(ex.Message);
            }

            if (file is null)
                return new WorktreeReadResult(WorktreeReadOutcome.NotFound, $"'{path}' is not a file inside the worktree.");
            if (file.IsBinary)
                return new WorktreeReadResult(WorktreeReadOutcome.Ok, Text: $"[binary file, {file.TotalBytes} bytes: content not shown]");

            return Capped(file.Text ?? string.Empty, file.TotalBytes - Encoding.UTF8.GetByteCount(file.Text ?? string.Empty));
        });
    }

    public async Task<WorktreeReadResult> SearchAsync(string workItemId, string pattern, bool regex, string? path, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(pattern) || pattern.Length > MaxPatternLength || pattern.Contains('\0'))
            return Invalid($"pattern must be 1 to {MaxPatternLength} characters.");
        if (path is not null && PathProblem(path) is { } problem)
            return Invalid(problem);

        return await WithWorktreeAsync(workItemId, async t =>
        {
            var result = await GitAsync(
                t, ["grep", "--no-color", "-I", "-n", "--untracked", regex ? "-E" : "-F", "-e", pattern, .. PathArgs(path)], ct, okExitCodes: [0, 1]);
            return result.Ok && result.Text.Length == 0 ? result with { Text = "No matches." } : result;
        });
    }

    public async Task<WorktreeReadResult> GetLogAsync(string workItemId, int limit, CancellationToken ct = default)
    {
        if (limit is < 1 or > MaxLogEntries)
            return Invalid($"limit must be between 1 and {MaxLogEntries}.");

        return await WithWorktreeAsync(workItemId, async t =>
        {
            if (await ForkPointAsync(t) is not { } baseSha)
                return Failed("The worktree's fork point could not be resolved, so the run's commits cannot be told apart.");

            return await GitAsync(
                t, ["log", "--no-color", "--no-decorate", "--date=short", "--format=%H %ad %an: %s", "-n", limit.ToString(), $"{baseSha}..HEAD", "--"], ct);
        });
    }

    public async Task<WorktreeReadResult> GetCommitAsync(string workItemId, string sha, CancellationToken ct = default)
    {
        if (!CommitIdPattern().IsMatch(sha))
            return Invalid("sha must be 7 to 64 lowercase hex characters.");

        return await WithWorktreeAsync(workItemId, async t =>
        {
            var resolved = await _repos.ReadGitAsync(t.WorktreePath, ["rev-parse", "--verify", "--quiet", $"{sha}^{{commit}}"], 128, ct);
            var full = resolved.StdOut.Trim();
            if (resolved.ExitCode != 0 || !RepositoryManager.IsCommitSha(full))
                return new WorktreeReadResult(WorktreeReadOutcome.NotFound, $"No commit '{sha}' in this worktree.");

            var onBranch = await _repos.ReadGitAsync(t.WorktreePath, ["merge-base", "--is-ancestor", full, "HEAD"], 128, ct);
            if (onBranch.ExitCode != 0)
                return new WorktreeReadResult(WorktreeReadOutcome.NotFound, $"Commit '{sha}' is not in the history of the run branch.");

            return await GitAsync(t, ["show", .. DiffFlags, "--format=fuller", full, "--"], ct);
        });
    }

    private async Task<WorktreeReadResult> WithWorktreeAsync(string workItemId, Func<Target, Task<WorktreeReadResult>> read)
    {
        var workItem = await _workItems.GetWorkItemAsync(workItemId);
        if (workItem is null)
            return new WorktreeReadResult(WorktreeReadOutcome.WorkItemNotFound, "Work item not found.");

        var path = workItem.WorktreePath;
        if (string.IsNullOrWhiteSpace(path) || !await _runHost.DirectoryExistsAsync(path))
            return new WorktreeReadResult(WorktreeReadOutcome.NoWorktree, "Work item does not currently have a worktree.");
        if (!await _repos.ValidateWorktreeHealthAsync(path))
            return new WorktreeReadResult(WorktreeReadOutcome.NoWorktree, "The work item's worktree is not a usable git worktree.");

        return await read(new Target(path, await DefaultBranchAsync(workItem)));
    }

    // The base the run was made on, as the Files tab resolves it (ADR-0008).
    private async Task<string?> DefaultBranchAsync(WorkItemView workItem)
    {
        if (await _loopRuns.GetByWorktreePathAsync(workItem.WorktreePath!) is { BaseBranchOverride: { Length: > 0 } pinned })
            return pinned;
        if (workItem.RunRepositoryId is not { } repositoryId)
            return null;
        return (await _providers.GetRepositoryByIdAsync(repositoryId))?.DefaultBranch;
    }

    private async Task<string?> ForkPointAsync(Target target)
        => (await _repos.ListWorktreeCommitsAsync(target.WorktreePath, target.DefaultBranch)).BaseSha;

    private async Task<WorktreeReadResult> GitAsync(Target target, IReadOnlyList<string> args, CancellationToken ct, int[]? okExitCodes = null)
    {
        var output = await _repos.ReadGitAsync(target.WorktreePath, args, MaxOutputBytes, ct);
        return (okExitCodes ?? [0]).Contains(output.ExitCode)
            ? Capped(output.StdOut, output.OmittedBytes)
            : Failed($"git {args[0]} failed: {output.StdErr.Trim()}");
    }

    private static WorktreeReadResult Capped(string text, long omittedBytes)
    {
        var lines = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n' || ++lines < MaxOutputLines) continue;
            omittedBytes += Encoding.UTF8.GetByteCount(text.AsSpan(i + 1));
            text = text[..(i + 1)];
            break;
        }

        return omittedBytes > 0
            ? new WorktreeReadResult(WorktreeReadOutcome.Ok, Text: $"{text}\n[truncated: {omittedBytes} more bytes not shown]", Truncated: true, OmittedBytes: omittedBytes)
            : new WorktreeReadResult(WorktreeReadOutcome.Ok, Text: text);
    }

    private static WorktreeReadResult Invalid(string message) => new(WorktreeReadOutcome.Invalid, message);

    private static WorktreeReadResult Failed(string message) => new(WorktreeReadOutcome.Failed, message);

    private static string[] StatFlag(bool statOnly) => statOnly ? ["--stat"] : [];

    private static string[] PathArgs(string? path) => path is null ? [] : ["--", path];

    /// <summary>Why <paramref name="path"/> is not a path inside the worktree that may be read, or null when it is one.</summary>
    internal static string? PathProblem(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaxPathLength)
            return $"path must be 1 to {MaxPathLength} characters.";
        if (path.Contains('\0') || path.Contains('\\'))
            return "path must not contain NUL or backslash.";
        if (path.StartsWith('/') || Path.IsPathRooted(path))
            return "path must be relative to the worktree root.";

        foreach (var segment in path.Split('/'))
        {
            if (segment == "..")
                return "path must stay inside the worktree.";
            if (segment.Equals(".git", StringComparison.OrdinalIgnoreCase))
                return "path must not enter .git; use the log and commit tools for history.";
        }

        return null;
    }

    [GeneratedRegex("^[0-9a-f]{7,64}$")]
    private static partial Regex CommitIdPattern();
}
