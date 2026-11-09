using System.Diagnostics;
using ILD.Core.Services.RunHosts.Local;

namespace ILD.Tests;

/// <summary>
/// The environment the local host gives a command, as a run container gives one when it is made: a home, an npm prefix in
/// it and its bin first on PATH. The preview service and Cmd nodes leave these to the host.
/// </summary>
public sealed class LocalRunHostUserToolsTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "ild-user-tools-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_home))
            Directory.Delete(_home, recursive: true);
    }

    private ProcessStartInfo Start(string? path)
    {
        var psi = new ProcessStartInfo("/bin/sh");
        psi.Environment["HOME"] = _home;
        if (path is null)
            psi.Environment.Remove("PATH");
        else
            psi.Environment["PATH"] = path;
        return psi;
    }

    private string Bin => Path.Combine(_home, ".local", "bin");

    [Fact]
    public void A_command_gets_an_npm_prefix_in_its_home_and_its_bin_first_on_path()
    {
        var psi = Start(string.Join(Path.PathSeparator, "/usr/bin", "/bin"));

        LocalRunHost.WithUserTools(psi);

        Assert.Equal(_home, psi.Environment["HOME"]);
        Assert.Equal(Path.Combine(_home, ".local"), psi.Environment["NPM_CONFIG_PREFIX"]);
        Assert.Equal(string.Join(Path.PathSeparator, Bin, "/usr/bin", "/bin"), psi.Environment["PATH"]);
        Assert.True(Directory.Exists(Bin));
    }

    [Fact]
    public void A_bin_already_last_on_ilds_path_is_moved_first_not_added_twice()
    {
        var psi = Start(string.Join(Path.PathSeparator, "/usr/bin", Bin));

        LocalRunHost.WithUserTools(psi);

        Assert.Equal(string.Join(Path.PathSeparator, Bin, "/usr/bin"), psi.Environment["PATH"]);
    }

    [Fact]
    public void A_command_with_no_path_gets_just_the_bin()
    {
        var psi = Start(path: null);

        LocalRunHost.WithUserTools(psi);

        Assert.Equal(Bin, psi.Environment["PATH"]);
    }
}
