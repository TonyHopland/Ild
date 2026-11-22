using System.Diagnostics;
using System.Runtime.Versioning;
using ILD.Core.Services.RunHosts.Isolated;

namespace ILD.Tests;

/// <summary>
/// The script that follows the links on a path in a run's environment, run by sh on a temp directory: once with the tools of
/// the image, and once with none on the path, as on an image whose <c>realpath</c> and <c>readlink</c> lack <c>-m</c>.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ExecRunHostResolveLinksScriptTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ild-resolve-script-").FullName;

    public ExecRunHostResolveLinksScriptTests()
    {
        // The temp root can itself sit behind a link, so everything below is spelled from its real place.
        _dir = Path.GetFullPath(Directory.ResolveLinkTarget(_dir, returnFinalTarget: true)?.FullName ?? _dir);
        Directory.CreateDirectory(Path.Combine(_dir, "inside"));
        File.WriteAllText(Path.Combine(_dir, "inside", "a.txt"), "x");
        Directory.CreateSymbolicLink(Path.Combine(_dir, "out"), "/");
        Directory.CreateSymbolicLink(Path.Combine(_dir, "shortcut"), Path.Combine(_dir, "inside"));
        File.CreateSymbolicLink(Path.Combine(_dir, "dangling"), Path.Combine(_dir, "nowhere"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private (int Exit, string StdOut) Run(string path, bool withTools)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "needs Linux with sh");
        var psi = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true };
        if (!withTools) psi.Environment["PATH"] = "";
        foreach (var a in new[] { "-c", ExecRunHost.ResolveLinksScript, "sh", path }) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output.Trim());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_directory_link_is_followed_to_where_it_leads(bool withTools)
    {
        Assert.Equal((0, "/etc/hostname"), Run(Path.Combine(_dir, "out", "etc", "hostname"), withTools));
        Assert.Equal((0, Path.Combine(_dir, "inside", "a.txt")), Run(Path.Combine(_dir, "shortcut", "a.txt"), withTools));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void What_is_not_there_is_carried_through_as_written(bool withTools)
    {
        Assert.Equal((0, Path.Combine(_dir, "inside", "new", "deeper.txt")), Run(Path.Combine(_dir, "inside", "new", "deeper.txt"), withTools));
        Assert.Equal((0, "/"), Run("/", withTools));
    }

    [Fact]
    public void Without_the_tools_a_link_that_leads_nowhere_is_refused_rather_than_guessed_at()
        => Assert.NotEqual(0, Run(Path.Combine(_dir, "dangling"), withTools: false).Exit);
}
