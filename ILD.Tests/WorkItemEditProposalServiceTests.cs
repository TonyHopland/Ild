using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using Moq;

namespace ILD.Tests;

/// <summary>What is checked before a Work Item Edit Proposal reaches the WorkItem server, with no HTTP host.</summary>
public sealed class WorkItemEditProposalServiceTests : IDisposable
{
    private readonly AgentSessionWorld _world = new();
    private readonly Mock<IChatNotifier> _chatNotifier = new();
    private readonly WorkItemEditProposalService _service;
    private RemoteCreateEditProposalRequest? _sent;

    public WorkItemEditProposalServiceTests()
    {
        _service = new WorkItemEditProposalService(_world.WorkItems.Object, _world.Resolver, _chatNotifier.Object);
        _world.WorkItems.Setup(m => m.GetWorkItemAsync("WI-1")).ReturnsAsync(new WorkItemView { Id = "WI-1" });
        _world.WorkItems.Setup(m => m.GetDependenciesAsync("WI-1")).ReturnsAsync([new WorkItemView { Id = "WI-3" }]);
        _world.WorkItems
            .Setup(m => m.ProposeEditAsync(It.IsAny<WorkItemActor>(), "WI-1", It.IsAny<RemoteCreateEditProposalRequest>(), It.IsAny<CancellationToken>()))
            .Callback((WorkItemActor _, string _, RemoteCreateEditProposalRequest sent, CancellationToken _) => _sent = sent)
            .ReturnsAsync(new EditProposalCreateResult(EditProposalCreateOutcome.Created, null, new RemoteWorkItemEditProposal { Id = Guid.NewGuid() }));
    }

    public void Dispose() => _world.Dispose();

    private async Task<WorkItemEditProposalResult> ProposeAsync(AgentWorkItemEditProposalRequest request)
    {
        var (runId, _) = await _world.SeedRunAsync();
        return await _service.ProposeAsync("WI-1", request, AgentSessionWorld.Run(runId), new AgentProposer(runId, null, null), TestContext.Current.CancellationToken);
    }

    private void VerifyNothingProposed() => _world.WorkItems.Verify(m => m.ProposeEditAsync(
        It.IsAny<WorkItemActor>(), It.IsAny<string>(), It.IsAny<RemoteCreateEditProposalRequest>(), It.IsAny<CancellationToken>()), Times.Never);

    public static TheoryData<string, AgentWorkItemEditProposalRequest, string> InvalidEdits => new()
    {
        { "nothing proposed", new(), "Propose at least one of title, description, tags, branchNameOverride, baseBranchOverride, addDependencies or removeDependencies." },
        { "only a rationale", new() { Rationale = "just because" }, "Propose at least one of" },
        { "empty title", new() { Title = "" }, "A proposed title cannot be blank." },
        { "blank title", new() { Title = "   " }, "A proposed title cannot be blank." },
        { "illegal branch name", new() { BranchNameOverride = "bad..name" }, "Branch name cannot contain '..'." },
        { "illegal base branch", new() { BaseBranchOverride = "has space" }, "Base branch cannot contain spaces." },
        { "blank addition", new() { Title = "ok", AddDependencies = ["  "] }, "A dependency id cannot be blank." },
        { "blank removal", new() { Title = "ok", RemoveDependencies = [""] }, "A dependency id cannot be blank." },
        { "addition repeated", new() { Title = "ok", AddDependencies = ["WI-2", "WI-2"] }, "Each dependency may appear only once" },
        { "removal repeated", new() { Title = "ok", RemoveDependencies = ["WI-3", "WI-3"] }, "Each dependency may appear only once" },
        { "in both lists", new() { Title = "ok", AddDependencies = ["WI-2"], RemoveDependencies = ["WI-2"] }, "Each dependency may appear only once" },
        { "removing what is not a dependency", new() { RemoveDependencies = ["WI-9"] }, "Work item WI-1 does not depend on WI-9, so there is nothing to remove." },
        { "adding what already is one", new() { AddDependencies = ["WI-3"] }, "Work item WI-1 already depends on WI-3." },
    };

    [Theory]
    [MemberData(nameof(InvalidEdits))]
    public async Task An_invalid_edit_proposal_is_refused_with_its_reason_and_sends_nothing(string _, AgentWorkItemEditProposalRequest request, string error)
    {
        var result = await ProposeAsync(request);

        Assert.Equal(WorkItemEditProposalOutcome.Invalid, result.Outcome);
        Assert.StartsWith(error, result.Error);
        VerifyNothingProposed();
    }

    [Fact]
    public async Task A_dependency_that_would_close_a_cycle_is_refused_with_the_servers_reason()
    {
        _world.WorkItems.Setup(m => m.CheckNewDependencyAsync("WI-1", "WI-2")).ReturnsAsync("That would make a cycle.");

        var result = await ProposeAsync(new() { AddDependencies = ["WI-2"] });

        Assert.Equal((WorkItemEditProposalOutcome.Invalid, "That would make a cycle."), (result.Outcome, result.Error));
        VerifyNothingProposed();
    }

