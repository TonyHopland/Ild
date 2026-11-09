using System.Diagnostics;
using System.Runtime.Versioning;
using ILD.Core.Services.RunHosts.Isolated;

namespace ILD.Tests;

/// <summary>The script that writes a file in a run's or chat's environment, run by bash on a temp directory.</summary>
[SupportedOSPlatform("linux")]
public sealed class ExecRunHostWriteFileScriptTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ild-write-script-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private (int Exit, string StdErr) Run(string path, string mode, string content)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "needs Linux with bash");
        var psi = new ProcessStartInfo("bash") { RedirectStandardInput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-c", ExecRunHost.WriteFileScript, "sh", path, mode }) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        process.StandardInput.Write(content);
        process.StandardInput.Close();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, error);
    }

    [Fact]
    public void An_executable_file_stays_executable_after_an_edit()
    {
        var file = Path.Combine(_dir, "build.sh");
        File.WriteAllText(file, "old");
        File.SetUnixFileMode(file, (UnixFileMode)0b111_101_101);

        var (exit, _) = Run(file, "rw", "new");

        Assert.Equal(0, exit);
        Assert.Equal("new", File.ReadAllText(file));
        Assert.Equal((UnixFileMode)0b111_101_101, File.GetUnixFileMode(file));
    }

    [Fact]
    public void A_hard_link_sees_the_new_content()
    {
        var file = Path.Combine(_dir, "a");
        var link = Path.Combine(_dir, "b");
        File.WriteAllText(file, "old");
        Process.Start("ln", [file, link])!.WaitForExit();

        Assert.Equal(0, Run(file, "rw", "new").Exit);

        Assert.Equal("new", File.ReadAllText(link));
    }

    [Fact]
    public void A_symbolic_link_is_refused_and_left_alone()
    {
        var target = Path.Combine(_dir, "target");
        var link = Path.Combine(_dir, "link");
        File.WriteAllText(target, "old");
        File.CreateSymbolicLink(link, target);

        var (exit, error) = Run(link, "rw", "new");

        Assert.Equal(4, exit);
        Assert.Contains("symbolic link", error);
        Assert.Equal("old", File.ReadAllText(target));
        Assert.NotNull(new FileInfo(link).LinkTarget);
    }

    [Fact]
    public void A_new_file_is_created_with_its_folders()
    {
        var file = Path.Combine(_dir, "a", "b", "c.txt");

        Assert.Equal(0, Run(file, "rw", "hello").Exit);

        Assert.Equal("hello", File.ReadAllText(file));
    }

    [Fact]
    public void A_read_only_write_makes_a_0444_file_even_over_an_existing_one()
    {
        var file = Path.Combine(_dir, "ro.txt");
        File.WriteAllText(file, "old");

        Assert.Equal(0, Run(file, "ro", "one").Exit);
        Assert.Equal(0, Run(file, "ro", "two").Exit);

        Assert.Equal("two", File.ReadAllText(file));
        Assert.Equal((UnixFileMode)0b100_100_100, File.GetUnixFileMode(file));
    }
}
