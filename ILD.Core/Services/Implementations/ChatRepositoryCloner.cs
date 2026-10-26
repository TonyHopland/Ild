using System.Diagnostics;
using System.Text;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// <see cref="IChatRepositoryCloner"/>. The work splits along the uid boundary
/// (ADR-0014): the orchestrator clones or fetches ILD's own base clone of the
/// repository with the remote provider's credentials, exactly as a run's Start
/// node does, and never touches the chat's folder; everything in the chat's
/// scratch folder is git running as the agent, with no credentials, filling the
/// chat's clone from that base clone over the local disk.
/// </summary>
public sealed class ChatRepositoryCloner : IChatRepositoryCloner
{
    private const string ReposFolder = "repos";
    private const string MarkerKey = "ild.repositoryId";
    private const int MaxFolderNameLength = 64;

    // What git takes credentials or a repository from. The orchestrator's own git
    // calls pass the askpass pair per process, so these are never expected in its
    // environment; they are removed so that one set by accident cannot follow git
    // into the agent's clone.
    private static readonly string[] GitEnvironmentKeys =
        ["GIT_ASKPASS", "ILD_GIT_PASSWORD", "ILD_GIT_USERNAME", "GIT_DIR", "GIT_WORK_TREE"];

    private static readonly string[] QuietGitOptions =
        ["-c", "core.hooksPath=/dev/null", "-c", "gc.auto=0", "-c", "maintenance.auto=false"];

    private readonly AppDbContext _db;
    private readonly IServiceScopeFactory _scopes;
    private readonly ChatCloneCoordinator _chats;
    private readonly IProviderStore _providers;
    private readonly IRepositoryManager _git;
    private readonly IConfiguration _config;
    private readonly IShutdownState _shutdown;
    private readonly ILogger<ChatRepositoryCloner> _log;

    public ChatRepositoryCloner(
        AppDbContext db,
        IServiceScopeFactory scopes,
        ChatCloneCoordinator chats,
        IProviderStore providers,
        IRepositoryManager git,
        IConfiguration config,
        IShutdownState shutdown,
        ILogger<ChatRepositoryCloner>? log = null)
    {
        _db = db;
        _scopes = scopes;
        _chats = chats;
        _providers = providers;
        _git = git;
        _config = config;
        _shutdown = shutdown;
        _log = log ?? NullLogger<ChatRepositoryCloner>.Instance;
    }

    private sealed record Job(Guid ChatSessionId, string ScratchPath, Repository Repo, string Branch, GitAuthOptions? Auth);

