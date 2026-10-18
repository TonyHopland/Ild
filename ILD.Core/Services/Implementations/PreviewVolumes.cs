using Microsoft.Extensions.Configuration;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Where repositories with Keep preview data between runs on keep their preview
/// volumes: one folder per repository under the worktrees root, a volume per
/// subfolder. Agent-writable like the worktrees beside it, so ILD only creates and
/// deletes there through <see cref="AgentWritableFiles"/>.
/// </summary>
public static class PreviewVolumes
{
    public const string DirectoryName = ".preview-volumes";

    public static string Root(IConfiguration config)
        => config["App:WorktreesPath"] is { } worktrees && !string.IsNullOrWhiteSpace(worktrees)
            ? Path.Combine(Path.GetFullPath(worktrees), DirectoryName)
            : throw new InvalidOperationException("App:WorktreesPath is not set.");

    public static string RepositoryFolder(IConfiguration config, Guid repositoryId)
        => Path.Combine(Root(config), repositoryId.ToString("D"));
}
