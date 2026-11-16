using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Entities;

namespace ILD.Tests;

/// <summary>Who is calling the agent API, what it owns and what it may reach, with no HTTP host.</summary>
public sealed class AgentSessionResolverTests : IDisposable
{
    private readonly AgentSessionWorld _world = new();

    public void Dispose() => _world.Dispose();

    // -- The caller ---------------------------------------------------------------

    [Fact]
    public void A_token_names_only_the_session_of_its_own_kind()
    {
        var id = Guid.NewGuid();

        Assert.Equal(id, AgentSessionWorld.Run(id).RunId);
        Assert.Null(AgentSessionWorld.Run(id).ChatSessionId);
        Assert.Null(AgentSessionWorld.Run(id).GrantId);
        Assert.Equal(id, AgentSessionWorld.Chat(id).ChatSessionId);
        Assert.Null(AgentSessionWorld.Chat(id).RunId);
        Assert.Equal(id, AgentSessionWorld.Grant(id).GrantId);
        Assert.Null(AgentSessionWorld.Grant(id).RunId);
    }

    [Fact]
    public void A_user_has_no_session_whatever_its_claims_say()
    {
        var user = new AgentCaller(false, false, "run", Guid.NewGuid(), null, "tester");

        Assert.Null(user.RunId);
        Assert.Null(user.SessionClaim(AgentTokenSource.Run));
    }

    [Theory]
    [InlineData(ExternalAgentGrantScope.Control, true)]
    [InlineData(ExternalAgentGrantScope.Full, false)]
    [InlineData(ExternalAgentGrantScope.ReadOnly, false)]
    public void Only_a_grant_at_the_control_level_controls_work(ExternalAgentGrantScope scope, bool expected)
    {
        Assert.Equal(expected, AgentSessionWorld.Grant(Guid.NewGuid(), scope).HasControlGrant);
        Assert.False(new AgentCaller(true, false, null, null, nameof(ExternalAgentGrantScope.Control), null).HasControlGrant);
    }

    // -- Who a change is for ----------------------------------------------------------

    [Fact]
    public async Task A_run_acts_as_the_instance_naming_its_running_step_and_its_work_item()
    {
        var (runId, _) = await _world.SeedRunAsync("WI-7", stepRunning: true);

        var actor = await _world.Resolver.ActorAsync(AgentSessionWorld.Run(runId));

        Assert.Equal(WorkItemActor.Agent(TestActors.Instance.Username, runId, "review", "WI-7"), actor);
    }

    [Fact]
    public async Task A_chat_acts_as_the_user_it_belongs_to()
    {
        var chatId = await _world.SeedChatAsync("alice");

        Assert.Equal(WorkItemActor.Chat("alice", chatId), await _world.Resolver.ActorAsync(AgentSessionWorld.Chat(chatId)));
    }

    [Fact]
    public async Task A_grant_acts_as_the_instance_named_by_the_grant()
    {
        var grantId = await _world.SeedGrantAsync("outside-agent");

        Assert.Equal(
            WorkItemActor.External(TestActors.Instance.Username, "outside-agent", grantId),
            await _world.Resolver.ActorAsync(AgentSessionWorld.Grant(grantId)));
    }

    [Fact]
    public async Task A_person_acts_as_themselves_and_an_agent_with_no_session_as_the_system()
    {
        Assert.Equal(WorkItemActor.Human("tester"), await _world.Resolver.ActorAsync(AgentSessionWorld.Human()));
        Assert.Equal(
            WorkItemActor.System(TestActors.Instance.Username, "agent with no run or chat"),
            await _world.Resolver.ActorAsync(AgentSessionWorld.AgentWithoutSession()));
    }

    [Fact]
    public async Task A_user_who_can_no_longer_be_resolved_has_no_actor()
    {
        Assert.Null(await _world.Resolver.ActorAsync(AgentSessionWorld.Human("")));
    }

    [Fact]
    public async Task A_session_that_is_gone_falls_back_to_what_the_token_is()
    {
        Assert.Equal(
            WorkItemActor.System(TestActors.Instance.Username, "agent with no run or chat"),
            await _world.Resolver.ActorAsync(AgentSessionWorld.Run(Guid.NewGuid())));
        Assert.Null(await _world.Resolver.ActorAsync(AgentSessionWorld.Grant(Guid.NewGuid())));
    }

    // -- Ownership --------------------------------------------------------------------

