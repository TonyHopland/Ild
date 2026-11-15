using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Isolated;
using ILD.Tests.Fakes;

namespace ILD.Tests;

/// <summary>What counts as a leftover and how one is deleted, for either engine, on an engine kept in memory. What an engine finds and deletes is in its own suite.</summary>
public class RunSweepPolicyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IReadOnlySet<Guid> None = new HashSet<Guid>();

    private static string Id(Guid run) => run.ToString("N");

    private static InMemoryRunHost HostFinding(string[] runIds, params Guid[] retained)
        => new() { Facts = new SweepFacts(runIds, [], []), RetainedRuns = _ => Task.FromResult<IReadOnlySet<Guid>>(retained.ToHashSet()) };

    [Fact]
    public async Task A_run_that_is_neither_live_nor_retained_is_a_leftover_and_the_others_are_not()
    {
        var live = Guid.NewGuid();
        var retained = Guid.NewGuid();
        var ended = Guid.NewGuid();
        var host = HostFinding([Id(live), Id(retained), Id(ended)], retained);

        var leftovers = await host.ListLeftoversAsync(new HashSet<Guid> { live }, Ct);

        var leftover = Assert.Single(leftovers);
        Assert.Equal(new RunLeftover("in-memory", Id(ended)) { Target = new LeftoverTarget.Run(ended) }, leftover);
    }

    [Fact]
    public async Task A_run_found_in_several_objects_is_one_leftover()
    {
        var run = Guid.NewGuid();

        var leftovers = await HostFinding([Id(run), Id(run), Id(run)]).ListLeftoversAsync(None, Ct);

        Assert.Single(leftovers);
    }

    [Fact]
    public async Task A_run_label_that_is_not_an_id_is_a_leftover_that_nothing_deletes()
    {
        var host = HostFinding(["not-an-id"]);
        var live = new HashSet<Guid> { Guid.NewGuid() };

        var leftover = Assert.Single(await host.ListLeftoversAsync(live, Ct));

        Assert.Equal("not-an-id", leftover.Location);
        Assert.Null(leftover.Target);
        Assert.False(await host.DeleteLeftoverAsync(leftover, Ct));
        Assert.Empty(host.RunsDeleted);
    }

    [Fact]
    public async Task A_sign_in_or_installer_ILD_has_open_is_not_a_leftover_and_one_it_has_not_is()
    {
        var host = new InMemoryRunHost();
        await using var terminal = await host.OpenSignInAsync(agent: null, "claude", [], 80, 24, Ct);
        var open = Assert.Single(host.SignInsStarted);
        var forgottenSignIn = RunNames.SignIn(Guid.NewGuid());
        var forgottenInstaller = RunNames.Installer(Guid.NewGuid());
        host.Facts = new SweepFacts([], [
            new SideEnvironmentFact(open, RunPaths.SignInRole),
            new SideEnvironmentFact(forgottenSignIn, RunPaths.SignInRole),
            new SideEnvironmentFact(forgottenInstaller, RunPaths.ToolsRole),
            new SideEnvironmentFact(forgottenInstaller, RunPaths.ToolsRole),
            new SideEnvironmentFact(RunNames.Chat("main"), RunPaths.ChatRole),
            new SideEnvironmentFact("unlabelled", null),
        ], []);

        var leftovers = await host.ListLeftoversAsync(None, Ct);

        Assert.Equal(
            [new LeftoverTarget.SideEnvironment(forgottenSignIn, false), new LeftoverTarget.SideEnvironment(forgottenInstaller, true)],
            leftovers.Select(l => l.Target));
    }

    [Fact]
    public async Task An_open_route_is_a_leftover_only_when_nothing_uses_it()
    {
        var host = new InMemoryRunHost { Facts = new SweepFacts([], [], [new OpenRouteFact("in-use", false), new OpenRouteFact("idle", true)]) };

        var leftover = Assert.Single(await host.ListLeftoversAsync(None, Ct));

        Assert.Equal(new LeftoverTarget.OpenRoute("idle"), leftover.Target);
    }

    [Fact]
    public async Task Deleting_a_leftover_hands_the_engine_what_it_is()
    {
        var run = Guid.NewGuid();
        var host = new InMemoryRunHost();

        Assert.True(await host.DeleteLeftoverAsync(new RunLeftover("in-memory", Id(run)) { Target = new LeftoverTarget.Run(run) }, Ct));
        Assert.True(await host.DeleteLeftoverAsync(new RunLeftover("in-memory", "a") { Target = new LeftoverTarget.SideEnvironment("a", IsInstaller: true) }, Ct));
        Assert.True(await host.DeleteLeftoverAsync(new RunLeftover("in-memory", "b") { Target = new LeftoverTarget.OpenRoute("b") }, Ct));

        Assert.Equal([run], host.RunsDeleted);
        Assert.Equal([("a", true)], host.SideEnvironmentsDeleted);
        Assert.Equal(["b"], host.OpenRoutesDeleted);
    }

    [Fact]
    public async Task A_leftover_given_by_its_location_alone_is_read_once_by_the_policy()
    {
        var run = Guid.NewGuid();
        var signIn = RunNames.SignIn(Guid.NewGuid());
        var installer = RunNames.Installer(Guid.NewGuid());
        var host = new InMemoryRunHost { OpenRouteNames = { "route" } };

        foreach (var location in new[] { Id(run), signIn, installer, "route" })
            Assert.True(await host.DeleteLeftoverAsync(new RunLeftover("in-memory", location), Ct));
        Assert.False(await host.DeleteLeftoverAsync(new RunLeftover("in-memory", "nothing of ours"), Ct));

        Assert.Equal([run], host.RunsDeleted);
        Assert.Equal([(signIn, false), (installer, true)], host.SideEnvironmentsDeleted);
        Assert.Equal(["route"], host.OpenRoutesDeleted);
    }

    [Fact]
    public async Task A_leftover_of_another_kind_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new InMemoryRunHost().DeleteLeftoverAsync(new RunLeftover("local", "x"), Ct));
    }

    [Fact]
    public async Task A_run_whose_environment_is_still_there_is_not_deleted()
    {
        var run = Guid.NewGuid();
        var host = new InMemoryRunHost { RunStays = true };

        Assert.False(await host.DeleteLeftoverAsync(new RunLeftover("in-memory", Id(run)), Ct));
    }

    [Fact]
    public async Task An_engine_failure_while_deleting_or_listing_is_survived_and_tried_again()
    {
        var host = new InMemoryRunHost { EngineFails = true };

        Assert.False(await host.DeleteLeftoverAsync(new RunLeftover("in-memory", Id(Guid.NewGuid())), Ct));
        Assert.Empty(await host.ListLeftoversAsync(None, Ct));
    }

    [Fact]
    public void A_chat_is_current_only_when_it_carries_the_stamp_of_what_would_be_made_now()
    {
        var stamp = ChatSpecStamp.Of("image-1\nsettings");

        Assert.True(ChatSpecStamp.IsCurrent(stamp, ChatSpecStamp.Of("image-1\nsettings")));
        Assert.False(ChatSpecStamp.IsCurrent(stamp, ChatSpecStamp.Of("image-2\nsettings")));
        Assert.False(ChatSpecStamp.IsCurrent(null, stamp));
    }

    [Fact]
    public void The_stamp_is_the_first_eight_bytes_of_the_sha256_in_lower_case_hex()
    {
        Assert.Equal("ba7816bf8f01cfea", ChatSpecStamp.Of("abc"));
        Assert.Equal("ild/spec", ChatSpecStamp.Key);
    }

    [Fact]
    public void Names_and_labels_are_what_objects_made_before_an_upgrade_already_carry()
    {
        var run = Guid.Parse("0123456789abcdef0123456789abcdef");

        Assert.Equal("ild-run-0123456789abcdef0123456789abcdef", RunNames.Run(run));
        Assert.Equal("ild-tools-0123456789abcdef0123456789abcdef", RunNames.Installer(run));
        Assert.Equal("ild-signin-0123456789abcdef0123456789abcdef", RunNames.SignIn(run));
        Assert.Equal("ild-chat-main", RunNames.Chat("main"));
        Assert.Equal("ild-chat-main-open", RunNames.OpenPolicy(RunNames.Chat("main")));
        Assert.Equal("ild-open-main", RunNames.SharedOpenNetwork("main"));
        Assert.Equal("ild-open-main-chat", RunNames.OpenNetwork("main", "chat"));

        Assert.Equal(
            new Dictionary<string, string> { ["app.kubernetes.io/managed-by"] = "ild", ["ild/instance"] = "main", ["ild/run"] = "0123456789abcdef0123456789abcdef" },
            RunNames.Labels("main", runId: run));
        Assert.Equal(
            new Dictionary<string, string> { ["app.kubernetes.io/managed-by"] = "ild", ["ild/instance"] = "main", ["ild/role"] = "chat" },
            RunNames.Labels("main", RunPaths.ChatRole));
        Assert.Equal(
            new Dictionary<string, string> { ["app.kubernetes.io/managed-by"] = "ild", ["ild/instance"] = "main", ["ild/role"] = "signin" },
            RunNames.Labels("main", RunPaths.SignInRole));
        Assert.Equal(
            new Dictionary<string, string> { ["app.kubernetes.io/managed-by"] = "ild", ["ild/role"] = "tools" },
            RunNames.Labels(null, RunPaths.ToolsRole));
        Assert.Equal("open", RunPaths.OpenRole);
        Assert.Equal("ild/open-owner", RunPaths.OpenOwnerLabel);
    }

    [Fact]
    public void A_run_name_is_parsed_back_and_nothing_else_is_taken_for_one()
    {
        var run = Guid.NewGuid();

        Assert.True(RunNames.TryParseRun(RunNames.Run(run), out var parsed));
        Assert.Equal(run, parsed);
        Assert.False(RunNames.TryParseRun("ild-chat-main", out _));
        Assert.False(RunNames.TryParseRun("ild-run-nope", out _));
        Assert.False(RunNames.TryParseRun(null, out _));
    }
}
