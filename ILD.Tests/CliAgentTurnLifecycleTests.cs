using System.Diagnostics;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Implementations.ModelGateway;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests;

/// <summary>
/// The agent turn every CLI adapter shares, driven through a scripted turn on a fake host: what it refuses, how it reports a
/// CLI that does not start, outlives the turn or cannot be killed, and what it makes of the processes the agent left running.
/// </summary>
public class CliAgentTurnLifecycleTests
{
    private const string Worktree = "/fake-worktrees/run";

    private readonly FakeRunHost _host = new FakeRunHost().WithDirectory(Worktree);

    private readonly CountingGateway _gateway = new();

    private ScriptedAdapter Adapter() => new(Services(_host, _gateway), _gateway);

    private static IServiceScopeFactory Services(FakeRunHost host, IModelGatewayRegistry gateway)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRunHost>(host);
        services.AddSingleton(gateway);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static AgentExecutionContext Context(
        string worktree = Worktree, CancellationToken? cancel = null, Guid? chat = null, bool noTools = false)
        => new(
            Provider: new AiProvider { Name = "p", Type = "scripted", BaseUrl = string.Empty, Model = string.Empty },
            Prompt: "prompt",
            RunContext: new LoopRunContext(Guid.NewGuid(), "wi", "t", "d", worktree, "main", new List<string>(), null),
            ExecutionCount: 1,
            Cancel: cancel ?? TestContext.Current.CancellationToken,
            ChatSessionId: chat,
            NoTools: noTools);

    [Fact]
    public async Task A_turn_ends_with_what_the_cli_printed_and_how_it_exited()
    {
        _host.Script = _ => FakeCommandScript.Prints("answer", "noise", 3);
        var adapter = Adapter();

        var result = await adapter.ExecuteAsync(Context());

        Assert.True(result.Success);
        Assert.Equal("answer|noise|3", result.Output);
        Assert.Equal(["prepare", "launch", "input", "stdout", "complete", "dispose"], adapter.Turn!.Steps);
    }

    [Fact]
    public async Task A_run_without_its_worktree_is_refused_before_anything_is_prepared()
    {
        var adapter = Adapter();

        var result = await adapter.ExecuteAsync(Context(worktree: "/not/there"));

        Assert.Equal("[scripted-error] AI node requires a valid worktree path; refusing to run outside the loop's worktree.", result.Error);
        Assert.Null(adapter.Turn);
        Assert.Empty(_host.Started);
    }

    [Fact]
    public async Task A_call_that_must_have_no_tools_is_refused_by_a_cli_that_cannot_run_without_them()
    {
        var adapter = Adapter();
        adapter.SupportsNoTools = false;

        var result = await adapter.ExecuteAsync(Context(noTools: true));

        Assert.Equal("[scripted-error] the Scripted CLI cannot run without its tools, so it cannot take a call that must have none.", result.Error);
        Assert.Empty(_host.Started);
    }

    [Fact]
    public async Task A_cli_that_cannot_be_started_is_named_with_what_to_do_about_it()
    {
        _host.Script = _ => throw new InvalidOperationException("no such file");
        var adapter = Adapter();

        var result = await adapter.ExecuteAsync(Context());

        Assert.Equal("[scripted-error] cannot start '/bin/scripted' — install it. Details: no such file", result.Error);
        Assert.Contains("dispose", adapter.Turn!.Steps);
    }

    [Fact]
    public async Task A_launch_the_turn_offers_after_a_failed_start_is_tried_once()
    {
        _host.Script = command => command.Start.ArgumentList.Contains("retry")
            ? FakeCommandScript.Prints("second")
            : throw new IOException("first failed");
        var adapter = Adapter();
        adapter.OffersRetry = true;

        var result = await adapter.ExecuteAsync(Context());

        Assert.True(result.Success);
        Assert.Equal("second||0", result.Output);
    }

    [Fact]
    public async Task A_retry_that_does_not_start_either_reports_its_own_failure()
    {
        _host.Script = _ => throw new IOException("still failing");
        var adapter = Adapter();
        adapter.OffersRetry = true;

        var result = await adapter.ExecuteAsync(Context());

        Assert.Equal("[scripted-error] cannot start '/bin/scripted' — install it. Details: still failing", result.Error);
    }

    [Fact]
    public async Task A_cli_that_outlives_the_turn_is_killed_and_the_turn_is_told()
    {
        _host.Script = _ => FakeCommandScript.Hangs();
        using var cancel = new CancellationTokenSource();
        var adapter = Adapter();

        var running = adapter.ExecuteAsync(Context(cancel: cancel.Token));
        cancel.Cancel();
        var result = await running;

        Assert.Equal("scripted timed out", result.Error);
        Assert.Equal(["prepare", "launch", "input", "stdout", "timeout", "dispose"], adapter.Turn!.Steps);
    }