    [Fact]
    public void A_session_owns_only_what_it_created()
    {
        var run = Guid.NewGuid();
        var chat = Guid.NewGuid();
        var grant = Guid.NewGuid();

        Assert.True(_world.Resolver.Owns(AgentSessionWorld.Run(run), _world.WorkItem("A", run: run)));
        Assert.False(_world.Resolver.Owns(AgentSessionWorld.Run(run), _world.WorkItem("B", run: Guid.NewGuid())));
        Assert.False(_world.Resolver.Owns(AgentSessionWorld.Run(run), _world.WorkItem("C", chat: run)));
        Assert.True(_world.Resolver.Owns(AgentSessionWorld.Chat(chat), _world.WorkItem("D", chat: chat)));
        Assert.False(_world.Resolver.Owns(AgentSessionWorld.Chat(chat), _world.WorkItem("E", run: chat)));
        Assert.True(_world.Resolver.Owns(AgentSessionWorld.Grant(grant), _world.WorkItem("F", grant: grant)));
        Assert.False(_world.Resolver.Owns(AgentSessionWorld.Grant(grant), _world.WorkItem("G")));
    }

    [Fact]
    public void A_caller_with_no_session_owns_nothing()
    {
        Assert.False(_world.Resolver.Owns(AgentSessionWorld.Human(), _world.WorkItem("A")));
        Assert.False(_world.Resolver.Owns(AgentSessionWorld.AgentWithoutSession(), _world.WorkItem("B", run: Guid.NewGuid())));
    }

    // -- Reach ------------------------------------------------------------------------

    [Fact]
    public async Task A_run_reaches_its_own_work_item_and_the_items_it_created_and_no_other()
    {
        var (runId, _) = await _world.SeedRunAsync("WI-1");
        var caller = AgentSessionWorld.Run(runId);
        _world.WorkItem("WI-1");
        _world.WorkItem("WI-made", run: runId);
        _world.WorkItem("WI-other");

        Assert.Null(await _world.Resolver.ReachRefusalAsync(caller, "WI-1"));
        Assert.Null(await _world.Resolver.ReachRefusalAsync(caller, "WI-made"));
        Assert.Equal("This work item belongs to another run.", await _world.Resolver.ReachRefusalAsync(caller, "WI-other"));
    }

    [Fact]
    public async Task A_run_whose_run_is_gone_reaches_only_what_it_created()
    {
        var caller = AgentSessionWorld.Run(Guid.NewGuid());
        _world.WorkItem("WI-1");

        Assert.Equal("This work item belongs to another run.", await _world.Resolver.ReachRefusalAsync(caller, "WI-1"));
    }

    [Fact]
    public async Task A_grant_reaches_only_the_items_it_created()
    {
        var grant = Guid.NewGuid();
        _world.WorkItem("WI-made", grant: grant);
        _world.WorkItem("WI-other", grant: Guid.NewGuid());
        _world.WorkItem("WI-pre-existing");

        Assert.Null(await _world.Resolver.ReachRefusalAsync(AgentSessionWorld.Grant(grant), "WI-made"));
        Assert.NotNull(await _world.Resolver.ReachRefusalAsync(AgentSessionWorld.Grant(grant), "WI-other"));
        Assert.Equal(
            "A grant reaches only the work items it created for this route.",
            await _world.Resolver.ReachRefusalAsync(AgentSessionWorld.Grant(grant), "WI-pre-existing"));
    }

    [Fact]
    public async Task A_chat_and_a_user_reach_any_item_and_an_unknown_item_passes_for_the_route_to_answer_404()
    {
        _world.WorkItem("WI-1");
        var (runId, _) = await _world.SeedRunAsync("WI-2");

        Assert.Null(await _world.Resolver.ReachRefusalAsync(AgentSessionWorld.Chat(Guid.NewGuid()), "WI-1"));
        Assert.Null(await _world.Resolver.ReachRefusalAsync(AgentSessionWorld.Human(), "WI-1"));
        Assert.Null(await _world.Resolver.ReachRefusalAsync(AgentSessionWorld.Run(runId), "WI-unknown"));
        Assert.Null(await _world.Resolver.ReachRefusalAsync(AgentSessionWorld.Grant(Guid.NewGuid()), "WI-unknown"));
    }

