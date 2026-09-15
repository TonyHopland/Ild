using System.Diagnostics;
using ILD.Core.Services.Implementations;

namespace ILD.Tests;

/// <summary>
/// Owner, group and permission bits of a path, for tests of who may change what
/// under uid isolation (ADR-0014).
/// </summary>
internal static class UnixOwnership
{
    public const UnixFileMode AgentReadDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;

    public const UnixFileMode AgentReadFile =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;

    private const UnixFileMode Permissions =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    public static UnixFileMode PermissionsOf(string path) => File.GetUnixFileMode(path) & Permissions;

    public static string OwnerOf(string path) => Stat(path, "%U");

    public static string GroupOf(string path) => Stat(path, "%G");

    /// <summary>
    /// <paramref name="path"/> belongs to the orchestrator (the user running the
    /// tests), carries the group of <see cref="AgentIsolation.AgentReadRoot"/>, and
    /// has exactly <paramref name="permissions"/>.
    /// </summary>
    public static void AssertOrchestratorOwned(string path, UnixFileMode permissions)
    {
        if (!OperatingSystem.IsLinux()) return;

        Assert.Equal(permissions, PermissionsOf(path));
        Assert.Equal(Environment.UserName, OwnerOf(path));
        Assert.Equal(GroupOf(AgentIsolation.AgentReadRoot), GroupOf(path));
    }

    private static string Stat(string path, string format)
    {
        var psi = new ProcessStartInfo("stat") { RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(format);
        psi.ArgumentList.Add(path);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return output.Trim();
    }
}
