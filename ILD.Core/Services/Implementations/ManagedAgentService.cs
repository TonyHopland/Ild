using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Installs and version-checks the managed coding agents (Pi, OpenCode) via npm,
/// landing installs on the persistent <c>/data</c> volume. See
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

    // One in-flight update per agent: two clicks racing the same agent are
    // serialized so they can't interleave installs and corrupt the swap.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public ManagedAgentService(
        HttpClient http,
        IProcessRunner runner,
        IConfiguration config,
        ILogger<ManagedAgentService>? logger = null)
    {
        _http = http;
        _runner = runner;
        _logger = logger;
        _dataRoot = config["App:DataPath"]
            ?? Environment.GetEnvironmentVariable("ILD_DATA_PATH")
            ?? config["Storage:DataRoot"]
            ?? "data";
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

        var updateAvailable = installed is not null && latest is not null && IsNewer(latest, installed);
        return new ManagedAgentStatus(
            agent.Key,
            agent.DisplayName,
            agent.NpmPackage,
            installed,
            latest,
            updateAvailable,
            error);
    }

    public async Task<ManagedAgentStatus> UpdateAsync(string agentKey, string? version, CancellationToken ct = default)
    {
        var agent = ManagedAgentCatalog.Find(agentKey)
            ?? throw new KeyNotFoundException($"No managed agent with key '{agentKey}'.");

        var gate = _locks.GetOrAdd(agent.Key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await InstallAsync(agent, version, ct);
            return await GetStatusAsync(agent, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task InstallAsync(ManagedAgent agent, string? version, CancellationToken ct)
    {
        var versionId = Guid.NewGuid().ToString("N");
        var versionDir = ManagedAgentInstall.VersionDir(_dataRoot, agent, versionId);
        Directory.CreateDirectory(versionDir);

        var spec = $"{agent.NpmPackage}@{(string.IsNullOrWhiteSpace(version) ? "latest" : version)}";

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

            SwapActiveVersion(agent, versionId);
            _logger?.LogInformation("Installed managed agent {Agent} version dir {VersionId} from {Spec}", agent.Key, versionId, spec);
        }
        catch
        {
            TryDeleteDirectory(versionDir);
            throw;
        }

        PruneOldVersions(agent, keep: versionId);
    }

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
        File.Move(tmp, pointer, overwrite: true);
    }

    /// <summary>Delete every install except the active one — latest only, no version history on disk.</summary>
    private void PruneOldVersions(ManagedAgent agent, string keep)
    {
        var versionsRoot = ManagedAgentInstall.VersionsRoot(_dataRoot, agent);
        if (!Directory.Exists(versionsRoot)) return;

        foreach (var dir in Directory.EnumerateDirectories(versionsRoot))
        {
            if (string.Equals(Path.GetFileName(dir), keep, StringComparison.Ordinal)) continue;
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