    [Fact]
    public async Task A_run_is_told_apart_from_other_runs_on_the_same_work_item()
    {
        var (runId, _) = await _world.SeedRunAsync("WI-1");

        Assert.True(await _world.Resolver.RunWorksOnAsync(runId, "WI-1"));
        Assert.False(await _world.Resolver.RunWorksOnAsync(runId, "WI-2"));
        Assert.True(await _world.Resolver.RunExistsAsync(runId));
        Assert.False(await _world.Resolver.RunExistsAsync(Guid.NewGuid()));
        Assert.False(await _world.Resolver.ChatSessionExistsAsync(Guid.NewGuid()));
        Assert.True(await _world.Resolver.ChatSessionExistsAsync(await _world.SeedChatAsync()));
    }

    // -- The proposing session --------------------------------------------------------

    [Fact]
    public async Task A_proposer_is_the_session_its_token_names_when_that_exists()
    {
        var (runId, _) = await _world.SeedRunAsync();
        var chatId = await _world.SeedChatAsync();
        var grantId = await _world.SeedGrantAsync();

        Assert.Equal(new AgentProposer(runId, null, null), (await _world.Resolver.ResolveProposerAsync(AgentSessionWorld.Run(runId))).Proposer);
        Assert.Equal(new AgentProposer(null, chatId, null), (await _world.Resolver.ResolveProposerAsync(AgentSessionWorld.Chat(chatId))).Proposer);
        Assert.Equal(new AgentProposer(null, null, grantId), (await _world.Resolver.ResolveProposerAsync(AgentSessionWorld.Grant(grantId))).Proposer);
    }

    [Theory]
    [InlineData("run", "Unknown loop run.")]
    [InlineData("chat", "Unknown or inactive chat session.")]
    [InlineData("grant", "Unknown external-agent grant.")]
    public async Task A_proposer_whose_session_does_not_exist_is_refused_naming_which(string source, string error)
    {
        var id = Guid.NewGuid();
        var caller = source switch
        {
            "run" => AgentSessionWorld.Run(id),
            "chat" => AgentSessionWorld.Chat(id),
            _ => AgentSessionWorld.Grant(id),
        };

        var resolution = await _world.Resolver.ResolveProposerAsync(caller);

        Assert.Equal(ProposerOutcome.Unknown, resolution.Outcome);
        Assert.Null(resolution.Proposer);
        Assert.Equal(error, resolution.Error);
    }

    [Fact]
    public async Task A_caller_with_no_session_cannot_propose()
    {
        foreach (var caller in new[] { AgentSessionWorld.Human(), AgentSessionWorld.AgentWithoutSession() })
        {
            var resolution = await _world.Resolver.ResolveProposerAsync(caller);

            Assert.Equal(ProposerOutcome.NoSession, resolution.Outcome);
            Assert.StartsWith("A proposal needs the proposing session.", resolution.Error);
        }
    }

    // -- The stamp --------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_run_stamp_names_the_step_that_was_running_and_no_chat_reply(bool stepRunning)
    {
        var (runId, runningNodeId) = await _world.SeedRunAsync(stepRunning: stepRunning);
        await _world.SeedRunAsync(stepRunning: true);

        var stamp = await _world.Resolver.StampAsync(new AgentProposer(runId, null, null));

        Assert.Equal(new ProposalStamp(runId, null, null, runningNodeId, null), stamp);
    }

    public static TheoryData<string, string[], int?> ChatTranscripts => new()
    {
        { "an empty chat", Array.Empty<string>(), null },
        { "the first turn in flight", new[] { "user" }, 1 },
        { "after a finished turn", new[] { "user", "assistant" }, 1 },
        { "the second turn in flight", new[] { "user", "assistant", "user" }, 3 },
        { "after two finished turns", new[] { "user", "assistant", "user", "assistant" }, 3 },
    };

    [Theory]
    [MemberData(nameof(ChatTranscripts))]
    public async Task A_chat_stamp_names_the_reply_it_follows(string _, string[] roles, int? expected)
    {
        var chatId = await _world.SeedChatAsync("owner", roles);
        await _world.SeedChatAsync("owner", Enumerable.Repeat("assistant", 12).Prepend("user").ToArray());

        var stamp = await _world.Resolver.StampAsync(new AgentProposer(null, chatId, null));

        Assert.Equal(new ProposalStamp(null, chatId, null, null, expected), stamp);
    }

    [Fact]
    public async Task A_grant_stamp_names_only_the_grant()
    {
        var grantId = await _world.SeedGrantAsync();

        Assert.Equal(new ProposalStamp(null, null, grantId, null, null), await _world.Resolver.StampAsync(new AgentProposer(null, null, grantId)));
    }
}
