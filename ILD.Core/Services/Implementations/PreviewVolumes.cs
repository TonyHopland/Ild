using System.Text.RegularExpressions;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// A preview profile's named volumes: which names it may declare, what
/// <c>${VOLUME:name}</c> expands to, and where each volume's folder is. Repositories
/// with Keep preview data between runs on keep theirs in one folder per repository
/// where the run host keeps it (<see cref="IRunHost.PreviewDataFolderAsync"/>), a volume per
/// subfolder; otherwise a volume lives under the worktree's state directory. Both are
/// agent-writable, so ILD only creates and deletes there through the run host.
/// </summary>
public static class PreviewVolumes
{
    public const string DirectoryName = ".preview-volumes";

    private const string TokenPrefix = "VOLUME:";
    private static readonly Regex NameRegex = new(@"^[A-Za-z0-9-]+\z", RegexOptions.Compiled);

    public static string Root(IConfiguration config)
        => config["App:WorktreesPath"] is { } worktrees && !string.IsNullOrWhiteSpace(worktrees)
            ? Path.Combine(Path.GetFullPath(worktrees), DirectoryName)
            : throw new InvalidOperationException("App:WorktreesPath is not set.");

    public static string RepositoryFolder(IConfiguration config, Guid repositoryId)
        => Path.Combine(Root(config), repositoryId.ToString("D"));

    /// <summary>
    /// Throws when <paramref name="declared"/> holds a name that is not letters, digits
    /// and '-', or the same name twice in any case, or when one of
    /// <paramref name="tokens"/> (the profile's template tokens, without <c>${ }</c>)
    /// names a volume the profile does not declare.
    /// </summary>
    public static void Validate(string profileName, IReadOnlyList<string>? declared, IEnumerable<string> tokens)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in declared ?? [])
        {
            // Names become folder names, so nothing that could reach outside one is allowed.
            if (name == null || !NameRegex.IsMatch(name))
                throw new InvalidOperationException($"Preview profile '{profileName}' declares volume '{name}': volume names may only contain letters, digits and '-'.");
            if (!names.Add(name))
                throw new InvalidOperationException($"Preview profile '{profileName}' declares volume '{name}' more than once.");
        }

        foreach (var token in tokens)
        {
            if (token.StartsWith(TokenPrefix, StringComparison.OrdinalIgnoreCase) && !names.Contains(token[TokenPrefix.Length..]))
                throw new InvalidOperationException(
                    $"Preview profile '{profileName}' references volume '{token[TokenPrefix.Length..]}', which it does not declare in \"volumes\".");
        }
    }

    /// <summary>
    /// The folder of each of <paramref name="declared"/>, by name in any case, created
    /// before anything runs: with Keep preview data on, the repository's folder that
    /// every work item shares; otherwise, without a repository, and for a chat in a host that keeps
    /// it no preview data, one under <paramref name="stateDirectory"/>. Created through <paramref name="host"/>,
    /// where the preview's steps run.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> CreateFoldersAsync(
        IRunHost host, string worktreePath, IReadOnlyList<string>? declared, PreviewVolumeScope? scope, string stateDirectory,
        ILogger logger, CancellationToken cancellationToken)
    {
        var volumes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (declared is not { Count: > 0 })
            return volumes;

        var kept = scope is { KeepData: true } ? await host.PreviewDataFolderAsync(scope.RepositoryId, worktreePath, cancellationToken) : null;
        if (scope is { KeepData: true } && kept is null)
            logger.LogWarning("Preview data is kept between runs only for loop runs; the preview of {Worktree} gets volumes of its own", worktreePath);

        foreach (var name in declared)
        {
            var folder = kept is not null
                ? Path.Combine(kept, name.ToLowerInvariant())
                : Path.Combine(stateDirectory, "volumes", name.ToLowerInvariant());
            try
            {
                await host.CreateDirectoryAsync(folder, cancellationToken);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException($"Preview volume '{name}' could not be created: {ex.Message}", ex);
            }

            volumes[name] = folder;
        }

        return volumes;
    }

    /// <summary>
    /// The folder <paramref name="token"/> names when it is a <c>VOLUME:name</c> token;
    /// false for any other token. Throws for a volume <paramref name="volumes"/> lacks.
    /// </summary>
    public static bool TryExpand(string token, IReadOnlyDictionary<string, string> volumes, string template, out string folder)
    {
        folder = string.Empty;
        if (!token.StartsWith(TokenPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var name = token[TokenPrefix.Length..];
        if (!volumes.TryGetValue(name, out var found))
            throw new InvalidOperationException($"Template '{template}' references undeclared volume '{name}'.");
        folder = found;
        return true;
    }

    /// <summary>Whether <paramref name="path"/> is one of the volume folders or inside one.</summary>
    public static bool Contain(IReadOnlyDictionary<string, string> volumes, string path)
        => volumes.Values.Any(volume => path == volume || path.StartsWith(volume + Path.DirectorySeparatorChar, StringComparison.Ordinal));
}
