using System.Diagnostics;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Isolated;

namespace ILD.Tests;

/// <summary>
/// The script that keeps a run's or chat's forwards answering on its loopback, run by bash against a fake socat that
/// records its arguments and stays up, so which relays it starts, keeps and stops is checked without a network.
/// </summary>
public sealed class ExecRunHostForwardsScriptTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ild-forwards-script-").FullName;
    private string Bin => Path.Combine(_dir, "bin");
    private string State => Path.Combine(_dir, "state");
    private string HostsFile => Path.Combine(_dir, "hosts");
    private string Log => Path.Combine(_dir, "socat.log");

    public ExecRunHostForwardsScriptTests()
    {
        Directory.CreateDirectory(Bin);
        var socat = Path.Combine(Bin, "socat");
        File.WriteAllText(socat, "#!/bin/bash\necho \"$*\" >> \"$FAKE_LOG\"\nexec sleep 300\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(socat, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose()
    {
        if (Directory.Exists(State))
            foreach (var pid in Directory.GetFiles(State, "*.pid"))
                try { Process.GetProcessById(int.Parse(File.ReadAllText(pid).Trim())).Kill(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException) { }
        Directory.Delete(_dir, recursive: true);
    }

    private static bool Has(string tool)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("bash", ["-c", $"command -v {tool}"]) { RedirectStandardOutput = true })!;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private void Run(params string[] arguments)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && Has("setsid"), "needs Linux with bash and setsid");
        var psi = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-c", ExecRunHost.ForwardsScript, "bash", "ild", "3128" }.Concat(arguments)) psi.ArgumentList.Add(a);
        psi.Environment["PATH"] = $"{Bin}:{Environment.GetEnvironmentVariable("PATH")}";
        psi.Environment["ILD_FORWARDS_DIR"] = State;
        psi.Environment["FAKE_LOG"] = Log;
        using var process = Process.Start(psi)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        // The relays write their arguments as they start.
        var expected = Directory.GetFiles(State, "*.pid").Length;
        for (var i = 0; i < 50 && Started().Length < expected; i++) Thread.Sleep(50);
    }

    private RunHostOutput RunHosts(params string[] arguments)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "needs Linux with bash");
        var psi = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-c", ExecRunHost.HostsScript, "bash" }.Concat(arguments)) psi.ArgumentList.Add(a);
        psi.Environment["ILD_HOSTS_FILE"] = HostsFile;
        using var process = Process.Start(psi)!;
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return new RunHostOutput(process.ExitCode, process.StandardOutput.ReadToEnd(), error.Result);
    }

    private string[] Started() => File.Exists(Log) ? File.ReadAllLines(Log) : [];

    private int Pid(int localPort) => int.Parse(File.ReadAllText(Path.Combine(State, $"{localPort}.pid")).Trim());

    private static bool Alive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    [Fact]
    public void A_forward_gets_one_relay_to_its_destination_through_the_proxy_and_a_second_sync_keeps_it()
    {
        Run("15432", "postgres", "5432", "-");
        var pid = Pid(15432);
        Run("15432", "postgres", "5432", "-");

        Assert.Equal(["TCP-LISTEN:15432,bind=127.0.0.1,reuseaddr,fork PROXY:ild:postgres:5432,proxyport=3128"], Started());
        Assert.Equal(pid, Pid(15432));
        Assert.True(Alive(pid));
    }

    [Fact]
    public void A_changed_forward_gets_a_new_relay_and_a_deleted_one_is_stopped()
    {
        Run("15432", "postgres", "5432", "-", "16379", "cache", "6379", "-");
        var old = Pid(15432);
        var deleted = Pid(16379);

        Run("15432", "db.internal", "5432", "-");

        Assert.False(File.Exists(Path.Combine(State, "16379.pid")));
        for (var i = 0; i < 50 && (Alive(old) || Alive(deleted)); i++) Thread.Sleep(50);
        Assert.False(Alive(old));
        Assert.False(Alive(deleted));
        Assert.True(Alive(Pid(15432)));
        Assert.Contains("TCP-LISTEN:15432,bind=127.0.0.1,reuseaddr,fork PROXY:ild:db.internal:5432,proxyport=3128", Started());
    }

    [Fact]
    public void A_forward_with_an_address_listens_on_its_destination_port_there_as_well_as_on_its_local_port()
    {
        Run("15432", "postgres", "5432", "127.1.2.3");
        var named = Pid(15432);
        Run("15432", "postgres", "5432", "127.1.2.3");

        Assert.Equal(2, Started().Length);
        Assert.Contains("TCP-LISTEN:15432,bind=127.0.0.1,reuseaddr,fork PROXY:ild:postgres:5432,proxyport=3128", Started());
        Assert.Contains("TCP-LISTEN:5432,bind=127.1.2.3,reuseaddr,fork PROXY:ild:postgres:5432,proxyport=3128", Started());
        Assert.Equal(named, Pid(15432));
    }

    [Fact]
    public void A_removed_forward_loses_both_listeners()
    {
        Run("15432", "postgres", "5432", "127.1.2.3");
        var pids = Directory.GetFiles(State, "*.pid").Select(f => int.Parse(File.ReadAllText(f).Trim())).ToList();
        Assert.Equal(2, pids.Count);

        Run();

        Assert.Empty(Directory.GetFiles(State, "*.pid"));
        for (var i = 0; i < 50 && pids.Any(Alive); i++) Thread.Sleep(50);
        Assert.All(pids, pid => Assert.False(Alive(pid)));
    }

    [Fact]
    public void Two_forwards_to_one_port_on_different_hosts_each_listen_on_their_own_address()
    {
        Run("15432", "postgres", "5432", "127.1.2.3", "15433", "replica", "5432", "127.1.4.5");

        Assert.Equal(4, Started().Length);
        Assert.Contains("TCP-LISTEN:5432,bind=127.1.2.3,reuseaddr,fork PROXY:ild:postgres:5432,proxyport=3128", Started());
        Assert.Contains("TCP-LISTEN:5432,bind=127.1.4.5,reuseaddr,fork PROXY:ild:replica:5432,proxyport=3128", Started());
    }

    [Fact]
    public void A_forward_without_an_address_gets_the_local_listener_only()
    {
        Run("15432", "10.0.0.5", "5432", "-");

        Assert.Equal(["TCP-LISTEN:15432,bind=127.0.0.1,reuseaddr,fork PROXY:ild:10.0.0.5:5432,proxyport=3128"], Started());
    }

    [Fact]
    public void The_hosts_script_maps_each_name_in_a_block_and_keeps_the_other_lines()
    {
        File.WriteAllText(HostsFile, "127.0.0.1 localhost\n172.17.0.2 abc123\n");

        Assert.Equal(0, RunHosts("127.1.2.3", "postgres", "127.1.4.5", "cache").ExitCode);
        Assert.Equal("127.0.0.1 localhost\n172.17.0.2 abc123\n# ild-forwards begin\n127.1.2.3 postgres\n127.1.4.5 cache\n# ild-forwards end\n", File.ReadAllText(HostsFile));

        // A change replaces the block, it does not add a second one, and removing the last forward removes the block.
        Assert.Equal(0, RunHosts("127.1.4.5", "cache").ExitCode);
        Assert.Equal("127.0.0.1 localhost\n172.17.0.2 abc123\n# ild-forwards begin\n127.1.4.5 cache\n# ild-forwards end\n", File.ReadAllText(HostsFile));
        Assert.Equal(0, RunHosts().ExitCode);
        Assert.Equal("127.0.0.1 localhost\n172.17.0.2 abc123\n", File.ReadAllText(HostsFile));
    }

    [Fact]
    public void A_hosts_file_that_cannot_be_written_gives_exit_3_and_changes_nothing_and_the_local_listener_still_starts()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && Environment.UserName != "root", "needs a user that file permissions bind");
        File.WriteAllText(HostsFile, "127.0.0.1 localhost\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(HostsFile, UnixFileMode.UserRead);

        var result = RunHosts("127.1.2.3", "postgres");

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("127.0.0.1 localhost\n", File.ReadAllText(HostsFile));
        Run("15432", "postgres", "5432", "-");
        Assert.Equal(["TCP-LISTEN:15432,bind=127.0.0.1,reuseaddr,fork PROXY:ild:postgres:5432,proxyport=3128"], Started());
    }
}
