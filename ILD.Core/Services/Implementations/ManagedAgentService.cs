using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Installs and version-checks the managed coding agents (Pi, OpenCode,
/// Claude Code) via npm, landing installs on the persistent <c>/data</c> volume. See
/// <see cref="ManagedAgentInstall"/> for the on-disk layout and the atomic swap.
/// </summary>
public sealed partial class ManagedAgentService : IManagedAgentService
{
    private const string RegistryBase = "https://registry.npmjs.org";
    private static readonly TimeSpan VersionLookupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http;
    private readonly IProcessRunner _runner;
    private readonly ILogger<ManagedAgentService>? _logger;
    private readonly string _dataRoot;
    private readonly string? _agentUser;

    // One in-flight update per agent, shared across every instance: the typed
    // HttpClient registration makes this service transient, so two racing
    // POST .../update requests run on different instances. A static lock store
    // is the only thing that actually serializes them — otherwise instance A's
    // PruneOldVersions could delete instance B's just-swapped version dir.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public ManagedAgentService(
        HttpClient http,
        IProcessRunner runner,
        ILogger<ManagedAgentService>? logger = null)
        : this(http, runner, ManagedAgentInstall.ResolveDataRoot(), logger, AgentIsolation.AgentUser)
    {
    }

    /// <summary>Test seam: construct against an explicit data root instead of the resolved one.</summary>
    public ManagedAgentService(
        HttpClient http,
        IProcessRunner runner,
        string dataRoot,
        ILogger<ManagedAgentService>? logger = null,
        string? agentUser = null)
    {
        _http = http;
        _runner = runner;
        _logger = logger;
        _dataRoot = dataRoot;
        // Taken verbatim: null means "isolation off", not "look it up". The
        // convenience constructor above resolves the ambient value, so a test can
        // drive either state deterministically instead of depending on a
        // process-global env var it shares with every other test.
        _agentUser = agentUser;
    }

    public IReadOnlyList<ManagedAgent> Agents => ManagedAgentCatalog.All;

    public async Task<IReadOnlyList<ManagedAgentStatus>> GetStatusesAsync(CancellationToken ct = default)
    {
        var statuses = await Task.WhenAll(Agents.Select(a => GetStatusAsync(a, ct)));
        return statuses;
    }

    public async Task<ManagedAgentStatus> GetStatusAsync(ManagedAgent agent, CancellationToken ct = default)
    {
        var installed = await GetInstalledVersionAsync(agent, ct);
        var (latest, error) = await GetLatestVersionAsync(agent, ct);

        // Actionable whenever the latest version is known and either nothing is
        // installed yet (fresh deployment — install it) or the installed copy is
        // behind (update it). Agents are no longer baked into the image, so the
        // not-installed case is the normal first-boot state and must be installable.
        var updateAvailable = latest is not null && (installed is null || IsNewer(latest, installed));
        return new ManagedAgentStatus(
            agent.Key,
            agent.DisplayName,
            agent.NpmPackage,
            installed,
            latest,
            updateAvailable,
            error);
    }

    public async Task<ManagedAgentStatus> UpdateAsync(string agentKey, CancellationToken ct = default)
    {
        var agent = ManagedAgentCatalog.Find(agentKey)
            ?? throw new KeyNotFoundException($"No managed agent with key '{agentKey}'.");

        var gate = Locks.GetOrAdd(agent.Key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await InstallAsync(agent, ct);
            return await GetStatusAsync(agent, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ManagedAgentStatus> EnsureInstalledAsync(string agentKey, CancellationToken ct = default)
    {
        var agent = ManagedAgentCatalog.Find(agentKey)
            ?? throw new KeyNotFoundException($"No managed agent with key '{agentKey}'.");

        // Fast path: already present on /data or PATH — nothing to do. We do NOT
        // upgrade a present-but-behind install here; that stays a user-triggered
        // action on the AI Provider page.
        if (await GetInstalledVersionAsync(agent, ct) is not null)
            return await GetStatusAsync(agent, ct);

        var gate = Locks.GetOrAdd(agent.Key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Re-check under the lock: a racing Update/Ensure may have installed
            // it while we waited, so we don't redundantly reinstall.
            if (await GetInstalledVersionAsync(agent, ct) is null)
                await InstallAsync(agent, ct);
            return await GetStatusAsync(agent, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task InstallAsync(ManagedAgent agent, CancellationToken ct)
    {
        // The version currently in use (if any). It is kept through this install
        // so a run already launched against it isn't pulled out from under it.
        var previousVersionId = ManagedAgentInstall.CurrentVersionId(_dataRoot, agent);

        var versionId = Guid.NewGuid().ToString("N");
        var versionDir = ManagedAgentInstall.VersionDir(_dataRoot, agent, versionId);
        Directory.CreateDirectory(versionDir);
        // CreateDirectory implicitly created <key>/ and <key>/versions/ too, and
        // under the container umask they land group-writable with the shared group
        // inherited from /data/agents' setgid bit. Close them immediately: create,
        // unlink and rename are governed by the CONTAINING directory, so a writable
        // versions/ would let the agent drop in its own version tree regardless of
        // any mode on the files themselves.
        ProtectAgentRoot(agent);
        // ...and close the version dir outright for the duration of the install.
        // npm creates the whole node_modules tree as the orchestrator under the
        // container's umask 002, inheriting the shared group from the setgid
        // parent, so it is agent-writable while it is being assembled — and this
        // is the tree the orchestrator itself later execs. Stripping write only
        // works on a finished tree, so block traversal instead until it is one.
        AgentIsolation.HideFromAgent(versionDir, _agentUser);

        var spec = $"{agent.NpmPackage}@latest";

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(InstallTimeout);

            ProcessResult result;
            try
            {
                // Install into versionDir/node_modules via --prefix. Run with the
                // install dir as cwd (outside the app repo) so npm doesn't pick up
                // the repo's workspace config.
                result = await _runner.RunAsync(
                    "npm",
                    ["install", spec, "--prefix", versionDir, "--no-audit", "--no-fund", "--no-package-lock"],
                    workingDirectory: versionDir,
                    ct: cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new InvalidOperationException($"npm install of {spec} timed out.");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.FileNotFoundException)
            {
                throw new InvalidOperationException($"npm is not available to install {spec}: {ex.Message}");
            }

            if (!result.Success)
                throw new InvalidOperationException($"npm install of {spec} failed (exit {result.ExitCode}): {FirstNonEmpty(result.StdErr, result.StdOut)}");

            var binary = ManagedAgentInstall.BinaryIn(versionDir, agent);
            if (!File.Exists(binary))
                throw new InvalidOperationException($"npm install of {spec} did not produce the expected '{agent.BinaryName}' binary.");

            // Publish the finished tree BEFORE the pointer flips: strip write from
            // everything npm just wrote, then reopen the version dir so the agent
            // can traverse and exec it. Doing this after the swap would leave the
            // agent able to rewrite the tree `current` already points at.
            AgentIsolation.ProtectFromAgentWrites(versionDir, _agentUser);
            AgentIsolation.AllowAgentReadExecute(versionDir, _agentUser);

            SwapActiveVersion(agent, versionId);
            _logger?.LogInformation("Installed managed agent {Agent} version dir {VersionId} from {Spec}", agent.Key, versionId, spec);
        }
        catch
        {
            TryDeleteDirectory(versionDir);
            throw;
        }

        PruneOldVersions(agent, versionId, previousVersionId);
    }

    /// <summary>
    /// Re-assert "the agent may exec this tree but never write it" over the whole
    /// of <c>/data/agents/&lt;key&gt;</c> — the version dirs, their shared
    /// <c>versions/</c> parent, the agent root itself and the <c>current</c>
    /// pointer (ADR-0014).
    ///
    /// <para>
    /// It has to be the whole root, not just the files: create, unlink and rename
    /// are governed by write permission on the <em>containing</em> directory. A
    /// group-writable <c>versions/</c> or <c>&lt;key&gt;/</c> would let the agent
    /// replace the pointer and drop in its own version tree no matter how the
    /// files inside are moded — and <c>GetInstalledVersionAsync</c> then runs the
    /// resolved binary as the orchestrator on every AI Provider status poll.
    /// </para>
    ///
    /// <para>
    /// Installs happen at runtime, after the entrypoint's boot-time pass, and npm
    /// writes group-writable under the container's <c>umask 002</c> — so this is
    /// what makes the guarantee hold without a restart, and without depending on
    /// the volume filesystem supporting the default ACL.
    /// </para>
    /// </summary>
    private void ProtectAgentRoot(ManagedAgent agent)
        => AgentIsolation.ProtectFromAgentWrites(ManagedAgentInstall.AgentRoot(_dataRoot, agent), _agentUser);

    /// <summary>
    /// Atomically point the agent at <paramref name="versionId"/> by overwriting
    /// the pointer file via a single rename — the swap either fully happens or
    /// not at all, so a reader never sees a half-written pointer.
    /// </summary>
    private void SwapActiveVersion(ManagedAgent agent, string versionId)
    {
        var pointer = ManagedAgentInstall.PointerFile(_dataRoot, agent);
        Directory.CreateDirectory(Path.GetDirectoryName(pointer)!);
        var tmp = Path.Combine(ManagedAgentInstall.AgentRoot(_dataRoot, agent), $".current.{versionId}.tmp");
        File.WriteAllText(tmp, versionId);
        // Protect before the rename, not after: the temp file is created 0664
        // under umask 002 and File.Move preserves the mode, so protecting
        // afterwards would leave a window where the live pointer — which decides
        // which binary the orchestrator execs — is agent-writable.
        AgentIsolation.ProtectFromAgentWrites(tmp, _agentUser);
        File.Move(tmp, pointer, overwrite: true);
    }

    /// <summary>
    /// Delete superseded installs, keeping the given version ids (nulls ignored).
    /// We keep the active version AND the one it just replaced: these agents are
    /// Node apps that lazily <c>require()</c> files from their install dir, so a
    /// run launched before the swap would crash if its version were deleted
    /// mid-run. Keeping current + previous lets in-flight runs finish while still
    /// bounding disk to two installs — no long version history.
    /// </summary>
    private void PruneOldVersions(ManagedAgent agent, params string?[] keep)
    {
        var versionsRoot = ManagedAgentInstall.VersionsRoot(_dataRoot, agent);
        if (!Directory.Exists(versionsRoot)) return;

        var keepSet = keep.Where(k => k is not null).ToHashSet(StringComparer.Ordinal);
        foreach (var dir in Directory.EnumerateDirectories(versionsRoot))
        {
            if (keepSet.Contains(Path.GetFileName(dir))) continue;
            TryDeleteDirectory(dir);
        }
    }

    private async Task<string?> GetInstalledVersionAsync(ManagedAgent agent, CancellationToken ct)
    {
        var command = ManagedAgentInstall.ResolveCommand(agent, _dataRoot);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(VersionLookupTimeout);
            var result = await _runner.RunAsync(command, ["--version"], ct: cts.Token);
            if (!result.Success) return null;
            return ParseVersion(result.StdOut) ?? ParseVersion(result.StdErr);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Binary missing or unreadable — report "not installed" rather than failing the page.
            return null;
        }
    }

    private async Task<(string? Version, string? Error)> GetLatestVersionAsync(ManagedAgent agent, CancellationToken ct)
    {
        // The registry serves the latest dist-tag manifest at /{pkg}/latest; its
        // `version` field is the newest published version.
        var url = $"{RegistryBase}/{agent.NpmPackage}/latest";
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(VersionLookupTimeout);
            using var doc = await _http.GetFromJsonAsync<JsonDocument>(url, cts.Token);
            if (doc is not null
                && doc.RootElement.TryGetProperty("version", out var v)
                && v.ValueKind == JsonValueKind.String
                && v.GetString() is { Length: > 0 } version)
            {
                return (version, null);
            }
            return (null, "npm registry did not return a version.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch latest version for {Package}", agent.NpmPackage);
            return (null, $"Could not reach the npm registry: {ex.Message}");
        }
    }

    /// <summary>Extract the leading <c>major.minor.patch</c> from arbitrary version output.</summary>
    public static string? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = SemverRegex().Match(text);
        return match.Success ? match.Value : null;
    }

    /// <summary>True when <paramref name="latest"/> is a strictly higher version than <paramref name="installed"/>.</summary>
    public static bool IsNewer(string latest, string installed)
    {
        var l = ParseTriple(latest);
        var i = ParseTriple(installed);
        if (l is null || i is null) return false;

        for (var idx = 0; idx < 3; idx++)
        {
            if (l[idx] != i[idx]) return l[idx] > i[idx];
        }
        return false;
    }

    private static int[]? ParseTriple(string version)
    {
        var match = SemverRegex().Match(version);
        if (!match.Success) return null;
        return
        [
            int.Parse(match.Groups[1].Value),
            int.Parse(match.Groups[2].Value),
            int.Parse(match.Groups[3].Value),
        ];
    }

    private static string FirstNonEmpty(string a, string b)
    {
        var trimmedA = a.Trim();
        if (trimmedA.Length > 0) return trimmedA;
        var trimmedB = b.Trim();
        return trimmedB.Length > 0 ? trimmedB : "no output";
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to remove managed-agent directory {Path}", path);
        }
    }

    [GeneratedRegex(@"(\d+)\.(\d+)\.(\d+)")]
    private static partial Regex SemverRegex();
}
