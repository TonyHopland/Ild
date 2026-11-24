using System.Text.Json;
using System.Text.RegularExpressions;
using ILD.Core.Services.Implementations.Adapters;

namespace ILD.Core.Services.RunHosts.Tools;

/// <summary>
/// A coding agent CLI a run gets, at the version ILD itself has installed and selected for it (its update cooldown and
/// any rollback included), so a run uses what Settings shows rather than whatever was newest when an image was built.
/// <paramref name="ReleaseManifest"/> is set for an agent that is a downloaded release, not an npm package: the manifest URL
/// ILD installed it from, which the run host reads for its own platform.
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
