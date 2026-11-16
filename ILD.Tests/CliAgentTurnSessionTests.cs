using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Implementations.ModelGateway;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using ILD.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The managed session every CLI adapter shares, driven through a scripted turn: the order the fork, the restore, the read and the
/// save run in, and the conditions each one runs under.
/// </summary>
public class CliAgentTurnSessionTests
{
    private const string Worktree = "/fake-worktrees/run";

    private readonly FakeRunHost _host = new FakeRunHost().WithDirectory(Worktree);
    private readonly List<string> _journal = [];

    public CliAgentTurnSessionTests() => _host.Script = _ => FakeCommandScript.Prints("ignored");
    private Exception? _forkFailure;

    private ScriptedAdapter Adapter()
    {
        var store = new Mock<IAdapterSessionSnapshotStore>();
        store.Setup(s => s.ForkAsync(It.IsAny<SessionSnapshotKey>(), It.IsAny<SessionSnapshotKey>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                _journal.Add("fork");
                return _forkFailure is { } failure ? throw failure : Task.FromResult(true);
            });
        var gateway = new CountingGateway();
        var services = new ServiceCollection();
        services.AddSingleton<IRunHost>(_host);
        services.AddSingleton<IModelGatewayRegistry>(gateway);
        services.AddScoped(_ => store.Object);
        return new ScriptedAdapter(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), gateway, _journal);
    }

    private static AgentExecutionContext Context(string? session = "s-new", string? forkFrom = null, bool manage = true)
        => new(
            Provider: new AiProvider { Name = "p", Type = "scripted", BaseUrl = string.Empty, Model = string.Empty },
            Prompt: "prompt",
            RunContext: new LoopRunContext(Guid.NewGuid(), "wi", "t", "d", Worktree, "main", new List<string>(), null),
            ExecutionCount: 1,
            Cancel: TestContext.Current.CancellationToken,
            SessionId: session,
            ManageSession: manage,
            ForkFromSessionId: forkFrom);

    [Fact]
    public async Task A_managed_session_is_forked_restored_read_launched_and_saved_in_that_order()
    {
        var adapter = Adapter();

        var result = await adapter.ExecuteAsync(Context(forkFrom: "s-src"));

        Assert.True(result.Success, result.Error);
        Assert.Equal(
            ["prepare", "fork", "restore:s-new", "read:s-new", "launch", "input", "stdout", "parse", "save:s-new", "usage", "dispose"],
            _journal);
        Assert.Equal("s-new", adapter.Turn!.LaunchedOn);
    }

    [Fact]
    public async Task A_session_that_is_not_forked_from_another_is_restored_without_a_fork()
    {
        var adapter = Adapter();

        await adapter.ExecuteAsync(Context());

        Assert.DoesNotContain("fork", _journal);
        Assert.Contains("restore:s-new", _journal);
    }

    [Fact]
    public async Task A_session_ILD_does_not_manage_is_resumed_as_it_is_and_neither_forked_restored_nor_saved()
    {
        var adapter = Adapter();

        await adapter.ExecuteAsync(Context(forkFrom: "s-src", manage: false));

        Assert.Equal(["prepare", "read:s-new", "launch", "input", "stdout", "parse", "usage", "dispose"], _journal);
        Assert.Equal("s-new", adapter.Turn!.LaunchedOn);
    }

    [Fact]
    public async Task A_turn_with_no_session_starts_a_new_one_and_saves_what_the_stream_names()
    {
        var adapter = Adapter();
        adapter.ReadAs = stdout => new StreamResult("answer", "s-made", null, true, stdout);

        await adapter.ExecuteAsync(Context(session: null));

        Assert.Equal(["prepare", "read:-", "launch", "input", "stdout", "parse", "save:s-made", "usage", "dispose"], _journal);
        Assert.Null(adapter.Turn!.LaunchedOn);
    }

    [Fact]
    public async Task Nothing_to_restore_launches_a_new_session_and_saves_the_one_it_makes()
    {
        var adapter = Adapter();
        adapter.Restores = false;
        adapter.ReadAs = stdout => new StreamResult("answer", "s-made", null, true, stdout);

        await adapter.ExecuteAsync(Context());

        Assert.Contains("read:-", _journal);
        Assert.Contains("save:s-made", _journal);
        Assert.Null(adapter.Turn!.LaunchedOn);
    }

    [Fact]
    public async Task A_session_id_the_cli_would_not_look_up_is_neither_forked_nor_restored_but_still_read()
    {
        var adapter = Adapter();
        adapter.AcceptsSessionId = _ => false;

        await adapter.ExecuteAsync(Context(forkFrom: "s-src"));

        Assert.DoesNotContain("fork", _journal);
        Assert.DoesNotContain("restore:s-new", _journal);
        Assert.Contains("read:s-new", _journal);
    }

    [Fact]
    public async Task The_session_the_read_answers_with_is_the_one_the_cli_is_launched_on()
    {
        var adapter = Adapter();
        adapter.ReadsSessionAs = _ => null;

        await adapter.ExecuteAsync(Context());

        Assert.Null(adapter.Turn!.LaunchedOn);
    }

    [Fact]
    public async Task A_cli_that_does_not_resume_sessions_has_no_session_protocol()
    {
        var adapter = Adapter();
        adapter.SupportsSessionResume = false;

        await adapter.ExecuteAsync(Context(forkFrom: "s-src"));

        Assert.Equal(["prepare", "read:-", "launch", "input", "stdout", "parse", "usage", "dispose"], _journal);
    }

    [Fact]
    public async Task A_fork_that_fails_ends_the_turn_under_the_clis_name_before_anything_is_launched()
    {
        _forkFailure = new InvalidOperationException("store is down");
        var adapter = Adapter();

        var result = await adapter.ExecuteAsync(Context(forkFrom: "s-src"));

        Assert.False(result.Success);
        Assert.Equal("[scripted-error] could not fork the scripted session s-src into s-new: store is down", result.Error);
        Assert.Equal(["prepare", "fork", "dispose"], _journal);
        Assert.Empty(_host.Started);
    }

    [Fact]
    public async Task A_session_is_saved_by_the_id_the_stream_named_over_the_one_it_was_asked_to_resume()
    {
        var adapter = Adapter();
        adapter.ReadAs = stdout => new StreamResult("answer", "s-other", null, true, stdout);

        await adapter.ExecuteAsync(Context());

        Assert.Contains("save:s-other", _journal);
        Assert.DoesNotContain("save:s-new", _journal);
    }

    [Fact]
    public async Task A_turn_that_exits_non_zero_saves_nothing()
    {
        _host.Script = _ => FakeCommandScript.Prints("out", "boom", 1);
        var adapter = Adapter();

        var result = await adapter.ExecuteAsync(Context());

        Assert.False(result.Success);
        Assert.DoesNotContain("save:s-new", _journal);
    }

    [Fact]
    public async Task A_clean_exit_saves_the_session_even_when_the_turn_is_then_classified_a_failure()
    {
        var adapter = Adapter();
        adapter.ReadAs = stdout => new StreamResult("", null, "provider said no", true, stdout);

        var result = await adapter.ExecuteAsync(Context());

        Assert.False(result.Success);
        Assert.Contains("save:s-new", _journal);
    }

    [Fact]
    public async Task The_save_runs_before_the_outcome_is_decided_and_its_answer_stands_in_for_a_stream_with_none()
    {
        var adapter = Adapter();
        adapter.ReadAs = stdout => new StreamResult("", null, null, true, stdout);
        adapter.SavedText = "the answer, from the saved session";

        var result = await adapter.ExecuteAsync(Context());

        Assert.True(result.Success, result.Error);
        Assert.Equal("the answer, from the saved session", result.Output);
    }

    [Fact]
    public async Task The_answer_the_stream_carried_is_not_replaced_by_the_saved_ones()
    {
        var adapter = Adapter();
        adapter.SavedText = "the answer, from the saved session";

        var result = await adapter.ExecuteAsync(Context());

        Assert.Equal("ignored", result.Output);
    }

    [Fact]
    public async Task A_cancelled_fork_is_not_made_into_a_failure_of_its_own()
    {
        _forkFailure = new OperationCanceledException();
        var adapter = Adapter();

        var result = await adapter.ExecuteAsync(Context(forkFrom: "s-src"));

        Assert.DoesNotContain("could not fork", result.Error);
    }
}
