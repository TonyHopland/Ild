using ILD.Core.Services.RunHosts.Isolated;

namespace ILD.Tests;

/// <summary>The direct-route bookkeeping, on an engine that only records what it was asked.</summary>
public class DirectRoutesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ExecOwner RunA = new(Guid.NewGuid(), Chat: false);
    private static readonly ExecOwner RunB = new(Guid.NewGuid(), Chat: false);
    private static readonly ExecOwner ChatOne = new(Guid.NewGuid(), Chat: true);
    private static readonly ExecOwner ChatTwo = new(Guid.NewGuid(), Chat: true);

    private sealed class StubEngine : IDirectRouteEngine
    {
        private readonly HashSet<ExecOwner> _open = [];
        private int _inFlight;

        public List<string> Calls { get; } = [];
        public int MostInFlight { get; private set; }
        public Func<ExecOwner, Task>? OnOpen { get; set; }
        public Exception? OpenFails { get; set; }

        public async Task OpenAsync(ExecOwner owner, CancellationToken ct)
        {
            await Track(async () =>
            {
                Calls.Add($"open {Key(owner)}");
                if (OnOpen is { } hook) await hook(owner);
                if (OpenFails is { } failure) throw failure;
                lock (_open) _open.Add(Normal(owner));
            });
        }

        public async Task CloseAsync(ExecOwner owner, CancellationToken ct)
        {
            await Track(() =>
            {
                Calls.Add($"close {Key(owner)}");
                lock (_open) _open.Remove(Normal(owner));
                return Task.CompletedTask;
            });
        }

        public Task<bool> HasAsync(ExecOwner owner, CancellationToken ct)
        {
            lock (_open) return Task.FromResult(_open.Contains(Normal(owner)));
        }

        public Task<IReadOnlyList<DirectRoute>> ListAsync(CancellationToken ct)
        {
            lock (_open) return Task.FromResult<IReadOnlyList<DirectRoute>>(_open.Select(o => new DirectRoute(o.Id, o.Chat)).ToList());
        }

        public bool IsOpen(ExecOwner owner)
        {
            lock (_open) return _open.Contains(Normal(owner));
        }

        private static ExecOwner Normal(ExecOwner owner) => owner.Chat ? new ExecOwner(Guid.Empty, true) : owner;

        private static string Key(ExecOwner owner) => owner.Chat ? "chat" : owner.Id.ToString("N")[..4];

        private async Task Track(Func<Task> work)
        {
            MostInFlight = Math.Max(MostInFlight, Interlocked.Increment(ref _inFlight));
            try { await work(); await Task.Yield(); }
            finally { Interlocked.Decrement(ref _inFlight); }
        }
    }

    private static DirectRoutes Routes(StubEngine engine, Action? ensureChat = null)
        => new(engine, _ =>
        {
            ensureChat?.Invoke();
            return Task.CompletedTask;
        });

    [Fact]
    public async Task A_route_stays_open_while_any_process_of_mode_Off_is_live_and_closes_on_the_first_proxied_launch_after_the_last_ends()
    {
        var engine = new StubEngine();
        var routes = Routes(engine);
        var first = await routes.OpenForProcessAsync(RunA, Ct);
        var second = await routes.OpenForProcessAsync(RunA, Ct);

        await routes.CloseIfUnusedAsync(RunA, Ct);
        Assert.True(engine.IsOpen(RunA));

        first.Dispose();
        await routes.CloseIfUnusedAsync(RunA, Ct);
        Assert.True(engine.IsOpen(RunA));

        second.Dispose();
        Assert.True(engine.IsOpen(RunA));
        await routes.CloseIfUnusedAsync(RunA, Ct);
        Assert.False(engine.IsOpen(RunA));
    }

    [Fact]
    public async Task Giving_a_count_back_twice_gives_it_back_once()
    {
        var engine = new StubEngine();
        var routes = Routes(engine);
        var first = await routes.OpenForProcessAsync(RunA, Ct);
        using var second = await routes.OpenForProcessAsync(RunA, Ct);

        first.Dispose();
        first.Dispose();
        await routes.CloseIfUnusedAsync(RunA, Ct);

        Assert.True(engine.IsOpen(RunA));
    }

    [Fact]
    public async Task A_process_is_counted_before_its_route_is_opened_so_a_proxied_launch_at_the_same_moment_leaves_it()
    {
        var engine = new StubEngine();
        var routes = Routes(engine);
        var opening = new TaskCompletionSource();
        var proceed = new TaskCompletionSource();
        engine.OnOpen = async _ =>
        {
            opening.TrySetResult();
            await proceed.Task;
        };

        var launch = routes.OpenForProcessAsync(RunA, Ct);
        await opening.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var proxied = routes.CloseIfUnusedAsync(RunA, Ct);
        await Task.Delay(50, Ct);
        Assert.False(proxied.IsCompleted);
        proceed.SetResult();
        using var hold = await launch;
        await proxied;

        Assert.True(engine.IsOpen(RunA));
        Assert.DoesNotContain("close", string.Join(' ', engine.Calls));
    }

    [Fact]
    public async Task A_route_that_fails_to_open_gives_its_count_back()
    {
        var engine = new StubEngine { OpenFails = new IOException("engine down") };
        var routes = Routes(engine);

        await Assert.ThrowsAsync<IOException>(() => routes.OpenForProcessAsync(RunA, Ct));
        engine.OpenFails = null;
        await routes.OpenForProcessAsync(RunB, Ct);
        engine.OnOpen = null;
        // Nothing keeps RunA's route: had the failed launch kept its count, a later close would leave it.
        await engine.OpenAsync(RunA, Ct);
        await routes.CloseIfUnusedAsync(RunA, Ct);

        Assert.False(engine.IsOpen(RunA));
    }

    [Fact]
    public async Task A_proxied_launch_in_a_run_that_never_had_the_route_asks_nothing_more_of_the_engine()
    {
        var engine = new StubEngine();
        var routes = Routes(engine);

        await routes.CloseIfUnusedAsync(RunA, Ct);

        Assert.Empty(engine.Calls);
    }

    [Fact]
    public async Task One_runs_process_does_not_keep_another_runs_route()
    {
        var engine = new StubEngine();
        var routes = Routes(engine);
        using var live = await routes.OpenForProcessAsync(RunA, Ct);
        await engine.OpenAsync(RunB, Ct);

        await routes.CloseIfUnusedAsync(RunB, Ct);

        Assert.False(engine.IsOpen(RunB));
        Assert.True(engine.IsOpen(RunA));
    }

    [Fact]
    public async Task The_chat_route_is_shared_so_one_chats_process_keeps_it_for_a_proxied_launch_of_another()
    {
        var engine = new StubEngine();
        var routes = Routes(engine);
        var live = await routes.OpenForProcessAsync(ChatOne, Ct);

        await routes.CloseIfUnusedAsync(ChatTwo, Ct);
        Assert.True(engine.IsOpen(ChatTwo));

        live.Dispose();
        await routes.CloseIfUnusedAsync(ChatTwo, Ct);
        Assert.False(engine.IsOpen(ChatOne));
    }

    [Fact]
    public async Task A_runs_process_does_not_keep_the_chat_route()
    {
        var engine = new StubEngine();
        var routes = Routes(engine);
        using var live = await routes.OpenForProcessAsync(RunA, Ct);
        await engine.OpenAsync(ChatOne, Ct);

        await routes.CloseIfUnusedAsync(ChatOne, Ct);

        Assert.False(engine.IsOpen(ChatOne));
    }

    [Fact]
    public async Task The_chat_route_is_given_to_a_chat_container_that_is_made_first_and_a_runs_is_not()
    {
        var engine = new StubEngine();
        var ensured = 0;
        var routes = Routes(engine, () =>
        {
            Interlocked.Increment(ref ensured);
            Assert.DoesNotContain("open chat", engine.Calls);
        });

        using var run = await routes.OpenForProcessAsync(RunA, Ct);
        Assert.Equal(0, ensured);
        using var chat = await routes.OpenForProcessAsync(ChatOne, Ct);

        Assert.Equal(1, ensured);
        Assert.Contains("open chat", engine.Calls);
    }

    [Fact]
    public async Task Opening_and_closing_a_route_are_one_at_a_time_and_every_chat_shares_one_gate()
    {
        var engine = new StubEngine();
        var routes = Routes(engine);
        await engine.OpenAsync(RunA, Ct);
        await engine.OpenAsync(ChatOne, Ct);
        engine.Calls.Clear();

        var work = new List<Task>();
        for (var i = 0; i < 20; i++)
        {
            work.Add(routes.CloseIfUnusedAsync(i % 2 == 0 ? ChatOne : ChatTwo, Ct));
            work.Add(Task.Run(async () => (await routes.OpenForProcessAsync(i % 2 == 0 ? ChatTwo : ChatOne, Ct)).Dispose(), Ct));
        }
        await Task.WhenAll(work);

        Assert.Equal(1, engine.MostInFlight);
    }

    [Fact]
    public async Task A_chat_route_is_left_untested_while_a_chat_process_or_an_open_route_makes_it_no_escape_and_tested_otherwise()
    {
        var engine = new StubEngine();
        var routes = Routes(engine);
        var worked = 0;
        Task<bool?> Work(CancellationToken _)
        {
            worked++;
            return Task.FromResult<bool?>(false);
        }

        Assert.False(await routes.WhileChatRouteClosedAsync(ChatOne, Work, Ct));
        Assert.Equal(1, worked);

        await engine.OpenAsync(ChatOne, Ct);
        Assert.Null(await routes.WhileChatRouteClosedAsync(ChatOne, Work, Ct));

        await engine.CloseAsync(ChatOne, Ct);
        using var live = await routes.OpenForProcessAsync(ChatTwo, Ct);
        await engine.CloseAsync(ChatOne, Ct);
        Assert.Null(await routes.WhileChatRouteClosedAsync(ChatOne, Work, Ct));
        Assert.Equal(1, worked);
    }

    [Fact]
    public async Task The_routes_listed_are_the_engines()
    {
        var engine = new StubEngine();
        var routes = Routes(engine);
        using var run = await routes.OpenForProcessAsync(RunA, Ct);

        Assert.Equal([new DirectRoute(RunA.Id, Chat: false)], await routes.ListAsync(Ct));
    }
}
