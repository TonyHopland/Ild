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
/// CLI that does not start, outlives the turn or cannot be killed.
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
    public async Task A_turn_ends_with_what_the_cli_printed()
    {
        _host.Script = _ => FakeCommandScript.Prints("answer", "noise");
        var adapter = Adapter();

        var result = await adapter.ExecuteAsync(Context());

        Assert.True(result.Success);
        Assert.Equal("answer", result.Output);
        Assert.Equal(["prepare", "launch", "input", "stdout", "parse", "dispose"], adapter.Turn!.Steps);
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
        Assert.Equal("second", result.Output);
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
        adapter.ParseThrows = new FormatException("unreadable");

        var result = await adapter.ExecuteAsync(Context());

        Assert.Equal("[scripted-error] unreadable", result.Error);
        Assert.Contains("dispose", adapter.Turn!.Steps);
    }
}
