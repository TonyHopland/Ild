using ILD.Core.Services.RunHosts.Local;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using ILD.Api.Services;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The launch seams that read the process-wide proxy port follow the network mode
/// (ADR-0019): in Off no proxy variable; in every other mode the provider-scoped proxy.
/// Runs alone because it sets that variable in the real process environment.
/// </summary>
[Collection(ProcessGlobalStateCollection.Name)]
public sealed class NetworkModeLaunchSeamTests : IDisposable
{
    private static readonly string[] ProxyKeys =
        { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy", "NODE_USE_ENV_PROXY" };

    private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);
    private readonly string _worktree = Path.Combine(Path.GetTempPath(), "ild-net-mode-" + Guid.NewGuid().ToString("N"));

    public NetworkModeLaunchSeamTests()
    {
        Directory.CreateDirectory(_worktree);
        foreach (var key in ProxyKeys) Set(key, null);
        Set(AgentIsolation.EgressProxyPortEnvVar, "3128");
    }

    public void Dispose()
    {
        foreach (var (name, value) in _previous)
            Environment.SetEnvironmentVariable(name, value);
        try { Directory.Delete(_worktree, recursive: true); } catch { /* best effort */ }
    }

    private void Set(string name, string? value)
    {
        _previous.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }

    private static AgentExecutionContext Execution(NetworkMode mode) => new(
        new AiProvider { Id = Guid.NewGuid(), Name = "p", Type = "claude-code", BaseUrl = "http://localhost", Model = "m", Parallelism = 1 },
        "prompt",
        new LoopRunContext(Guid.NewGuid(), "WI-1", "t", "d", "/worktrees/wi-1", "b", new List<string>(), null),
        ExecutionCount: 0,
        Cancel: CancellationToken.None,
        NetworkMode: mode);

    private static ProcessStartInfo Prepare(ProcessStartInfo psi, AgentExecutionContext ctx)
        => LocalRunHost.PrepareAgentLaunch(psi, ctx.Provider.Id, ctx.NetworkMode, ctx.Environment is null ? null : CommandEnvironment.Of(ctx.Environment));

    [Theory]
    [InlineData(NetworkMode.Monitor)]
    [InlineData(NetworkMode.Whitelist)]
    [InlineData(NetworkMode.Blacklist)]
    public void An_adapter_launch_in_a_proxied_mode_gets_the_provider_scoped_proxy(NetworkMode mode)
    {
        var psi = Prepare(new ProcessStartInfo("claude"), Execution(mode));

        Assert.StartsWith("http://provider:", psi.Environment["HTTPS_PROXY"]);
        Assert.EndsWith("@127.0.0.1:3128", psi.Environment["HTTPS_PROXY"]);
        Assert.Equal("1", psi.Environment["NODE_USE_ENV_PROXY"]);
    }

    [Fact]
    public void An_adapter_launch_in_Off_has_no_proxy_variable()
    {
        var psi = Prepare(new ProcessStartInfo("claude"), Execution(NetworkMode.Off));

        foreach (var key in ProxyKeys)
            Assert.False(psi.Environment.ContainsKey(key), $"{key} set for an Off launch");
    }

    [Fact]
    public void The_execution_environment_still_reaches_an_Off_launch()
    {
        var ctx = Execution(NetworkMode.Off) with { Environment = new Dictionary<string, string> { ["FEED_TOKEN"] = "t" } };

        var psi = Prepare(new ProcessStartInfo("claude"), ctx);

        Assert.Equal("t", psi.Environment["FEED_TOKEN"]);
    }

