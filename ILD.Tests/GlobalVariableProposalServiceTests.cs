using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Stores;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ILD.Tests;

/// <summary>The rules of suggesting a global variable change, against in-memory SQLite and with no HTTP host.</summary>
public sealed class GlobalVariableProposalServiceTests : IDisposable
{
    private readonly AgentSessionWorld _world = new();
    private readonly Mock<ISchedulerSettingsService> _settings = new();
    private readonly Mock<IWorkItemNotifier> _notifier = new();
    private readonly Mock<IChatNotifier> _chatNotifier = new();
    private readonly GlobalVariableProposalService _service;

    public GlobalVariableProposalServiceTests()
    {
        _settings.Setup(s => s.GetGlobalVariableMaxLengthAsync(It.IsAny<CancellationToken>())).ReturnsAsync(4000);
        _service = new GlobalVariableProposalService(
            new GlobalVariableProposalStore(_world.Db.Context), _settings.Object, _world.Resolver, _notifier.Object, _chatNotifier.Object);
    }

    public void Dispose() => _world.Dispose();

    private async Task<GlobalVariableProposalResult> SuggestAsync(AgentGlobalVariableProposalRequest request, AgentProposer? proposer = null)
        => await _service.ProposeAsync(request, proposer ?? new AgentProposer((await _world.SeedRunAsync()).RunId, null, null), TestContext.Current.CancellationToken);

    public static TheoryData<string, AgentGlobalVariableProposalRequest> InvalidSuggestions => new()
    {
        { "no name", new() { Value = "x", Rationale = "r" } },
        { "blank name", new() { Name = "   ", Value = "x", Rationale = "r" } },
        { "name starting with a digit", new() { Name = "9lives", Value = "x", Rationale = "r" } },
        { "name with a hyphen", new() { Name = "bad-name", Value = "x", Rationale = "r" } },
        { "name with a dot", new() { Name = "Global.rules", Value = "x", Rationale = "r" } },
        { "name over 128 characters", new() { Name = "a" + new string('b', 128), Value = "x", Rationale = "r" } },
        { "no value", new() { Name = "rules", Rationale = "r" } },
        { "no rationale", new() { Name = "rules", Value = "x" } },
        { "blank rationale", new() { Name = "rules", Value = "x", Rationale = "  " } },
        { "rationale of NUL characters alone", new() { Name = "rules", Value = "x", Rationale = "\0\0" } },
    };

    [Theory]
    [MemberData(nameof(InvalidSuggestions))]
    public async Task An_invalid_suggestion_is_refused_and_stores_and_announces_nothing(string _, AgentGlobalVariableProposalRequest request)
    {
        var result = await SuggestAsync(request);

        Assert.Equal(GlobalVariableProposalOutcome.Invalid, result.Outcome);
        Assert.NotEmpty(result.Error!);
        Assert.Empty(await _world.Db.Context.GlobalVariableProposals.ToListAsync(TestContext.Current.CancellationToken));
        _notifier.VerifyNoOtherCalls();
        _chatNotifier.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(4000, 4812, "The value is 4,812 / 4,000 characters; consolidate it and suggest it again.")]
    [InlineData(1000, 1001, "The value is 1,001 / 1,000 characters; consolidate it and suggest it again.")]
    public async Task A_value_over_the_length_setting_is_refused_stating_its_length_and_the_limit(int limit, int length, string error)
    {
        _settings.Setup(s => s.GetGlobalVariableMaxLengthAsync(It.IsAny<CancellationToken>())).ReturnsAsync(limit);

        var refused = await SuggestAsync(new() { Name = "rules", Value = new string('v', length), Rationale = "r" });
        var accepted = await SuggestAsync(new() { Name = "rules", Value = new string('v', limit), Rationale = "r" });

        Assert.Equal(error, refused.Error);
        Assert.Equal(GlobalVariableProposalOutcome.Created, accepted.Outcome);
    }

    [Fact]
    public async Task The_value_is_checked_as_it_will_be_stored_without_NUL_characters()
    {
        _settings.Setup(s => s.GetGlobalVariableMaxLengthAsync(It.IsAny<CancellationToken>())).ReturnsAsync(5);

        var result = await SuggestAsync(new() { Name = "rules", Value = "ab\0cd\0e", Rationale = " why\0 " });

        Assert.Equal("abcde", result.Proposal!.Value);
        Assert.Equal("why", result.Proposal.Rationale);
    }

    [Fact]
    public async Task A_run_suggestion_is_stamped_with_the_run_and_its_step_and_announced_to_the_board_alone()
    {
        var (runId, nodeId) = await _world.SeedRunAsync(stepRunning: true);

        var result = await SuggestAsync(new() { Name = " rules ", Value = "x", Rationale = "r" }, new AgentProposer(runId, null, null));

        var proposal = result.Proposal!;
        Assert.Equal("rules", proposal.Name);
        Assert.Equal(runId, proposal.CreatedByLoopRunId);
        Assert.Equal(nodeId, proposal.CreatedByRunNodeId);
        Assert.Null(proposal.CreatedByChatSessionId);
        Assert.Null(proposal.ChatReplySequence);
        _notifier.Verify(n => n.GlobalVariableProposalsChangedAsync("rules"), Times.Once);
        _chatNotifier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_chat_suggestion_is_stamped_with_the_reply_it_follows_and_announced_to_the_chat_too()
    {
        var chatId = await _world.SeedChatAsync("owner", "user", "assistant", "user");

        var result = await SuggestAsync(new() { Name = "rules", Value = "x", Rationale = "r" }, new AgentProposer(null, chatId, null));

        Assert.Equal(chatId, result.Proposal!.CreatedByChatSessionId);
        Assert.Equal(3, result.Proposal.ChatReplySequence);
        Assert.Null(result.Proposal.CreatedByRunNodeId);
        _notifier.Verify(n => n.GlobalVariableProposalsChangedAsync("rules"), Times.Once);
        _chatNotifier.Verify(n => n.GlobalVariableProposalsChangedAsync(chatId), Times.Once);
    }

    [Fact]
    public async Task A_grant_suggestion_is_stamped_with_the_grant()
    {
        var grantId = await _world.SeedGrantAsync();

        var result = await SuggestAsync(new() { Name = "rules", Value = "", Rationale = "r" }, new AgentProposer(null, null, grantId));

        Assert.Equal(grantId, result.Proposal!.CreatedByExternalGrantId);
        Assert.Equal("", result.Proposal.Value);
    }
}
