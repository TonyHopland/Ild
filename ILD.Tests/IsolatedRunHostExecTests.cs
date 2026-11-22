using System.Diagnostics;
using System.Text;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Isolated;
using ILD.Core.Services.RunHosts.Local;
using ILD.Tests.Fakes;

namespace ILD.Tests;

/// <summary>
/// What a command, a file and a bundle do in a run, whichever engine holds the run, on an engine kept in memory: the output
/// and exit code a caller sees, the input it gives, the forwards declared at a command's start, how ILD is named to the run,
/// and the files and bundles that go in and out. Where a command's environment travels is each engine's own.
/// </summary>
[Collection(ProcessGlobalStateCollection.Name)]
public sealed class IsolatedRunHostExecTests : IDisposable
{
    private static readonly string[] IldScriptPrefix = ["/usr/bin/env", "-i", "PATH=/usr/sbin:/usr/bin:/sbin:/bin"];

    private readonly string? _previousProxyPort = Environment.GetEnvironmentVariable(AgentIsolation.EgressProxyPortEnvVar);
    private readonly Guid _run = Guid.NewGuid();
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), $"ild-isolated-exec-{Guid.NewGuid():N}");

    public IsolatedRunHostExecTests()
    {
        Environment.SetEnvironmentVariable(AgentIsolation.EgressProxyPortEnvVar, "3128");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AgentIsolation.EgressProxyPortEnvVar, _previousProxyPort);
        if (Directory.Exists(_scratch)) Directory.Delete(_scratch, recursive: true);
    }

    private static InMemoryRunHost NewHost(bool environmentInAFile = false, string? ildAddress = null, Func<CancellationToken, Task<IReadOnlyList<RunForward>>>? forwards = null)
        => new() { Credentials = EgressProxyTests.Creds, EnvironmentInAFile = environmentInAFile, IldAddress = ildAddress ?? "http://ild.example:8080", Forwards = forwards };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string WorkingDirectory => RunPaths.WorkingDirectory(_run);

    private static ProcessStartInfo Command(string workingDirectory, string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    private async Task RunAsync(InMemoryRunHost host, RunCommand command)
    {
        using var process = await host.StartCommandAsync(command, Ct);
        await process.WaitForExitAsync(Ct);
    }

    private string MakeBaseRepository()
    {
        var path = Path.Combine(_scratch, "base");
        Directory.CreateDirectory(path);
        GitCli.Run(path, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(path, "a.txt"), "a");
        GitCli.Run(path, "add", "-A");
        GitCli.Run(path, "-c", "user.email=t@example.test", "-c", "user.name=T", "commit", "-q", "-m", "first");
        GitCli.Run(path, "remote", "add", "origin", "https://user:secret@forge.example/acme/app.git");
        return path;
    }

    // -- Output, exit code and input -----------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_command_streams_its_output_and_reports_its_exit_code_with_only_the_environment_the_caller_set(bool inAFile)
    {
        var host = NewHost(inAFile);
        host.OnExec = async e =>
        {
            if (e.WantsStdin) await e.ReadStdinToEndAsync();
            if (e.Joined.Contains("make-it"))
            {
                await e.WriteStdoutAsync("hello\n");
                await e.WriteStderrAsync("warn\n");
                await e.ExitAsync(3);
            }
            else await e.ExitAsync(0);
        };

        using var process = await host.StartCommandAsync(
            RunCommand.Prepared(Command(WorkingDirectory, "make-it", "a b"), _run, new Dictionary<string, string?> { ["ILD_TEST_ONLY"] = "yes" }), Ct);
        await process.WaitForExitAsync(Ct);

        Assert.Equal("hello\n", await process.StandardOutput.ReadToEndAsync(Ct));
        Assert.Equal("warn\n", await process.StandardError.ReadToEndAsync(Ct));
        Assert.Equal(3, process.ExitCode);
        var exec = host.Execs.Single(e => e.Joined.Contains("make-it"));
        Assert.Equal(host.NameOf(_run), exec.Name);
        var launch = await host.LaunchEnvironmentAsync();
        Assert.Contains("ILD_TEST_ONLY=yes", launch);
        Assert.Equal($"HOME={RunPaths.HomeDirectory(_run)}", launch.Single(c => c.StartsWith("HOME=", StringComparison.Ordinal)));
        Assert.DoesNotContain(launch, c => c.StartsWith("PATH=", StringComparison.Ordinal));
        Assert.DoesNotContain(exec.Command, c => c.Contains("ILD_TEST_ONLY", StringComparison.Ordinal));
        Assert.Equal(["make-it", "a b"], exec.Command.TakeLast(2));
        Assert.Contains(WorkingDirectory, exec.Command);
    }

    [Fact]
    public async Task A_path_is_resolved_to_where_the_run_says_it_leads_and_none_when_it_cannot_be()
    {
        var host = NewHost();
        host.OnExec = async e =>
        {
            if (e.Joined.Contains("realpath")) await e.WriteStdoutAsync("/etc/passwd\n");
            await e.ExitAsync(e.Joined.Contains("broken") ? 1 : 0);
        };

        Assert.Equal("/etc/passwd", await host.ResolveLinksAsync($"{WorkingDirectory}/out/etc/passwd", Ct));
        Assert.Null(await host.ResolveLinksAsync($"{WorkingDirectory}/broken", Ct));
    }

    [Fact]
    public async Task Closing_the_input_of_a_command_ends_what_it_reads_to_the_end()
    {
        var host = NewHost();
        host.OnExec = async e =>
        {
            var input = Encoding.UTF8.GetString(await e.ReadStdinToEndAsync());
            await e.WriteStdoutAsync(input.ToUpperInvariant());
            await e.ExitAsync(0);
        };
        var psi = Command(WorkingDirectory, "cat");
        psi.RedirectStandardInput = true;

        using var process = await host.StartCommandAsync(RunCommand.Prepared(psi, _run), Ct);
        await process.StandardInput.WriteAsync("prompt text");
        process.StandardInput.Close();
        await process.WaitForExitAsync(Ct);

        Assert.Equal("PROMPT TEXT", await process.StandardOutput.ReadToEndAsync(Ct));
        Assert.Equal(0, process.ExitCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_command_that_reads_its_own_stdin_gets_it_and_nothing_else_with_an_environment_too(bool inAFile)
    {
        var host = NewHost(inAFile);
        host.OnExec = async e =>
        {
            var input = Encoding.UTF8.GetString(await e.ReadStdinToEndAsync());
            if (!e.Command.Contains(ExecRunHost.WriteEnvironmentFileScript)) await e.WriteStdoutAsync(input);
            await e.ExitAsync(0);
        };
        var psi = Command(WorkingDirectory, "cat");
        psi.RedirectStandardInput = true;

        using var process = await host.StartCommandAsync(RunCommand.Prepared(psi, _run, new Dictionary<string, string?> { ["ILD_API_TOKEN"] = "token-value" }), Ct);
        await process.StandardInput.WriteAsync("prompt text");
        process.StandardInput.Close();
        await process.WaitForExitAsync(Ct);

        Assert.Equal("prompt text", await process.StandardOutput.ReadToEndAsync(Ct));
        Assert.Equal("prompt text", host.Execs.Single(e => e.Command.Contains("cat")).StdinSoFar());
    }

    [Fact]
    public async Task A_dropped_stream_fails_the_command_instead_of_leaving_it_waiting()
    {
        var host = NewHost();
        host.OnExec = async e =>
        {
            await e.WriteStdoutAsync("partial");
            await e.DropAsync();
        };

        using var process = await host.StartCommandAsync(RunCommand.Prepared(Command(WorkingDirectory, "long-job"), _run), Ct);
        await process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.NotEqual(0, process.ExitCode);
        Assert.Equal("partial", await process.StandardOutput.ReadToEndAsync(Ct));
        Assert.Contains("connection to the run's pod", await process.StandardError.ReadToEndAsync(Ct));
    }

    [Fact]
    public async Task Kill_all_for_a_run_with_no_environment_asks_nothing_of_the_engine_and_kills_nothing()
    {
        var host = NewHost();
        host.HoldsRun = _ => false;

        var killed = await host.KillAllAsync(Guid.NewGuid(), Ct);

        Assert.Equal(0, killed);
        Assert.Empty(host.Execs);
    }

    // -- Forwards ------------------------------------------------------------------

    [Fact]
    public async Task A_command_starts_with_the_declared_forwards_answering_on_the_runs_loopback_through_the_proxy()
    {
        var host = NewHost(forwards: _ => Task.FromResult<IReadOnlyList<RunForward>>(
            [new(15432, "postgres", 5432), new(16379, "bad,host", 6379)]));

        await RunAsync(host, RunCommand.Prepared(Command(WorkingDirectory, "make-it"), _run));

        var sync = host.Execs.Single(e => e.Command.Contains(ExecRunHost.ForwardsScript));
        Assert.Equal(host.NameOf(_run), sync.Name);
        var address = ForwardAddresses.Allocate(["postgres"])["postgres"];
        Assert.Equal(["15432", "postgres", "5432", address], sync.Command.TakeLast(4));
        // The name is mapped to that address first.
        var hosts = host.Execs.Single(e => e.Command.Contains(ExecRunHost.HostsScript));
        Assert.Equal(IldScriptPrefix.Append("/bin/bash").Append("-c"), hosts.Command.Take(5));
        Assert.Equal([address, "postgres"], hosts.Command.TakeLast(2));
        var order = host.Execs.ToList();
        Assert.True(order.IndexOf(hosts) < order.IndexOf(sync));
        // A host that could carry a socat option is not given a relay.
        Assert.DoesNotContain("bad,host", sync.Command);
        Assert.True(order.IndexOf(sync) < order.FindIndex(e => e.Command.Contains("make-it")));
    }

    [Fact]
    public async Task ILDs_own_scripts_start_with_a_fixed_environment_and_the_runs_own_command_does_not()
    {
        var host = NewHost(forwards: _ => Task.FromResult<IReadOnlyList<RunForward>>([new(15432, "postgres", 5432)]));

        await RunAsync(host, RunCommand.Prepared(Command(WorkingDirectory, "make-it"), _run));
        await host.GetFileLengthAsync($"{WorkingDirectory}/file", Ct);

        var own = host.Execs.Where(e => e.Command.Contains(ExecRunHost.ForwardsScript) || e.Command.Contains(ExecRunHost.HostsScript) || e.Joined.Contains("stat -c %s")).ToList();
        Assert.Equal(3, own.Count);
        Assert.All(own, e => Assert.Equal(IldScriptPrefix, e.Command.Take(3)));
        var run = host.Execs.Single(e => e.Command.Contains("make-it"));
        Assert.NotEqual("/usr/bin/env", run.Command[0]);
        Assert.Contains(run.Env, c => c.StartsWith("HOME=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_no_forwards_a_command_costs_no_extra_exec_until_one_was_given_then_its_relays_are_stopped()
    {
        IReadOnlyList<RunForward> forwards = [];
        var host = NewHost(forwards: _ => Task.FromResult(forwards));

        await RunAsync(host, RunCommand.Prepared(Command(WorkingDirectory, "a"), _run));
        Assert.DoesNotContain(host.Execs, e => e.Command.Contains(ExecRunHost.ForwardsScript));

        forwards = [new(15432, "postgres", 5432)];
        await RunAsync(host, RunCommand.Prepared(Command(WorkingDirectory, "b"), _run));
        forwards = [];
        await RunAsync(host, RunCommand.Prepared(Command(WorkingDirectory, "c"), _run));

        var syncs = host.Execs.Where(e => e.Command.Contains(ExecRunHost.ForwardsScript)).ToList();
        Assert.Equal(2, syncs.Count);
        Assert.Equal("5432", syncs[0].Command[^2]);
        // Only the proxy's host and port: every relay is stopped.
        Assert.Equal(IldScriptPrefix.Concat(["/bin/bash", "-c", ExecRunHost.ForwardsScript, "bash"]), syncs[1].Command.Take(7));
        Assert.Equal(9, syncs[1].Command.Count);
    }

    // -- How ILD is named to the run ---------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Proxy_variables_that_name_ilds_loopback_name_ilds_address_for_the_run(bool inAFile)
    {
        var host = NewHost(inAFile);

        await RunAsync(host, RunCommand.Prepared(Command(WorkingDirectory, "curl"), _run, new Dictionary<string, string?> { ["HTTP_PROXY"] = "http://scope:abc@127.0.0.1:3128" }));

        var proxy = Assert.Single(await host.LaunchEnvironmentAsync(), c => c.StartsWith("HTTP_PROXY=", StringComparison.Ordinal));
        Assert.Contains("ild.example:3128", proxy);
        Assert.DoesNotContain("127.0.0.1", proxy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_host_that_is_a_suffix_of_the_preview_domain_is_not_put_on_NO_PROXY_and_ild_is_reached_by_another_name(bool inAFile)
    {
        var host = NewHost(inAFile, ildAddress: "http://ild-api:8080");

        await RunAsync(host, RunCommand.Prepared(Command(WorkingDirectory, "curl"), _run));

        var noProxy = Assert.Single(await host.LaunchEnvironmentAsync(), c => c.StartsWith("NO_PROXY=", StringComparison.Ordinal))[9..].Split(',');
        Assert.Contains("ild-api", noProxy);
        Assert.DoesNotContain(noProxy, e => "x.preview.ild".EndsWith(e.TrimStart('.'), StringComparison.Ordinal) && e is not ("localhost" or "127.0.0.1" or "::1" or "[::1]"));
        Assert.Equal("http://ild-api:8080", host.GatewayApiUrl(null, WorkingDirectory));
        var mcp = IldMcpServer.BuildEnvironment(null, environment: new RunFacingEnvironment(new TestProcessEnvironment { { "ILD_API_URL", "http://ild-api:8080" } }, host, WorkingDirectory));
        Assert.Contains(new Uri((string)mcp["ILD_API_URL"]!).Host, noProxy);
    }


    // -- Files and bundles -----------------------------------------------------------

    private async Task<(InMemoryRunHost Host, int Writes)> WritesAfterOpeningForWriteAsync(string existing, string? written)
    {
        var host = NewHost();
        host.OnExec = async e =>
        {
            if (e.WantsStdin) await e.ReadStdinToEndAsync();
            if (e.Joined.Contains("cat --")) await e.WriteStdoutAsync(existing);
            await e.ExitAsync(0);
        };
        var path = $"{WorkingDirectory}/scripts/build.sh";
        await using (var stream = await host.OpenFileForWriteAsync(path, Ct))
        {
            if (written is not null)
            {
                stream.SetLength(0);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(written), Ct);
            }
        }
        return (host, host.Execs.Count(e => e.Command.Contains(ExecRunHost.WriteFileScript)));
    }

    [Fact]
    public async Task A_file_opened_for_write_but_not_written_is_not_rewritten()
        => Assert.Equal(0, (await WritesAfterOpeningForWriteAsync("abc", null)).Writes);

    [Fact]
    public async Task A_file_saved_with_the_same_bytes_is_not_rewritten()
        => Assert.Equal(0, (await WritesAfterOpeningForWriteAsync("abc", "abc")).Writes);

    [Fact]
    public async Task An_edited_file_is_written_back_once_in_place()
    {
        var (host, writes) = await WritesAfterOpeningForWriteAsync("abc", "changed");

        Assert.Equal(1, writes);
        var write = host.Execs.Last();
        Assert.Equal("changed", Encoding.UTF8.GetString(await write.ReadStdinToEndAsync()));
        Assert.Equal("rw", write.Command[^1]);
    }

    [Fact]
    public async Task A_bundle_comes_out_of_the_run_into_the_sink()
    {
        var host = NewHost();
        host.OnExec = async e =>
        {
            await e.WriteStdoutAsync("BUNDLE-BYTES");
            await e.ExitAsync(0);
        };
        using var sink = new MemoryStream();

        var result = await host.PullBundleAsync(WorkingDirectory, "ild/wi-1-run", incremental: true, sink, Ct);

        Assert.True(result.Success);
        Assert.Equal("BUNDLE-BYTES", Encoding.UTF8.GetString(sink.ToArray()));
        Assert.Contains("--remotes=origin", host.Execs.Last().Command);
    }

    [Fact]
    public async Task Create_puts_the_base_repository_in_as_a_bundle_with_the_remote_as_origin_and_checks_out_the_run_branch()
    {
        var host = NewHost();
        // A checkout is not there yet.
        host.OnExec = async e =>
        {
            if (e.WantsStdin) await e.ReadStdinToEndAsync();
            await e.ExitAsync(e.Joined.Contains("-e \"$1/.git\"") ? 1 : 0);
        };

        await host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository(), "ild/wi-1-run", Guid.NewGuid()), Ct);

        var clone = Assert.Single(host.Execs, e => e.Joined.Contains("git clone"));
        Assert.Equal(WorkingDirectory, clone.Command[^2]);
        Assert.Equal("https://forge.example/acme/app.git", clone.Command[^1]);
        Assert.StartsWith("# v2 git bundle", Encoding.ASCII.GetString(await clone.ReadStdinToEndAsync(), 0, 15));
        var checkout = Assert.Single(host.Execs, e => e.Joined.Contains("git checkout"));
        Assert.Equal(["ild/wi-1-run"], checkout.Command.TakeLast(1));
    }
}
