using System.Diagnostics;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Isolated;
using ILD.Core.Services.RunHosts.Local;
using ILD.Data.Enums;
using ILD.Tests.Fakes;
using Porta.Pty;

namespace ILD.Tests;

/// <summary>
/// What a terminal in a run does and what a command's secrets never touch, whichever engine holds the run, on an engine kept
/// in memory: a terminal is a tty exec reached through the pty interface, it is given the network as a launch is, and no
/// secret of a command's or a terminal's environment is in an exec's arguments or typed into it.
/// </summary>
[Collection(ProcessGlobalStateCollection.Name)]
public sealed class IsolatedRunHostTerminalTests : IDisposable
{
    private const string GatewayToken = "gateway-token-value-1";
    private const string ApiToken = "ild-api-token-value-2";

    private static readonly string[] ProxyVariables = ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy", "NODE_USE_ENV_PROXY", "NO_PROXY", "no_proxy"];

    private readonly string? _previousProxyPort = Environment.GetEnvironmentVariable(AgentIsolation.EgressProxyPortEnvVar);
    private readonly Guid _run = Guid.NewGuid();

    public IsolatedRunHostTerminalTests() => Environment.SetEnvironmentVariable(AgentIsolation.EgressProxyPortEnvVar, "3128");

    public void Dispose() => Environment.SetEnvironmentVariable(AgentIsolation.EgressProxyPortEnvVar, _previousProxyPort);

    private static InMemoryRunHost NewHost(bool environmentInAFile = false)
        => new() { Credentials = EgressProxyTests.Creds, EnvironmentInAFile = environmentInAFile };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string WorkingDirectory => RunPaths.WorkingDirectory(_run);

    private static Dictionary<string, string?> Secrets() => new() { ["ANTHROPIC_API_KEY"] = GatewayToken, ["ILD_API_TOKEN"] = ApiToken };

    private PtyOptions Terminal(Dictionary<string, string>? environment = null, int cols = 80, int rows = 24)
        => new() { Cwd = WorkingDirectory, App = "/bin/bash", Cols = cols, Rows = rows, Name = "t", Environment = environment ?? [] };

    private static ProcessStartInfo Command(string workingDirectory, string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    // -- The terminal --------------------------------------------------------------

    [Fact]
    public async Task A_terminal_is_a_tty_exec_whose_input_output_and_end_go_through_the_pty_interface()
    {
        var host = NewHost();
        host.OnExec = async e =>
        {
            await e.WriteStdoutAsync("ready$ ");
            while (!e.StdinSoFar().Contains('x')) await Task.Delay(10);
            await e.WriteStdoutAsync("got x");
            await e.ExitAsync(5);
        };
        var exited = new TaskCompletionSource<int>();

        using var pty = await host.OpenTerminalAsync(Terminal(cols: 120, rows: 30), NetworkMode.Monitor, Ct);
        pty.ProcessExited += (_, args) => exited.TrySetResult(args.ExitCode);
        await pty.WriterStream.WriteAsync("x"u8.ToArray(), Ct);
        var read = await new StreamReader(pty.ReaderStream).ReadToEndAsync(Ct);

        Assert.Equal("ready$ got x", read);
        Assert.Equal(5, await exited.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        var exec = host.Execs.Last();
        Assert.True(exec.Tty);
        Assert.Contains("120x30", exec.Resizes);
    }

    [Theory]
    [InlineData(NetworkMode.Monitor, false)]
    [InlineData(NetworkMode.Whitelist, false)]
    [InlineData(NetworkMode.Monitor, true)]
    [InlineData(NetworkMode.Whitelist, true)]
    public async Task A_terminal_in_a_proxied_mode_reaches_the_network_through_the_runs_relay_as_a_launch_does(NetworkMode mode, bool inAFile)
    {
        var host = NewHost(inAFile);

        using var pty = await host.OpenTerminalAsync(Terminal(), mode, Ct);

        var terminal = host.Execs.Last(e => e.Tty);
        Assert.Contains(terminal.Command, c => c.Contains(ExecRunHost.EgressRelayScript, StringComparison.Ordinal));
        var proxyUrl = Assert.Single(await host.LaunchEnvironmentAsync(), e => e.StartsWith("HTTPS_PROXY=", StringComparison.Ordinal))["HTTPS_PROXY=".Length..];
        Assert.EndsWith("@ild.example:3128", proxyUrl);
        Assert.True(AgentIsolationEgressTests.TryReadUrl(EgressProxyTests.Creds, proxyUrl, out var scope));
        Assert.Equal(new LaunchScope(LaunchKind.Run, _run), scope);
        Assert.DoesNotContain(host.Execs.SelectMany(e => e.Command), c => c.Contains(proxyUrl, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_terminal_in_off_has_no_proxy_variables(bool inAFile)
    {
        var host = NewHost(inAFile);

        using var pty = await host.OpenTerminalAsync(Terminal(), NetworkMode.Off, Ct);

        var environment = await host.LaunchEnvironmentAsync();
        Assert.Contains(environment, e => e.StartsWith("HOME=", StringComparison.Ordinal));
        Assert.DoesNotContain(environment, e => ProxyVariables.Any(v => e.StartsWith(v + "=", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_secret_of_a_terminals_environment_is_in_the_exec_command_or_typed_into_the_terminal(bool inAFile)
    {
        var host = NewHost(inAFile);

        using var pty = await host.OpenTerminalAsync(Terminal(new Dictionary<string, string> { ["ILD_API_TOKEN"] = ApiToken }), NetworkMode.Monitor, Ct);

        var terminal = host.Execs.Last(e => e.Tty);
        var environment = await host.LaunchEnvironmentAsync();
        Assert.Contains($"ILD_API_TOKEN={ApiToken}", environment);
        Assert.Contains($"HOME={RunPaths.HomeDirectory(_run)}", environment);
        Assert.DoesNotContain(host.Execs.SelectMany(e => e.Command), c => c.Contains(ApiToken, StringComparison.Ordinal));
        Assert.DoesNotContain(ApiToken, terminal.StdinSoFar());
    }

    // -- A command's secrets -------------------------------------------------------

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task No_secret_of_a_commands_environment_is_in_any_exec_command_or_in_what_its_command_reads(bool asAgent, bool inAFile)
    {
        var host = NewHost(inAFile);
        var psi = Command(WorkingDirectory, "claude", "-p", "hi");

        using var process = await host.StartCommandAsync(
            asAgent ? RunCommand.Agent(psi, _run, null, NetworkMode.Monitor, Secrets()) : RunCommand.Prepared(psi, _run, Secrets()), Ct);
        await process.WaitForExitAsync(Ct);

        var environment = await host.LaunchEnvironmentAsync();
        Assert.Contains($"ANTHROPIC_API_KEY={GatewayToken}", environment);
        Assert.Contains($"ILD_API_TOKEN={ApiToken}", environment);
        Assert.DoesNotContain(host.Execs.SelectMany(e => e.Command), c => c.Contains(GatewayToken, StringComparison.Ordinal) || c.Contains(ApiToken, StringComparison.Ordinal));
        // The one place a secret may be sent is the write of the environment file, by a command that has none in it.
        Assert.DoesNotContain(host.Execs.Where(e => !e.Command.Contains(ExecRunHost.WriteEnvironmentFileScript)),
            e => e.StdinSoFar().Contains(GatewayToken, StringComparison.Ordinal));
    }
}