    public async Task<ChatRepositoryCloneResult> CloneAsync(
        Guid chatSessionId, string repositoryId, string? branch, CancellationToken cancellationToken = default)
    {
        var chat = await _db.ChatSessions.AsNoTracking()
            .Where(c => c.Id == chatSessionId)
            .Select(c => new { c.ScratchPath, c.ScheduleId })
            .SingleOrDefaultAsync(cancellationToken);
        if (chat is null)
            return Refused(ChatRepositoryCloneOutcome.UnknownChat, "Unknown or inactive chat session.");

        var schedule = chat.ScheduleId is { } scheduleId
            ? await _db.ChatSchedules.AsNoTracking().SingleOrDefaultAsync(s => s.Id == scheduleId, cancellationToken)
            : null;
        if (schedule is null)
            return Refused(ChatRepositoryCloneOutcome.NotScheduledChat,
                "Only a chat a chat schedule started can clone repositories: the schedule's repository allowlist decides which. This chat has none.");

        if (!Guid.TryParse(repositoryId, out var id) || await _providers.GetRepositoryByIdAsync(id) is not { } repo)
            return Refused(ChatRepositoryCloneOutcome.UnknownRepository,
                $"No repository has the id '{repositoryId}'. Call list_repositories for the ids.");

        if (!Allows(schedule, repo.Id))
            return Refused(ChatRepositoryCloneOutcome.NotAllowed, schedule.RepositoryScope == ChatScheduleRepositoryScope.None
                ? $"The schedule '{schedule.Name}' allows no repositories."
                : $"The schedule '{schedule.Name}' does not allow the repository '{repo.Name}'.");

        var branchName = BranchNameRules.Normalize(branch) ?? BranchNameRules.Normalize(repo.DefaultBranch) ?? "main";
        if (BranchNameRules.Validate(branchName) is { } invalid)
            return Refused(ChatRepositoryCloneOutcome.InvalidBranch, invalid);
        // A valid ref name but never a branch: git refuses to create one by it.
        if (branchName == "HEAD")
            return Refused(ChatRepositoryCloneOutcome.InvalidBranch, "Branch name cannot be 'HEAD'.");

        var provider = await _providers.GetRemoteProviderByIdAsync(repo.RemoteProviderId);
        var auth = new GitAuthOptions(repo.CloneUrl, provider?.ApiKey, provider?.Type);

        // The git work does not run on the request's token: a client that times out
        // or goes away would otherwise kill a clone half-way, and the agent's retry
        // would start over. Only shutdown stops it; the request merely stops waiting.
        var job = new Job(chatSessionId, chat.ScratchPath, repo, branchName, auth);
        var work = Task.Run(() => CloneOrUpdateAsync(job, _shutdown.Stopping), CancellationToken.None);
        try
        {
            return await work.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = work.ContinueWith(
                t => _log.LogError(t.Exception, "Cloning repository {RepositoryId} into chat {ChatSessionId} failed after its request was cancelled", repo.Id, chatSessionId),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            throw;
        }
    }

    private static bool Allows(ChatSchedule schedule, Guid repositoryId) => schedule.RepositoryScope switch
    {
        ChatScheduleRepositoryScope.All => true,
        ChatScheduleRepositoryScope.Selected => ChatScheduleService.RepositoryIdsOf(schedule).Contains(repositoryId),
        _ => false,
    };

    private async Task<ChatRepositoryCloneResult> CloneOrUpdateAsync(Job job, CancellationToken shutdown)
        => await _chats.RunExclusiveAsync(job.ChatSessionId, shutdown, ct => CloneOrUpdateAloneAsync(job, ct))
            ?? ChatDeleted();

    private async Task<ChatRepositoryCloneResult> CloneOrUpdateAloneAsync(Job job, CancellationToken ct)
    {
        // The request checked the chat before this call held its lock; a chat
        // deleted in between would otherwise get its scratch folder back.
        if (!await ChatExistsAsync(job.ChatSessionId, ct))
            return ChatDeleted();

        string basePath;
        using (await BaseRepoClone.LockAsync(job.Repo.Id, ct))
        {
            var (ok, path, error) = await BaseRepoClone.EnsureAsync(_git, job.Repo, _config, job.Auth, ct, _log);
            if (!ok)
                return Refused(ChatRepositoryCloneOutcome.FetchFailed,
                    $"ILD could not fetch the repository '{job.Repo.Name}' from its remote: {error}");
            if (!await _git.RemoteBranchExistsAsync(path!, job.Branch))
                return Refused(ChatRepositoryCloneOutcome.BranchNotFound,
                    $"The repository '{job.Repo.Name}' has no branch '{job.Branch}' on its remote.");
            basePath = path!;
        }

        try
        {
            return await FillChatCloneAsync(job, basePath, ct);
        }
        catch (CloneFailedException e)
        {
            return Refused(ChatRepositoryCloneOutcome.CloneFailed, e.Message);
        }
    }

    private async Task<bool> ChatExistsAsync(Guid chatSessionId, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .ChatSessions.AsNoTracking().AnyAsync(c => c.Id == chatSessionId, ct);
    }

    private static ChatRepositoryCloneResult ChatDeleted()
        => Refused(ChatRepositoryCloneOutcome.UnknownChat, "The chat was deleted.");

    /// <summary>Clone into, or update, the chat's folder for the repository, as the agent.</summary>
    private static async Task<ChatRepositoryCloneResult> FillChatCloneAsync(Job job, string basePath, CancellationToken ct)
    {
        var reposDir = Path.Combine(job.ScratchPath, ReposFolder);
        var (folderName, exists) = await FindFolderAsync(reposDir, job.Repo.Id, Clean(job.Repo.Name), ct);
        var folder = Path.Combine(reposDir, folderName);
        var relativePath = $"{ReposFolder}/{folderName}";
        var branch = job.Branch;
        var remoteRef = $"refs/remotes/origin/{branch}";

        Task<AgentProcessResult> Git(params string[] args) => RunGitAsync(folder, reposDir, args, ct);

        if (!exists)
        {
            try
            {
                await AgentWritableFiles.CreateDirectoryAsync(folder, ct);
            }
            catch (IOException e)
            {
                throw new CloneFailedException($"Could not create {relativePath}: {e.Message}");
            }
            await CheckedAsync(Git("init", "-q"), relativePath);
            await CheckedAsync(Git("config", "--local", MarkerKey, job.Repo.Id.ToString()), relativePath);
        }

        // A clone without a commit is a fresh one, including one whose first call
        // was cut short after its marker was written: the call completes it.
        var previous = await Git("rev-parse", "--verify", "-q", "HEAD^{commit}");
        var fresh = previous.ExitCode != 0;
        await CheckedAsync(Git("config", "--local", "remote.origin.url", WithoutUserInfo(job.Repo.CloneUrl)), relativePath);
        await CheckedAsync(Git("config", "--local", "--replace-all", "remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*"), relativePath);

        var fetch = await Git("fetch", "-q", "--no-tags", basePath, $"+{remoteRef}:{remoteRef}");
        if (fetch.ExitCode != 0)
            throw new CloneFailedException(
                $"Could not fetch '{branch}' into {relativePath} from ILD's own copy of the repository: {fetch.Stderr.Trim()}");

        var update = await SwitchOrFastForwardAsync(Git, branch, remoteRef);
        if (update.ExitCode != 0)
            return Refused(ChatRepositoryCloneOutcome.UpdateRefused,
                $"git refused to bring {relativePath} to origin/{branch}, so nothing in it was changed:\n{update.Stderr.Trim()}\n"
                + "Revert the local work git names (git checkout, git reset, git clean), then call clone_repository again.");

        var commit = (await CheckedAsync(Git("rev-parse", "HEAD"), relativePath)).Stdout.Trim();
        var previousCommit = fresh ? null : previous.Stdout.Trim();
        var changed = previousCommit != commit;
        var message = fresh
            ? $"Cloned {job.Repo.Name} on {branch} at {commit} into {relativePath}."
            : changed
                ? $"Updated {relativePath} on {branch} from {previousCommit} to {commit}."
                : $"{relativePath} on {branch} is already at {commit}: the update changed nothing.";
        return new ChatRepositoryCloneResult(
            fresh ? ChatRepositoryCloneOutcome.Cloned : ChatRepositoryCloneOutcome.Updated,
            message, relativePath, branch, commit, previousCommit, changed);
    }

    /// <summary>
    /// Switch to <paramref name="branch"/>, creating it at its remote head or
    /// moving it forward to that head; a local branch is never moved back or
    /// sideways, so one already at or ahead of its remote head is checked out as
    /// it is, upstream included, and one that has diverged from it is refused.
    /// Every step either does all it says or nothing, so a refusal leaves HEAD,
    /// the branches, the index and the working tree as they were.
    /// </summary>
    private static async Task<AgentProcessResult> SwitchOrFastForwardAsync(
        Func<string[], Task<AgentProcessResult>> git, string branch, string remoteRef)
    {
        var local = await git(["rev-parse", "--verify", "-q", $"refs/heads/{branch}"]);
        if (local.ExitCode != 0)
            return await git(["checkout", "-q", "--track", "-b", branch, remoteRef]);

        var current = await git(["symbolic-ref", "-q", "--short", "HEAD"]);
        if (current.ExitCode == 0 && current.Stdout.Trim() == branch)
            return await git(["merge", "-q", "--ff-only", remoteRef]);

        var upToDate = await git(["merge-base", "--is-ancestor", remoteRef, $"refs/heads/{branch}"]);
        if (upToDate.ExitCode == 0)
            return await git(["checkout", "-q", branch]);
        if (upToDate.ExitCode != 1)
            throw new CloneFailedException($"git could not compare {branch} with origin/{branch}: {upToDate.Stderr.Trim()}");

        // Git's own fast-forward check, which moves nothing; the checkout then
        // moves the branch only once the switch has succeeded.
        var fastForward = await git(["fetch", "--dry-run", ".", $"{remoteRef}:refs/heads/{branch}"]);
        if (fastForward.ExitCode != 0)
            return fastForward;
        return await git(["checkout", "-q", "--track", "-B", branch, remoteRef]);
    }

    /// <summary>
    /// The folder for the repository under <paramref name="reposDir"/>: the one
    /// whose clone carries its id, looked for first under the names it would
    /// have been given — <c>name</c>, else <c>name-&lt;repository id&gt;</c>, which
    /// no other repository can take — and then in every other folder, since the
    /// repository may have been renamed since. Failing that, the first of those
    /// names that is free.
    /// </summary>
    private static async Task<(string Name, bool Exists)> FindFolderAsync(
        string reposDir, Guid repositoryId, string name, CancellationToken ct)
    {
        string[] candidates = [name, $"{name}-{repositoryId:N}"];
        var others = (await AgentWritableFiles.ListDirectoriesAsync(reposDir, ct))
            .Except(candidates, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        foreach (var folder in candidates.Where(c => AgentWritableFiles.EntryExists(Path.Combine(reposDir, c))).Concat(others))
        {
            if (await IsCloneOfAsync(Path.Combine(reposDir, folder), reposDir, repositoryId, ct))
                return (folder, true);
        }

        return candidates.FirstOrDefault(c => !AgentWritableFiles.EntryExists(Path.Combine(reposDir, c))) is { } free
            ? (free, false)
            : throw new CloneFailedException(
                $"{ReposFolder}/{name}-{repositoryId:N} is taken by something that is not this repository's clone; move it away and call again.");
    }

    /// <summary>
    /// Whether <paramref name="folder"/> is a clone carrying the repository's
    /// marker in a repository of its own: a worktree the agent added to the clone
    /// reads the same marker, but is not the clone.
    /// </summary>
    private static async Task<bool> IsCloneOfAsync(string folder, string reposDir, Guid repositoryId, CancellationToken ct)
    {
        var marker = await RunGitAsync(folder, reposDir, ["config", "--local", "--get", MarkerKey], ct);
        if (marker.ExitCode != 0 || !Guid.TryParse(marker.Stdout.Trim(), out var id) || id != repositoryId)
            return false;

        var dirs = await RunGitAsync(folder, reposDir, ["rev-parse", "--git-dir", "--git-common-dir"], ct);
        var lines = dirs.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return dirs.ExitCode == 0 && lines.Length == 2 && lines[0] == lines[1];
    }

    /// <summary>
    /// A folder name made from a repository name: only <c>[A-Za-z0-9._-]</c>,
    /// never starting or ending with '.' or '-', at most
    /// <see cref="MaxFolderNameLength"/> characters, and never empty.
    /// </summary>
    internal static string Clean(string repositoryName)
    {
        var cleaned = new StringBuilder(repositoryName.Length);
        foreach (var c in repositoryName)
        {
            var safe = char.IsAsciiLetterOrDigit(c) || c is '.' or '_' ? c : '-';
            if (safe == '-' && cleaned.Length > 0 && cleaned[^1] == '-')
                continue;
            cleaned.Append(safe);
        }

        var trimmed = cleaned.ToString().Trim('.', '-');
        if (trimmed.Length > MaxFolderNameLength)
            trimmed = trimmed[..MaxFolderNameLength].TrimEnd('.', '-');
        return trimmed.Length == 0 ? "repository" : trimmed;
    }

    private static string WithoutUserInfo(string cloneUrl)
        => Uri.TryCreate(cloneUrl, UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0
            ? new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri.AbsoluteUri
            : cloneUrl;

    private static ChatRepositoryCloneResult Refused(ChatRepositoryCloneOutcome outcome, string message) => new(outcome, message);

    /// <summary>A step in the chat's clone failed for a reason other than the agent's local work.</summary>
    private sealed class CloneFailedException(string message) : Exception(message);

    private static async Task<AgentProcessResult> CheckedAsync(Task<AgentProcessResult> run, string relativePath)
    {
        var result = await run;
        if (result.ExitCode != 0)
            throw new CloneFailedException($"git failed in {relativePath}: {result.Stderr.Trim()} (exit {result.ExitCode})");
        return result;
    }

    /// <summary>
    /// The git command <paramref name="arguments"/> as the agent: from <c>/</c>,
    /// addressing the clone with <c>-C</c> (the orchestrator never enters a folder
    /// the agent owns), with none of the orchestrator's secrets or git
    /// credentials in its environment, and never looking for a repository above
    /// <paramref name="ceilingDirectory"/>.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(
        IReadOnlyList<string> arguments, string? agentUser, string? agentGroup, string? agentHome, string? ceilingDirectory = null)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = "/",
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        AgentIsolation.StripOrchestratorEnvironment(psi);
        foreach (var key in GitEnvironmentKeys)
            psi.Environment.Remove(key);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (ceilingDirectory is not null)
            psi.Environment["GIT_CEILING_DIRECTORIES"] = ceilingDirectory;

        return AgentIsolation.Route(psi, agentUser, agentGroup, agentHome);
    }

    // Hooks in the clone are off, so a failing post-checkout or post-merge hook
    // cannot make a switch that has already happened look refused; so are
    // automatic gc and maintenance, which detach from git and would go on
    // writing in the clone after it is killed, e.g. as the chat is deleted.
    private static async Task<AgentProcessResult> RunGitAsync(
        string folder, string ceilingDirectory, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        using var process = Process.Start(BuildStartInfo(
                [.. QuietGitOptions, "-C", folder, .. arguments], AgentIsolation.AgentUser, AgentIsolation.AgentGroup, AgentIsolation.AgentHome, ceilingDirectory))
            ?? throw new IOException("could not start git");
        return await AgentProcess.RunAsync(process, stdin: null, ct);
    }
}
