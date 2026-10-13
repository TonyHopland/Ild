using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ILD.McpServer.Tools;

/// <summary>
/// MCP tools that bring a repository into a scheduled chat's working directory.
/// A chat has only its scratch folder, and the repositories may be private: the
/// agent cannot clone them itself, so ILD clones with its own credentials, which
/// never reach the clone (ADR-0014).
///
/// Drift warning: these shapes must stay in lockstep with the agent-API endpoints
/// (<c>AgentController</c>) they call.
/// </summary>
[McpServerToolType]
public sealed class RepositoryTools
{
    private readonly IldClient _ild;

    public RepositoryTools(IldClient ild) { _ild = ild; }

    [McpServerTool(Name = "clone_repository")]
    [Description("Have ILD clone a repository into this chat's working directory, or update the clone made there before. Only for chats a chat schedule started, and only for the repositories the schedule allows; call list_repositories for the ids. Each repository gets its own folder under repos/ (the returned 'path', relative to your working directory). Calling it again for the same repository pulls the latest commits from origin, fast-forward only; a different 'branch' switches the clone to it. It never throws away local work: if your uncommitted changes or local commits are in the way, the call fails with git's own message and changes nothing, so revert them yourself (git checkout, git reset, git clean) and call again. Returns 'outcome' (Cloned or Updated), 'path', 'branch', 'commit', 'previousCommit' (null for a fresh clone) and 'changed' — false means nothing changed on the remote since the last call. The clone holds no credentials, so you cannot push from it. A call that times out keeps running in ILD: call again and it picks up the finished work.")]
    public Task<string> CloneRepository(
        [Description("Repository GUID (from list_repositories).")] string repositoryId,
        [Description("Branch to clone or switch to. Omit for the repository's default branch.")] string? branch = null)
        => _ild.PostJsonAsync($"api/v1/agent/chat/repositories/{Uri.EscapeDataString(repositoryId)}/clone", new { branch });
}