    [Fact]
    public async Task A_cli_that_cannot_be_killed_is_reported_as_possibly_still_running()
    {
        _host.Script = _ => FakeCommandScript.RefusesKill();
        using var cancel = new CancellationTokenSource();

        var running = Adapter().ExecuteAsync(Context(cancel: cancel.Token));
        cancel.Cancel();
        var result = await running;

        Assert.Equal("scripted timed out — WARNING: the agent process could not be killed and may still be running.", result.Error);
    }

    [Fact]
    public async Task A_turn_that_fails_itself_ends_with_its_own_error()
    {
        var adapter = Adapter();
        adapter.PrepareFailure = "[scripted-error] nothing to run";

        var result = await adapter.ExecuteAsync(Context());

        Assert.Equal("[scripted-error] nothing to run", result.Error);
        Assert.Empty(_host.Started);
        Assert.Equal(["prepare", "dispose"], adapter.Turn!.Steps);
    }

    [Fact]
    public async Task A_turn_that_ends_before_it_reads_the_gateway_opens_no_lease_on_it()
    {
        var adapter = Adapter();
        adapter.PrepareFailure = "[scripted-error] nothing to run";

        await adapter.ExecuteAsync(Context());

        Assert.Equal(0, _gateway.Opened);
    }

    [Fact]
    public async Task The_lease_a_turn_reads_is_opened_then_and_ended_with_the_turn()
    {
        var adapter = Adapter();

        await adapter.ExecuteAsync(Context());

        Assert.Equal(["prepare:0", "launch:1"], adapter.Turn!.LeasesOpened);
        Assert.Equal((1, 1), (_gateway.Opened, _gateway.Ended));
    }

    [Fact]
    public async Task Anything_else_that_goes_wrong_fails_the_turn_under_the_clis_name()
    {
        var adapter = Adapter();
        adapter.CompleteThrows = new FormatException("unreadable");

        var result = await adapter.ExecuteAsync(Context());

        Assert.Equal("[scripted-error] unreadable", result.Error);
        Assert.Contains("dispose", adapter.Turn!.Steps);
    }

    [Fact]
    public async Task Processes_left_running_at_the_end_of_the_turn_come_back_with_a_successful_result()
    {
        var child = _host.LeaveRunning(4242, "dotnet test");
        _host.LeaveRunning(4243, "/bin/scripted --fingerprint");
        _host.StillRunningFailure = new InvalidOperationException("cannot tell");
        var adapter = Adapter();
        adapter.Fingerprint = "--fingerprint";
        var ctx = Context();

        var result = await adapter.ExecuteAsync(ctx);

        Assert.Equal([child], result.LiveChildren);
        var launch = Assert.Single(_host.Started);
        Assert.Equal(ctx.RunContext.LoopRunId.ToString("N"), launch.Environment![AgentChildren.LaunchVariable]);
        Assert.Equal("1", launch.Environment["FROM_TURN"]);
        Assert.Equal(ctx.RunContext.LoopRunId.ToString("N"), Assert.Single(_host.ListChildrenCalls).LaunchId);
    }

    [Fact]
    public async Task A_process_that_ended_with_the_cli_is_not_left_running_whatever_completing_the_turn_then_meets()
    {
        _host.LeaveRunning(4242, "language-server");
        var adapter = Adapter();
        adapter.Fingerprint = "--fingerprint";
        adapter.AfterTurnEnded = () => _host.RunningPids.Remove(4242);
        adapter.OnComplete = () => _host.StillRunningFailure = new OperationCanceledException();

        var result = await adapter.ExecuteAsync(Context());

        Assert.True(result.Success);
        Assert.Empty(result.LiveChildren!);
    }

    [Fact]
    public async Task A_failed_turn_reports_no_processes_left_running()
    {
        _host.LeaveRunning(4242, "dotnet test");
        var adapter = Adapter();
        adapter.Fingerprint = "--fingerprint";
        adapter.Succeeds = false;

        var result = await adapter.ExecuteAsync(Context());

        Assert.False(result.Success);
        Assert.Null(result.LiveChildren);
    }

    [Fact]
    public async Task A_chat_turn_and_a_cli_without_a_fingerprint_look_for_no_processes()
    {
        _host.LeaveRunning(4242, "dotnet test");
        var chat = Adapter();
        chat.Fingerprint = "--fingerprint";

        var chatResult = await chat.ExecuteAsync(Context(chat: Guid.NewGuid()));
        var plainResult = await Adapter().ExecuteAsync(Context());

        Assert.Null(chatResult.LiveChildren);
        Assert.Null(plainResult.LiveChildren);
        Assert.Empty(_host.ListChildrenCalls);
        Assert.All(_host.Started, launch => Assert.False(launch.Environment!.ContainsKey(AgentChildren.LaunchVariable)));
        Assert.Null(chat.Turn!.TurnEnded);
    }

