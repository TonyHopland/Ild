using System.Collections.Concurrent;
using System.Globalization;
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
    private readonly IManagedAgentUsage? _usage;
    private readonly AutoInstallForecast? _forecast;

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
        ILogger<ManagedAgentService>? logger = null,
        IManagedAgentUsage? usage = null,
        AutoInstallForecast? forecast = null)
        : this(http, runner, ManagedAgentInstall.ResolveDataRoot(), logger, usage, forecast)
    {
    }

    /// <summary>Test seam: construct against an explicit data root instead of the resolved one.</summary>
    public ManagedAgentService(
        HttpClient http,
        IProcessRunner runner,
        string dataRoot,
        ILogger<ManagedAgentService>? logger = null,
        IManagedAgentUsage? usage = null,
        AutoInstallForecast? forecast = null)
    {
        _http = http;
        _runner = runner;
        _logger = logger;
        _dataRoot = dataRoot;
        _usage = usage;
        _forecast = forecast;
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

        // A hold left behind an installed version that has since moved past it no longer holds anything.
        var held = ReadHeld(agent) is { } h && (installed is null || !IsNewer(installed, h)) ? h : null;
        var forecast = installed is not null ? _forecast?.Get(agent.Key) : null;
        // Nothing is promised that the installed version already has or that a hold rules out.
        if (forecast is { } due && (!IsNewer(due.Version, installed!) || (held is not null && !IsNewer(due.Version, held)))) forecast = null;
        return new ManagedAgentStatus(
            agent.Key,
            agent.DisplayName,
            agent.NpmPackage,
            installed,
            latest,
            updateAvailable,
            error,
            ReadPrevious(agent)?.Version,
            held,
            forecast?.Version,
            forecast?.After);
    }

    public async Task<ManagedAgentStatus> UpdateAsync(string agentKey, CancellationToken ct = default)
    {
        var agent = ManagedAgentCatalog.Find(agentKey)
            ?? throw new KeyNotFoundException($"No managed agent with key '{agentKey}'.");

        var gate = Locks.GetOrAdd(agent.Key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await InstallLatestAsync(agent, ct);
            // The user chose the newest version over whatever a rollback had held back.
            TryDeleteFile(ManagedAgentInstall.HeldFile(_dataRoot, agent));
            return await GetStatusAsync(agent, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ManagedAgentStatus> RollbackAsync(string agentKey, string? expectedPreviousVersion = null, CancellationToken ct = default)
    {
        var agent = ManagedAgentCatalog.Find(agentKey)
            ?? throw new KeyNotFoundException($"No managed agent with key '{agentKey}'.");

        var gate = Locks.GetOrAdd(agent.Key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var previous = ReadPrevious(agent) is { Version: not null } found
                ? found
                : throw new InvalidOperationException($"{agent.DisplayName} has no previous version to roll back to.");
            if (expectedPreviousVersion is not null && expectedPreviousVersion != previous.Version)
                throw new InvalidOperationException(
                    $"{agent.DisplayName} would now roll back to {previous.Version}, not {expectedPreviousVersion}; reload and try again.");

            // Like a manual update, this does not wait for the agent to be idle: a run in
            // progress keeps the install dir it started in, and the dir rolled back from is
            // left for the next swap's cleanup rather than deleted under it.
            var currentId = ManagedAgentInstall.CurrentVersionId(_dataRoot, agent);
            if (currentId is not null && InstalledVersionIn(agent, currentId) is { } rolledBackFrom)
                WriteFileAtomically(ManagedAgentInstall.HeldFile(_dataRoot, agent), JsonSerializer.Serialize(new HeldMarker(rolledBackFrom)));

            SwapActiveVersion(agent, previous.VersionId);
            TryDeleteFile(ManagedAgentInstall.PreviousFile(_dataRoot, agent));
            _logger?.LogInformation("Rolled managed agent {Agent} back to {Version}", agent.Key, previous.Version);
            return await GetStatusAsync(agent, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AutoUpdatePlan?> GetAutoUpdatePlanAsync(
        string agentKey, TimeSpan cooldown, DateTimeOffset now, CancellationToken ct = default)
    {
        var agent = ManagedAgentCatalog.Find(agentKey)
            ?? throw new KeyNotFoundException($"No managed agent with key '{agentKey}'.");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(VersionLookupTimeout);
            using var doc = await _http.GetFromJsonAsync<JsonDocument>($"{RegistryBase}/{agent.NpmPackage}", cts.Token);
            if (doc is null
                || !doc.RootElement.TryGetProperty("dist-tags", out var tags)
                || !tags.TryGetProperty("latest", out var latestElement)
                || latestElement.GetString() is not { Length: > 0 } latest
                || !doc.RootElement.TryGetProperty("time", out var time)
                || time.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("versions", out var versions)
                || versions.ValueKind != JsonValueKind.Object)
            {
                _logger?.LogWarning("npm packument for {Package} has no latest tag, release times or versions", agent.NpmPackage);
                return null;
            }

            var cutoff = now - cooldown;
            var held = ReadHeld(agent);
            string? target = null;
            (string Version, DateTimeOffset Published)? upcoming = null;
            foreach (var release in time.EnumerateObject())
            {
                // Exactly MAJOR.MINOR.PATCH: drops prereleases and the "created"/"modified" keys.
                // `time` also keeps unpublished versions, which can no longer be installed.
                if (!StableVersionRegex().IsMatch(release.Name)
                    || !versions.TryGetProperty(release.Name, out _)
                    || IsNewer(release.Name, latest)
                    || release.Value.ValueKind != JsonValueKind.String
                    || (held is not null && !IsNewer(release.Name, held))
                    || !DateTimeOffset.TryParse(release.Value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var published))
                {
                    continue;
                }

                if (published > cutoff)
                {
                    if (upcoming is null || IsNewer(release.Name, upcoming.Value.Version)) upcoming = (release.Name, published);
                }
                else if (target is null || IsNewer(release.Name, target))
                {
                    target = release.Name;
                }
            }

            return upcoming is { } next && (target is null || IsNewer(next.Version, target))
                ? new AutoUpdatePlan(target, next.Version, next.Published + cooldown)
                : new AutoUpdatePlan(target, null, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read the release history of {Package}", agent.NpmPackage);
            return null;
        }
    }

    public async Task<AutoUpdateResult> AutoUpdateAsync(string agentKey, string version, CancellationToken ct = default)
    {
        var agent = ManagedAgentCatalog.Find(agentKey)
            ?? throw new KeyNotFoundException($"No managed agent with key '{agentKey}'.");

        var gate = Locks.GetOrAdd(agent.Key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Re-read under the gate: the caller's view of the installed version may be
            // stale by the time its turn comes, and an update must never go backwards.
            if (await GetInstalledVersionAsync(agent, ct) is { } installed && !IsNewer(version, installed))
                return AutoUpdateResult.NotNewer;

            // Switched in at once, in use or not: a running process keeps the install it was
            // launched from, and only new launches follow the pointer.
            SwapIn(agent, await InstallToVersionDirAsync(agent, $"{agent.NpmPackage}@{version}", ct));
            if (ReadHeld(agent) is { } held && IsNewer(version, held))
                TryDeleteFile(ManagedAgentInstall.HeldFile(_dataRoot, agent));
            return AutoUpdateResult.Installed;
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
                await InstallLatestAsync(agent, ct);
            return await GetStatusAsync(agent, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task InstallLatestAsync(ManagedAgent agent, CancellationToken ct)
    {
        var versionId = await InstallToVersionDirAsync(agent, $"{agent.NpmPackage}@latest", ct);
        SwapIn(agent, versionId);
    }

    public async Task RemoveOldVersionsWhenIdleAsync(string agentKey, CancellationToken ct = default)
    {
        var agent = ManagedAgentCatalog.Find(agentKey)
            ?? throw new KeyNotFoundException($"No managed agent with key '{agentKey}'.");

        // Not waited for: an install or rollback holding the gate is about to leave its own
        // new version dir on disk, which a cleanup must not mistake for a leftover.
        var gate = Locks.GetOrAdd(agent.Key, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) return;
        try
        {
            PruneOldVersionsWhenIdle(agent);
        }
        finally
        {
            gate.Release();
        }
    }

    private sealed record PreviousMarker(string? VersionId);

    private sealed record PreviousInstall(string VersionId, string? Version);

    private sealed record HeldMarker(string? Version);

    /// <summary>A marker's contents, whether or not what it names is still intact.</summary>
    private static T? ReadMarker<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The install a rollback would switch back to, or null when there is none or it is no longer
    /// intact. The id is checked as a plain version dir name before it becomes a path, the
    /// install must still have its binary, and it is not the active one. Its version is null when
    /// the install does not say which it is: it is still kept for runs launched from it, but a
    /// rollback to it cannot be offered.
    /// </summary>
    private PreviousInstall? ReadPrevious(ManagedAgent agent)
    {
        if (ReadMarker<PreviousMarker>(ManagedAgentInstall.PreviousFile(_dataRoot, agent)) is not { VersionId: { } versionId }
            || !Guid.TryParseExact(versionId, "N", out _)
            || versionId == ManagedAgentInstall.CurrentVersionId(_dataRoot, agent))
        {
            return null;
        }

        var binary = ManagedAgentInstall.BinaryIn(ManagedAgentInstall.VersionDir(_dataRoot, agent, versionId), agent);
        return File.Exists(binary)
            ? new PreviousInstall(versionId, InstalledVersionIn(agent, versionId))
            : null;
    }

    /// <summary>The version a rollback left, which auto-update must get past; null when nothing is held.</summary>
    private string? ReadHeld(ManagedAgent agent)
        => ReadMarker<HeldMarker>(ManagedAgentInstall.HeldFile(_dataRoot, agent)) is { Version: { } version }
            && StableVersionRegex().IsMatch(version)
            ? version
            : null;

    /// <summary>
    /// The version npm recorded when it installed <paramref name="versionId"/>, read from the
    /// package's own manifest so it needs no process and works for an install that cannot start.
    /// </summary>
    private string? InstalledVersionIn(ManagedAgent agent, string versionId)
    {
        try
        {
            var manifest = Path.Combine(ManagedAgentInstall.VersionDir(_dataRoot, agent, versionId), "node_modules", agent.NpmPackage, "package.json");
            if (!File.Exists(manifest)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            return doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                ? ParseVersion(v.GetString())
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Names the install that was active before a swap as the one a rollback returns to (and the
    /// one kept for runs launched from it), or clears the marker when that install is gone or is
    /// the same version as the new one.
    /// </summary>
    private void RecordPrevious(ManagedAgent agent, string? previousVersionId, string versionId)
    {
        var markerPath = ManagedAgentInstall.PreviousFile(_dataRoot, agent);
        if (previousVersionId is null || previousVersionId == versionId
            || !File.Exists(ManagedAgentInstall.BinaryIn(ManagedAgentInstall.VersionDir(_dataRoot, agent, previousVersionId), agent))
            || (InstalledVersionIn(agent, previousVersionId) is { } previousVersion && previousVersion == InstalledVersionIn(agent, versionId)))
        {
            TryDeleteFile(markerPath);
            return;
        }

        WriteFileAtomically(markerPath, JsonSerializer.Serialize(new PreviousMarker(previousVersionId)));
    }

    /// <summary>
    /// Installs <paramref name="spec"/> into a fresh version dir, closed to the agent for writes,
    /// and returns its id. The pointer is not touched; a failed or cancelled install leaves
    /// nothing behind.
    /// </summary>
    private async Task<string> InstallToVersionDirAsync(ManagedAgent agent, string spec, CancellationToken ct)
    {
        var versionId = Guid.NewGuid().ToString("N");
        var versionDir = ManagedAgentInstall.VersionDir(_dataRoot, agent, versionId);
        Directory.CreateDirectory(versionDir);

        try
        {
            // npm 12 skips dependency install scripts unless the project's
            // package.json approves them, and the agent CLIs need their own
            // postinstall to work. Approve exactly the agent package, nothing
            // it depends on.
            File.WriteAllText(
                Path.Combine(versionDir, "package.json"),
                JsonSerializer.Serialize(new { allowScripts = new Dictionary<string, bool> { [agent.NpmPackage] = true } }));

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

            _logger?.LogInformation("Installed managed agent {Agent} version dir {VersionId} from {Spec}", agent.Key, versionId, spec);
        }
        catch
        {
            TryDeleteDirectory(versionDir);
            throw;
        }

        return versionId;
    }

    /// <summary>
    /// Makes <paramref name="versionId"/> the active version, remembers the one it replaced as the
    /// rollback target, and removes older installs if nothing is using the agent. A swap that
    /// fails removes the new install.
    /// </summary>
    private void SwapIn(ManagedAgent agent, string versionId)
    {
        var previousVersionId = ManagedAgentInstall.CurrentVersionId(_dataRoot, agent);
        try
        {
            SwapActiveVersion(agent, versionId);
        }
        catch
        {
            TryDeleteDirectory(ManagedAgentInstall.VersionDir(_dataRoot, agent, versionId));
            throw;
        }

        // The switch has happened; failing to note the rollback target must not report the update as failed.
        try
        {
            RecordPrevious(agent, previousVersionId, versionId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Could not record the previous version of managed agent {Agent}; it cannot be rolled back to", agent.Key);
        }
        PruneOldVersionsWhenIdle(agent);
    }

    /// <summary>
    /// Atomically point the agent at <paramref name="versionId"/> by overwriting
    /// the pointer file via a single rename — the swap either fully happens or
    /// not at all, so a reader never sees a half-written pointer.
    /// </summary>
    private void SwapActiveVersion(ManagedAgent agent, string versionId)
        => WriteFileAtomically(ManagedAgentInstall.PointerFile(_dataRoot, agent), versionId);

    /// <summary>
    /// Replaces <paramref name="path"/> through a temp file and a single rename, so a reader
    /// never sees a half-written file.
    /// </summary>
    private static void WriteFileAtomically(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Deletes every install but the active one and the one a rollback would return to, while no
    /// run or terminal is using the agent. A process resolves its binary inside
    /// <c>versions/&lt;id&gt;</c> when it starts and these agents are Node apps that lazily
    /// <c>require()</c> files from there, so deleting the folder of a running one would break
    /// it. The idle check and the delete share the lock that starting a run takes, so none can
    /// start halfway through; what is kept because the agent was busy goes at a later call.
    /// </summary>
    private void PruneOldVersionsWhenIdle(ManagedAgent agent)
    {
        void Prune()
        {
            var versionsRoot = ManagedAgentInstall.VersionsRoot(_dataRoot, agent);
            // Without a readable pointer there is no telling which install is the live one.
            if (!Directory.Exists(versionsRoot) || ManagedAgentInstall.CurrentVersionId(_dataRoot, agent) is not { } current) return;

            var keep = new HashSet<string>(StringComparer.Ordinal) { current };
            if (ReadPrevious(agent) is { } previous) keep.Add(previous.VersionId);
            foreach (var dir in Directory.EnumerateDirectories(versionsRoot))
            {
                if (!keep.Contains(Path.GetFileName(dir))) TryDeleteDirectory(dir);
            }
        }

        if (_usage is null) Prune();
        else _usage.TryRunWhenIdle(agent.Key, Prune);
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

    private void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to remove managed-agent file {Path}", path);
        }
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

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex StableVersionRegex();

    [GeneratedRegex(@"(\d+)\.(\d+)\.(\d+)")]
    private static partial Regex SemverRegex();
}