    [Fact]
    public async Task A_dependency_change_on_an_unknown_item_is_not_found()
    {
        _world.WorkItems.Setup(m => m.GetWorkItemAsync("WI-1")).ReturnsAsync((WorkItemView?)null);

        var result = await ProposeAsync(new() { AddDependencies = ["WI-2"] });

        Assert.Equal(WorkItemEditProposalOutcome.NotFound, result.Outcome);
        VerifyNothingProposed();
    }

    [Fact]
    public async Task A_proposal_is_sent_with_trimmed_dependency_ids_and_the_stamp_of_the_run_and_its_step()
    {
        var (runId, nodeId) = await _world.SeedRunAsync(stepRunning: true);

        var result = await _service.ProposeAsync(
            "WI-1",
            new() { Title = "New", Tags = ["a"], AddDependencies = [" WI-2 "], RemoveDependencies = [" WI-3"], Rationale = "why" },
            AgentSessionWorld.Run(runId), new AgentProposer(runId, null, null), TestContext.Current.CancellationToken);

        Assert.Equal(WorkItemEditProposalOutcome.Created, result.Outcome);
        Assert.Equal(["WI-2"], _sent!.AddDependencies);
        Assert.Equal(["WI-3"], _sent.RemoveDependencies);
        Assert.Equal(("New", "why"), (_sent.Title, _sent.Rationale));
        Assert.Equal((runId, nodeId, null, null, null), (_sent.CreatedByLoopRunId, _sent.CreatedByRunNodeId, _sent.CreatedByChatSessionId, _sent.CreatedByExternalGrantId, _sent.ChatReplySequence));
        _chatNotifier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_chat_proposal_records_the_reply_it_follows_and_hints_the_chat()
    {
        var chatId = await _world.SeedChatAsync("owner", "user", "assistant", "user");

        var result = await _service.ProposeAsync(
            "WI-1", new() { Title = "New" }, AgentSessionWorld.Chat(chatId), new AgentProposer(null, chatId, null), TestContext.Current.CancellationToken);

        Assert.Equal(WorkItemEditProposalOutcome.Created, result.Outcome);
        Assert.Equal((chatId, 3, null), (_sent!.CreatedByChatSessionId, _sent.ChatReplySequence, _sent.CreatedByRunNodeId));
        _chatNotifier.Verify(n => n.EditProposalsChangedAsync(chatId), Times.Once);
    }

    [Fact]
    public async Task A_grant_proposal_is_stamped_with_the_grant()
    {
        var grantId = await _world.SeedGrantAsync();

        await _service.ProposeAsync("WI-1", new() { Title = "New" }, AgentSessionWorld.Grant(grantId), new AgentProposer(null, null, grantId), TestContext.Current.CancellationToken);

        Assert.Equal(grantId, _sent!.CreatedByExternalGrantId);
    }

    [Theory]
    [InlineData(EditProposalCreateOutcome.NotFound, WorkItemEditProposalOutcome.NotFound)]
    [InlineData(EditProposalCreateOutcome.TooManyPending, WorkItemEditProposalOutcome.TooManyPending)]
    [InlineData(EditProposalCreateOutcome.Invalid, WorkItemEditProposalOutcome.Invalid)]
    public async Task What_the_server_refuses_comes_back_with_its_reason_and_tells_no_chat(
        EditProposalCreateOutcome server, WorkItemEditProposalOutcome expected)
    {
        _world.WorkItems
            .Setup(m => m.ProposeEditAsync(It.IsAny<WorkItemActor>(), "WI-1", It.IsAny<RemoteCreateEditProposalRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EditProposalCreateResult(server, "the server says no", null));
        var chatId = await _world.SeedChatAsync();

        var result = await _service.ProposeAsync(
            "WI-1", new() { Title = "New" }, AgentSessionWorld.Chat(chatId), new AgentProposer(null, chatId, null), TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Outcome);
        if (expected != WorkItemEditProposalOutcome.NotFound)
            Assert.Equal("the server says no", result.Error);
        _chatNotifier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_caller_whose_user_cannot_be_resolved_is_unauthorized_and_sends_nothing()
    {
        var result = await _service.ProposeAsync(
            "WI-1", new() { Title = "New" }, AgentSessionWorld.Human(""), new AgentProposer(Guid.NewGuid(), null, null), TestContext.Current.CancellationToken);

        Assert.Equal(WorkItemEditProposalOutcome.Unauthorized, result.Outcome);
        VerifyNothingProposed();
    }

    [Fact]
    public async Task An_unreachable_WorkItem_server_surfaces_for_the_caller_to_map()
    {
        _world.WorkItems.Setup(m => m.GetDependenciesAsync("WI-1")).ThrowsAsync(new HttpRequestException("down"));

        await Assert.ThrowsAsync<HttpRequestException>(() => ProposeAsync(new() { AddDependencies = ["WI-2"] }));
    }
}
