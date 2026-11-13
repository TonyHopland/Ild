using System.Text.Json;
using System.Text.RegularExpressions;
using ILD.Core.Services.Implementations.Adapters;

namespace ILD.Core.Services.RunHosts.Tools;

/// <summary>
/// A coding agent CLI a run gets, at the version ILD itself has installed and selected for it (its update cooldown and
/// any rollback included), so a run uses what Settings shows rather than whatever was newest when an image was built.
/// </summary>
public sealed partial record RunAgent(ManagedAgent Agent, string Version)
{
    /// <summary>The agents ILD has a current install of on <paramref name="dataRoot"/>, each at that install's version.</summary>
    public static IReadOnlyList<RunAgent> Installed(string dataRoot)
    {
        var agents = new List<RunAgent>();
        foreach (var agent in ManagedAgentCatalog.All)
        {
            if (ManagedAgentInstall.CurrentVersionId(dataRoot, agent) is not { } id) continue;
            var manifest = Path.Combine(ManagedAgentInstall.VersionDir(dataRoot, agent, id), "node_modules", agent.NpmPackage, "package.json");
            try
            {
                if (!File.Exists(manifest)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                if (doc.RootElement.TryGetProperty("version", out var v) && v.GetString() is { } version && Exact().IsMatch(version))
                    agents.Add(new RunAgent(agent, version));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException) { }
        }
        return agents;
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+([-+][0-9A-Za-z.-]+)?$")]
    private static partial Regex Exact();
}
