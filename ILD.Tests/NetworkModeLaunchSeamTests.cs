using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using ILD.Api.Services;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The launch seams that read the process-wide proxy port and open group follow the
/// network mode (ADR-0019): in Off no proxy variable and, under uid isolation, the
/// open group for <c>--regid</c>; in every other mode the launch is as before.
/// Runs alone because it sets those variables in the real process environment.
/// </summary>
[Collection(ProcessGlobalStateCollection.Name)]
public sealed class NetworkModeLaunchSeamTests : IDisposable
{
    private const string OpenGroup = "ild-net-open";

    private static readonly string[] ProxyKeys =
        { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy", "NODE_USE_ENV_PROXY" };

    private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);
    private readonly string _worktree = Path.Combine(Path.GetTempPath(), "ild-net-mode-" + Guid.NewGuid().ToString("N"));

    public NetworkModeLaunchSeamTests()
    {
        Directory.CreateDirectory(_worktree);
        foreach (var key in ProxyKeys) Set(key, null);
        Set(AgentIsolation.EgressProxyPortEnvVar, "3128");
        Set(AgentIsolation.NetworkOpenGroupEnvVar, OpenGroup);
        Set(AgentIsolation.AgentUserEnvVar, null);
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

    private void IsolateAs(string user, string group)
    {
        Set(AgentIsolation.AgentUserEnvVar, user);
        Set(AgentIsolation.AgentGroupEnvVar, group);
        Set(AgentIsolation.AgentHomeEnvVar, "/home/agent");
    }

    private static AgentExecutionContext Execution(NetworkMode mode) => new(
        new AiProvider { Id = Guid.NewGuid(), Name = "p", Type = "claude-code", BaseUrl = "http://localhost", Model = "m", Parallelism = 1 },
        "prompt",
        new LoopRunContext(Guid.NewGuid(), "WI-1", "t", "d", "/worktrees/wi-1", "b", new List<string>(), null),
        ExecutionCount: 0,
        Cancel: CancellationToken.None,
        NetworkMode: mode);

    [Theory]
    [InlineData(NetworkMode.Monitor)]
    [InlineData(NetworkMode.Whitelist)]
    [InlineData(NetworkMode.Blacklist)]
    public void An_adapter_launch_in_a_proxied_mode_is_what_it_was_before_Off_existed(NetworkMode mode)
    {
        IsolateAs("agent", "agent-primary");

        var psi = CliAgentAdapterBase.RouteForExecution(new ProcessStartInfo("claude"), Execution(mode));

        Assert.Contains("--regid=agent-primary", psi.ArgumentList);
        Assert.DoesNotContain($"--regid={OpenGroup}", psi.ArgumentList);
        Assert.StartsWith("http://provider:", psi.Environment["HTTPS_PROXY"]);
        Assert.EndsWith("@127.0.0.1:3128", psi.Environment["HTTPS_PROXY"]);
        Assert.Equal("1", psi.Environment["NODE_USE_ENV_PROXY"]);
    }

    [Fact]
    public void An_adapter_launch_in_Off_runs_under_the_open_group_with_no_proxy_variable()
    {
        IsolateAs("agent", "agent-primary");

        var psi = CliAgentAdapterBase.RouteForExecution(new ProcessStartInfo("claude"), Execution(NetworkMode.Off));

        Assert.Contains($"--regid={OpenGroup}", psi.ArgumentList);
        Assert.Contains("--init-groups", psi.ArgumentList);
        Assert.DoesNotContain("--regid=agent-primary", psi.ArgumentList);
        foreach (var key in ProxyKeys)
            Assert.False(psi.Environment.ContainsKey(key), $"{key} set for an Off launch");
    }

    [Fact]
    public void The_execution_environment_still_reaches_an_Off_launch()
    {
        var ctx = Execution(NetworkMode.Off) with { Environment = new Dictionary<string, string> { ["FEED_TOKEN"] = "t" } };

        var psi = CliAgentAdapterBase.RouteForExecution(new ProcessStartInfo("claude"), ctx);

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
