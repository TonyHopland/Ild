using System.ComponentModel;
using System.Text.Json;
using ILD.Core.Services.Implementations.Adapters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// The folders Claude Code keeps per working directory: the transcripts under
/// <c>$HOME/.claude/projects/&lt;encoded cwd&gt;</c> and its scratch under
/// <c>/tmp/claude-&lt;uid&gt;/&lt;encoded cwd&gt;</c>. Ild copies each transcript into
/// its database at the end of every turn, so once a run or chat is over they are
/// only taking up the agent's disk. The agent owns them, so they are measured and
/// deleted as the agent (<see cref="AgentWritableFiles"/>). Every deletion is
/// best-effort: it is logged with the bytes it freed, and never throws.
/// </summary>
public sealed class ClaudeProjectFolders
{
    private readonly string? _projectsRoot;
    private readonly string _tempRoot;
    private readonly ILogger<ClaudeProjectFolders> _log;
    private string? _agentUid;

    public ClaudeProjectFolders(
        string? agentUser, string? agentHome, IProcessEnvironment environment, ILogger<ClaudeProjectFolders>? log = null, string tempRoot = "/tmp")
    {
        _projectsRoot = ClaudeCodeAdapter.GetProjectsRoot(agentUser, agentHome, environment);
        _tempRoot = tempRoot;
        _log = log ?? NullLogger<ClaudeProjectFolders>.Instance;
    }

    /// <summary>The folders of the HOME and uid Claude actually runs with.</summary>
    public static ClaudeProjectFolders ForProcess(ILogger<ClaudeProjectFolders>? log = null)
        => new(AgentIsolation.AgentUser, AgentIsolation.AgentHome, ProcessEnvironment.Current, log);

    public string? ProjectsRoot => _projectsRoot;

    /// <summary>
    /// Delete what Claude Code kept for sessions started in <paramref name="cwd"/>.
    /// Returns the bytes freed; zero when there was nothing, or it could not be deleted (logged).
    /// </summary>
    public async Task<long> DeleteForAsync(string? cwd, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(cwd) || _projectsRoot is null) return 0;

        var name = ClaudeCodeAdapter.EncodeWorktreePath(cwd);
        try
        {
            var paths = new List<string> { Path.Combine(_projectsRoot, name) };
            if (await GetAgentUidAsync(ct) is { } uid)
                paths.Add(Path.Combine(_tempRoot, $"claude-{uid}", name));

            var (allGone, bytes) = await AgentWritableFiles.DeleteMeasuredAsync(paths, ct);
            if (!allGone)
                _log.LogWarning("Claude Code folders for {Cwd} were not all deleted ({Paths}); {Bytes} bytes freed", cwd, string.Join(", ", paths), bytes);
            else if (bytes > 0)
                _log.LogInformation("Deleted Claude Code folders for {Cwd} ({Paths}), freeing {Bytes} bytes", cwd, string.Join(", ", paths), bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            _log.LogWarning(ex, "Could not delete the Claude Code folders for {Cwd}", cwd);
            return 0;
        }
    }

    /// <summary>
    /// Every transcript folder nothing has written to for <paramref name="quiet"/>,
    /// with the working directories its sessions started in. Those are the recorded
    /// cwds the folder's name encodes: an agent that changes directory records others
    /// later, and since <c>/</c>, <c>.</c> and <c>-</c> all encode to <c>-</c>, the
    /// name alone cannot be turned back into a path. A folder none of whose cwds
    /// encode to its name is left out.
    /// </summary>
    public async Task<IReadOnlyList<(string Name, IReadOnlyList<string> StartingCwds)>> ListQuietAsync(TimeSpan quiet, CancellationToken ct = default)
    {
        if (_projectsRoot is null) return [];

        var folders = await AgentWritableFiles.ListQuietTranscriptFoldersAsync(
            _projectsRoot, (int)Math.Ceiling(quiet.TotalMinutes), ct);
        var result = new List<(string, IReadOnlyList<string>)>();
        foreach (var (name, recorded) in folders)
        {
            var starting = recorded
                .Select(DecodeCwd)
                .OfType<string>()
                .Where(cwd => ClaudeCodeAdapter.EncodeWorktreePath(cwd) == name)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (starting.Count > 0)
                result.Add((name, starting));
        }
        return result;
    }

    private static string? DecodeCwd(string field)
    {
        try
        {
            using var document = JsonDocument.Parse($"{{{field}}}");
            return document.RootElement.TryGetProperty("cwd", out var cwd) && cwd.ValueKind == JsonValueKind.String
                ? cwd.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string?> GetAgentUidAsync(CancellationToken ct)
        => _agentUid ??= await AgentWritableFiles.GetUidAsync(ct);
}