    private async Task<string> RunCmdNodeAsync(IEgressPolicy? policy)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new Mock<IWorkItemManager>().Object);
        services.AddSingleton(NoPackageFeeds.Resolver);
        if (policy is not null) services.AddSingleton(policy);
        var ctx = new NodeExecutionContext(
            new LoopRun { Id = Guid.NewGuid(), WorktreePath = _worktree },
            new LoopNode
            {
                Id = Guid.NewGuid(),
                NodeType = NodeType.Cmd,
                Label = "cmd",
                Config = System.Text.Json.JsonSerializer.Serialize(new { command = "printf %s \"$HTTPS_PROXY\"" }),
            },
            services.BuildServiceProvider(),
            CancellationToken.None);

        NodeOutcome? last = null;
        await foreach (var outcome in new CmdNodeExecutor().ExecuteAsync(ctx)) last = outcome;
        return Assert.IsType<NodeOutcome.Success>(last).Output ?? string.Empty;
    }

    [Fact]
    public async Task A_Cmd_node_runs_without_a_proxy_when_the_policy_reports_Off()
        => Assert.Equal(string.Empty, (await RunCmdNodeAsync(EgressPolicies.Reporting(NetworkMode.Off))).Trim());

    [Theory]
    [InlineData(NetworkMode.Monitor)]
    [InlineData(NetworkMode.Whitelist)]
    public async Task A_Cmd_node_runs_proxied_in_every_other_mode(NetworkMode mode)
        => Assert.Equal("http://127.0.0.1:3128", (await RunCmdNodeAsync(EgressPolicies.Reporting(mode))).Trim());

    // What an isolated host reads to leave the proxy off (and open the direct route) for a command it runs in the run's own environment.
    [Theory]
    [InlineData(NetworkMode.Off)]
    [InlineData(NetworkMode.Monitor)]
    [InlineData(NetworkMode.Whitelist)]
    public async Task A_Cmd_node_hands_its_run_host_the_mode_it_was_launched_in(NetworkMode mode)
    {
        var host = new ILD.Tests.Fakes.FakeRunHost();
        host.WithDirectory(_worktree);
        var services = new ServiceCollection();
        services.AddSingleton<IRunHost>(host);
        services.AddSingleton(new Mock<IWorkItemManager>().Object);
        services.AddSingleton(NoPackageFeeds.Resolver);
        services.AddSingleton(EgressPolicies.Reporting(mode));
        var ctx = new NodeExecutionContext(
            new LoopRun { Id = Guid.NewGuid(), WorktreePath = _worktree },
            new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.Cmd, Label = "cmd", Config = System.Text.Json.JsonSerializer.Serialize(new { command = "true" }) },
            services.BuildServiceProvider(),
            CancellationToken.None);

        await foreach (var _ in new CmdNodeExecutor().ExecuteAsync(ctx)) { }

        Assert.Equal(mode, Assert.Single(host.Started).NetworkMode);
    }

    [Fact]
    public async Task A_Cmd_node_without_a_policy_runs_proxied_as_Monitor()
        => Assert.Equal("http://127.0.0.1:3128", (await RunCmdNodeAsync(null)).Trim());

    private async Task<string> RunTerminalAsync(IEgressPolicy? policy)
    {
        var (server, client) = WebSocketPair.Create();
        var service = new InteractiveProviderSessionService(NullLogger<InteractiveProviderSessionService>.Instance, policy);
        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "test",
            Type = "cat",
            BaseUrl = "http://localhost",
            Model = "n/a",
            Config = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["binaryPath"] = "/usr/bin/printenv",
                ["extraArgs"] = "PATH HTTPS_PROXY",
            }),
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = service.RunAsync(server, provider, initialCols: 200, initialRows: 24, cancellationToken: cts.Token);
        var received = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await client.ReceiveAsync(buffer, cts.Token);
            if (result.MessageType != WebSocketMessageType.Close)
                received.Write(buffer, 0, result.Count);
        }
        while (result.MessageType != WebSocketMessageType.Close);
        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
        await runTask;
        return Encoding.UTF8.GetString(received.ToArray());
    }

    [Fact]
    public async Task The_provider_terminal_has_no_proxy_when_the_policy_reports_Off()
    {
        var output = await RunTerminalAsync(EgressPolicies.Reporting(NetworkMode.Off));

        Assert.Matches(@"/[^\s:]+:/", output);
        Assert.DoesNotContain("127.0.0.1:3128", output);
    }

    [Theory]
    [InlineData(NetworkMode.Monitor)]
    [InlineData(NetworkMode.Whitelist)]
    [InlineData(NetworkMode.Blacklist)]
    public async Task The_provider_terminal_is_proxied_in_every_other_mode(NetworkMode mode)
        => Assert.Contains("127.0.0.1:3128", await RunTerminalAsync(EgressPolicies.Reporting(mode)));

    [Fact]
    public async Task The_provider_terminal_without_a_policy_is_proxied_as_Monitor()
        => Assert.Contains("127.0.0.1:3128", await RunTerminalAsync(null));
}
