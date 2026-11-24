using System.Text.Json;
using System.Text.RegularExpressions;
using ILD.Core.Services.Implementations.Adapters;

namespace ILD.Core.Services.RunHosts.Tools;

/// <summary>Where a downloaded agent's archive is, and the SHA-512 (lowercase hex) it must have.</summary>
public sealed record RunAgentArchive(string Url, string Sha512);

/// <summary>
/// A coding agent CLI a run gets, at the version ILD itself has installed and selected for it (its update cooldown and
/// any rollback included), so a run uses what Settings shows rather than whatever was newest when an image was built.
/// <paramref name="Archive"/> is set for an agent that is a downloaded release, not an npm package.
/// </summary>
public sealed partial record RunAgent(ManagedAgent Agent, string Version, RunAgentArchive? Archive = null)
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
                else if (Text(doc.RootElement, "url") is { } url && url.StartsWith("https://", StringComparison.Ordinal)
                    && Text(doc.RootElement, "sha512") is { } sha512 && Sha512Hex().IsMatch(sha512))
                    agents.Add(new RunAgent(agent, version, new RunAgentArchive(url, sha512)));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException) { }
        }
        return agents;
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex(@"^\d+\.\d+\.\d+([-+][0-9A-Za-z.-]+)?$")]
    private static partial Regex Exact();

    [GeneratedRegex(@"^[0-9a-f]{128}$")]
    private static partial Regex Sha512Hex();
}
