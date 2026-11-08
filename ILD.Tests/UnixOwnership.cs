using System.Diagnostics;
using System.Runtime.Versioning;

namespace ILD.Tests;

/// <summary>Owner and permission bits of a path, for the token-bearing files ILD writes for a launch to read.</summary>
internal static class UnixOwnership
{
    public const UnixFileMode AgentReadDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public const UnixFileMode AgentReadFile =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode Permissions =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    [UnsupportedOSPlatform("windows")]
    public static UnixFileMode PermissionsOf(string path) => File.GetUnixFileMode(path) & Permissions;

    public static string OwnerOf(string path) => Stat(path, "%U");

    /// <summary><paramref name="path"/> belongs to the user running the tests and has exactly <paramref name="permissions"/>.</summary>
    public static void AssertOrchestratorOwned(string path, UnixFileMode permissions)
    {
        if (!OperatingSystem.IsLinux()) return;

        Assert.Equal(permissions, PermissionsOf(path));
        Assert.Equal(Environment.UserName, OwnerOf(path));
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