    private sealed class CountingGateway : IModelGatewayRegistry
    {
        public int Opened { get; private set; }
        public int Ended { get; private set; }

        public ModelGatewayLease Open(Guid providerId, Guid ownerId, string apiUrl, IReadOnlyList<string> modelHosts)
        {
            Opened++;
            return new ModelGatewayLease("token", apiUrl, () => Ended++);
        }

        public GatewayTokenState Lookup(string? presented, out GatewayGrant? grant)
        {
            grant = null;
            return GatewayTokenState.Unknown;
        }

        public bool BlocksHost(Guid? providerId, string host) => false;
    }

    private sealed class ScriptedAdapter(IServiceScopeFactory scopes, CountingGateway gateway) : CliAgentAdapterBase(scopes)
    {
        public CountingGateway Gateway => gateway;
        public bool SupportsNoTools { get; set; } = true;
        public bool OffersRetry { get; set; }
        public bool Succeeds { get; set; } = true;
        public string? Fingerprint { get; set; }
        public string? PrepareFailure { get; set; }
        public Exception? CompleteThrows { get; set; }
        public Action? AfterTurnEnded { get; set; }
        public Action? OnComplete { get; set; }
        public ScriptedTurn? Turn { get; private set; }

        public override string Name => "Scripted";
        public override string[] SupportedProviderTypes => ["scripted"];
        public override AgentAdapterTraits Traits
            => ClaudeCodeAdapter.TypeTraits with { DisplayName = "Scripted", SupportsNoTools = SupportsNoTools };

        protected override string CliName => "scripted";
        protected override string StartHelp => "install it.";
        protected override string? LaunchFingerprint => Fingerprint;

        protected override CliAgentTurn CreateTurn(AgentExecutionContext ctx, string worktreePath, Lazy<ModelGatewayLease?> gateway)
            => Turn = new ScriptedTurn(this, worktreePath, gateway);

        public sealed class ScriptedTurn(ScriptedAdapter adapter, string worktreePath, Lazy<ModelGatewayLease?> gateway) : CliAgentTurn
        {
            private string _stdout = string.Empty;

            public List<string> Steps { get; } = [];

            /// <summary>How many leases the gateway had opened by the end of each step; this turn reads its own when it launches.</summary>
            public List<string> LeasesOpened { get; } = [];

            public override string BinaryPath => "/bin/scripted";

            public override Task PrepareAsync()
            {
                Steps.Add("prepare");
                LeasesOpened.Add($"prepare:{adapter.Gateway.Opened}");
                return adapter.PrepareFailure is { } failure ? throw Failed(failure) : Task.CompletedTask;
            }

            public override Task<AgentLaunch> LaunchAsync()
            {
                Steps.Add("launch");
                _ = gateway.Value;
                LeasesOpened.Add($"launch:{adapter.Gateway.Opened}");
                return Task.FromResult(Launch());
            }

            public override Task<AgentLaunch?> RetryLaunchAsync(Exception failure)
                => Task.FromResult(adapter.OffersRetry ? Launch("retry") : null);

            private AgentLaunch Launch(params string[] arguments)
            {
                var psi = new ProcessStartInfo(BinaryPath) { WorkingDirectory = worktreePath };
                foreach (var argument in arguments) psi.ArgumentList.Add(argument);
                return new AgentLaunch(psi, new Dictionary<string, string?> { ["FROM_TURN"] = "1" });
            }

            public override Task SendInputAsync(IRunProcess process)
            {
                Steps.Add("input");
                return Task.CompletedTask;
            }

            public override async Task ReadStdoutAsync(StreamReader stdout)
            {
                Steps.Add("stdout");
                _stdout = await stdout.ReadToEndAsync();
                if (TurnEnded is not null) await TurnEnded();
                adapter.AfterTurnEnded?.Invoke();
            }

            public override Task AfterTimeoutAsync(Task stdout)
            {
                Steps.Add("timeout");
                return Task.CompletedTask;
            }

            public override Task<NodeExecutionResult> CompleteAsync(string stderr, int exitCode)
            {
                Steps.Add("complete");
                adapter.OnComplete?.Invoke();
                if (adapter.CompleteThrows is { } failure) throw failure;
                var output = $"{_stdout}|{stderr}|{exitCode}";
                return Task.FromResult(adapter.Succeeds ? NodeExecutionResult.Ok(output) : NodeExecutionResult.Fail("failed", output));
            }

            public override ValueTask DisposeAsync()
            {
                Steps.Add("dispose");
                return ValueTask.CompletedTask;
            }
        }
    }
}
