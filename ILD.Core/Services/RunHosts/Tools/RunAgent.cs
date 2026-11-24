using System.Text.Json;
using System.Text.RegularExpressions;
using ILD.Core.Services.Implementations.Adapters;

namespace ILD.Core.Services.RunHosts.Tools;

/// <summary>
/// A coding agent CLI a run gets. An npm agent is installed at the exact version ILD itself has installed and selected for it (its
/// update cooldown and any rollback included), so a run uses what Settings shows rather than whatever was newest when an image was built.
/// <paramref name="ReleaseManifest"/> is set for an agent that is a downloaded release, not an npm package, and the guarantee is
/// weaker: a release manifest names only the newest build, so the run host uses the version ILD holds when it already has it or the
/// manifest still names it, and otherwise the version the manifest names (and says so). A rollback or hold on such an agent therefore
/// reaches a run only while that version is still installed on the run host or still offered. The manifest URL is the one ILD
/// installed it from, which the run host reads for its own platform.
/// </summary>
public sealed partial record RunAgent(ManagedAgent Agent, string Version, string? ReleaseManifest = null)
{
    /// <summary>The agents ILD has a current install of on <paramref name="dataRoot"/>, each at that install's version.</summary>
    public static IReadOnlyList<RunAgent> Installed(string dataRoot)
    {
        var agents = new List<RunAgent>();
        foreach (var agent in ManagedAgentCatalog.All)
        {
            if (ManagedAgentInstall.CurrentVersionId(dataRoot, agent) is not { } id) continue;
            var versionDir = ManagedAgentInstall.VersionDir(dataRoot, agent, id);
            var manifest = agent.NpmPackage is { } package
                ? Path.Combine(versionDir, "node_modules", package, "package.json")
                : ManagedAgentInstall.ReleaseRecordIn(versionDir);
            try
            {
                if (!File.Exists(manifest)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                if (!doc.RootElement.TryGetProperty("version", out var v) || v.GetString() is not { } version || !Exact().IsMatch(version)) continue;
                if (agent.NpmPackage is not null)
                    agents.Add(new RunAgent(agent, version));
                else if (doc.RootElement.TryGetProperty("manifestUrl", out var url) && url.GetString() is { } manifestUrl
                    && manifestUrl.StartsWith("https://", StringComparison.Ordinal))
                    agents.Add(new RunAgent(agent, version, manifestUrl));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException) { }
        }
        return agents;
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+([-+][0-9A-Za-z.-]+)?$")]
    private static partial Regex Exact();
}
