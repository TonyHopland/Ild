using ILD.Core.Services.Implementations;

namespace ILD.Tests;

/// <summary>
/// A <see cref="ClaudeProjectFolders"/> whose HOME and temp root are a directory of
/// this test process, so code under test that cleans up Claude Code's folders never
/// reaches the real <c>~/.claude/projects</c> or <c>/tmp/claude-&lt;uid&gt;</c>.
/// </summary>
internal static class TestClaudeProjectFolders
{
    private static readonly Lazy<ClaudeProjectFolders> Shared = new(() =>
    {
        var root = Directory.CreateTempSubdirectory("ild-test-claude-").FullName;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(root, true); } catch { }
        };
        return Under(root);
    });

    /// <summary>One instance for the whole test process; tests name their folders by unique paths.</summary>
    public static ClaudeProjectFolders Instance => Shared.Value;

    /// <summary>An instance rooted at <paramref name="root"/>: HOME is <c>root/home</c>, the temp root <c>root/tmp</c>.</summary>
    public static ClaudeProjectFolders Under(string root)
        => new(agentUser: null, agentHome: null, new TestProcessEnvironment { { "HOME", Path.Combine(root, "home") } },
            tempRoot: Path.Combine(root, "tmp"));
}
