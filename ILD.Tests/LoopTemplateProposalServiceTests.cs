using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Stores;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ILD.Tests;

/// <summary>The rules of proposing a loop template change, against in-memory SQLite and with no HTTP host.</summary>
public sealed class LoopTemplateProposalServiceTests : IDisposable
{
    private const string LatestDocument = """{"name":"one"}""";

    private readonly AgentSessionWorld _world = new();
    private readonly Mock<ILoopTemplateManager> _templates = new();
    private readonly Mock<IWorkItemNotifier> _notifier = new();
    private readonly Mock<IChatNotifier> _chatNotifier = new();
    private readonly LoopTemplateProposalService _service;
    private readonly LoopTemplateLookup _lookup;

    public LoopTemplateProposalServiceTests()
    {
        _lookup = new LoopTemplateLookup(_world.Db.Context);
        _templates.Setup(t => t.CheckProposedDocument(It.IsAny<string>()))
            .Returns((string document) => new ProposedLoopDocument(null, document, null, []));
        _service = new LoopTemplateProposalService(
            new LoopTemplateProposalStore(_world.Db.Context), _templates.Object, _lookup, _world.Resolver, _notifier.Object, _chatNotifier.Object);
    }

    public void Dispose() => _world.Dispose();

    private async Task<AgentProposer> RunProposerAsync() => new((await _world.SeedRunAsync()).RunId, null, null);

    private async Task<LoopTemplateProposalResult> ProposeAsync(AgentLoopTemplateProposalRequest request, AgentProposer? proposer = null)
        => await _service.ProposeAsync(request, proposer ?? await RunProposerAsync(), TestContext.Current.CancellationToken);

    private async Task<List<LoopTemplateProposal>> StoredAsync()
        => await _world.Db.Context.LoopTemplateProposals.ToListAsync(TestContext.Current.CancellationToken);

    private static AgentLoopTemplateProposalRequest Document(string name, int? baseVersion = 1, string document = "{}", string? rationale = "Because.")
        => new() { NameOrId = name, BaseVersion = baseVersion, Document = document, Rationale = rationale };

    // -- Validation ---------------------------------------------------------------

    public static TheoryData<string, AgentLoopTemplateProposalRequest> InvalidProposals => new()
    {
        { "no name", Document(" ") },
        { "base version zero", Document("Release Loop", baseVersion: 0) },
        { "negative base version", Document("Release Loop", baseVersion: -1) },
        { "no base version", Document("Release Loop", baseVersion: null) },
        { "blank rationale", Document("Release Loop", rationale: "  \t ") },
        { "no rationale", Document("Release Loop", rationale: null) },
        { "rationale over 2000", Document("Release Loop", rationale: new string('r', 2001)) },
        { "document over 1,000,000 characters", Document("Release Loop", document: new string('x', 1_000_001)) },
        { "neither a document nor edits", new() { NameOrId = "Release Loop", BaseVersion = 1, Rationale = "Because." } },
        { "a blank document", Document("Release Loop", document: "  ") },
        { "both a document and edits", new() { NameOrId = "Release Loop", BaseVersion = 1, Rationale = "Because.", Document = "{}", Edits = [new()] } },
        { "an empty edit list", new() { NameOrId = "Release Loop", BaseVersion = 1, Rationale = "Because.", Edits = [] } },
    };

    [Theory]
    [MemberData(nameof(InvalidProposals))]
    public async Task An_invalid_proposal_is_refused_with_a_reason_and_stores_and_announces_nothing(string _, AgentLoopTemplateProposalRequest request)
    {
        await _world.SeedTemplateAsync("Release Loop");

        var result = await ProposeAsync(request);

        Assert.Equal(LoopTemplateProposalOutcome.Invalid, result.Outcome);
        Assert.NotEmpty(result.Error!);
        Assert.Empty(await StoredAsync());
        _notifier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_document_over_the_size_limit_is_refused_stating_the_size_and_the_limit()
    {
        var result = await ProposeAsync(Document("Release Loop", document: new string('x', 1_000_001)));

        Assert.Equal("document is too large (1000001 chars); the limit is 1000000.", result.Error);
    }

    [Fact]
    public async Task A_rationale_is_checked_as_it_will_be_stored_without_NUL_characters()
    {
        var template = await _world.SeedTemplateAsync();

        var result = await ProposeAsync(Document(template.Id.ToString(), rationale: " why\0 "));

        Assert.Equal("why", result.Proposal!.Rationale);
        Assert.Equal(LoopTemplateProposalOutcome.Invalid, (await ProposeAsync(Document(template.Id.ToString(), rationale: "\0"))).Outcome);
    }

    // -- Finding the template -----------------------------------------------------

    [Fact]
    public async Task A_template_is_found_by_id_or_by_name_ignoring_case_and_surrounding_whitespace_archived_ones_included()
    {
        var live = await _world.SeedTemplateAsync("Release Loop");
        var archived = await _world.SeedTemplateAsync("Old Loop", archived: true);

        Assert.Equal(live.Id, (await _lookup.FindAsync($" {live.Id} ")).Template!.Id);
        Assert.Equal(live.Id, (await _lookup.FindAsync("  release LOOP ")).Template!.Id);
        Assert.Equal(archived.Id, (await _lookup.FindAsync("old loop")).Template!.Id);
    }

    [Fact]
    public async Task An_id_wins_over_a_name_that_only_looks_like_one()
    {
        var byId = await _world.SeedTemplateAsync("Plain");
        await _world.SeedTemplateAsync(byId.Id.ToString().ToUpperInvariant());

        Assert.Equal(byId.Id, (await _lookup.FindAsync(byId.Id.ToString())).Template!.Id);
    }

    [Fact]
    public async Task An_unknown_template_is_refused_naming_what_was_asked_for_and_stores_nothing()
    {
        await _world.SeedTemplateAsync("Release Loop");

        var result = await ProposeAsync(Document("  Nope  "));

        Assert.Equal(LoopTemplateProposalOutcome.TemplateNotFound, result.Outcome);
        Assert.Equal("Loop template not found: Nope", result.Error);
        Assert.Empty(await StoredAsync());
    }

    [Fact]
    public async Task A_name_more_than_one_template_has_is_refused_listing_the_candidates()
    {
        var first = await _world.SeedTemplateAsync("Twin");
        var second = await _world.SeedTemplateAsync(" twin ", archived: true);

        var result = await ProposeAsync(Document("TWIN"));

        Assert.Equal(LoopTemplateProposalOutcome.AmbiguousName, result.Outcome);
        Assert.Equal("2 loop templates are named 'TWIN'; pass the id of the one you mean.", result.Error);
        Assert.Equal(new[] { first.Id, second.Id }.Order(), result.Candidates!.Select(t => t.Id).Order());
        Assert.Empty(await StoredAsync());
    }

    // -- Proposing against the latest version -------------------------------------

    [Fact]
    public async Task A_document_against_the_latest_version_is_stored_pending_stamped_and_announced()
    {
        var template = await _world.SeedTemplateAsync();
        var (runId, nodeId) = await _world.SeedRunAsync(stepRunning: true);

        var result = await ProposeAsync(Document(template.Name, document: """{"name":"two"}"""), new AgentProposer(runId, null, null));

        var proposal = Assert.Single(await StoredAsync());
        Assert.Equal(LoopTemplateProposalOutcome.Created, result.Outcome);
        Assert.Equal(proposal.Id, result.Proposal!.Id);
        Assert.Equal("""{"name":"two"}""", proposal.Document);
        Assert.Null(proposal.Edits);
        Assert.Equal((template.Id, 1, runId, nodeId), (proposal.LoopTemplateId, proposal.BaseVersion, proposal.CreatedByLoopRunId, proposal.CreatedByRunNodeId));
        _notifier.Verify(n => n.LoopTemplateProposalsChangedAsync(template.Id), Times.Once);
        _chatNotifier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_chat_proposal_records_the_reply_it_follows_and_hints_the_chat()
    {
        var template = await _world.SeedTemplateAsync();
        var chatId = await _world.SeedChatAsync("owner", "user");

        var result = await ProposeAsync(Document(template.Id.ToString()), new AgentProposer(null, chatId, null));

        Assert.Equal((chatId, 1), (result.Proposal!.CreatedByChatSessionId, result.Proposal.ChatReplySequence));
        _chatNotifier.Verify(n => n.LoopTemplateProposalsChangedAsync(chatId), Times.Once);
    }

    [Fact]
    public async Task A_grant_proposal_is_stamped_with_the_grant()
    {
        var template = await _world.SeedTemplateAsync();
        var grantId = await _world.SeedGrantAsync();

        var result = await ProposeAsync(Document(template.Id.ToString()), new AgentProposer(null, null, grantId));

        Assert.Equal(grantId, result.Proposal!.CreatedByExternalGrantId);
    }

    [Fact]
    public async Task A_document_against_an_older_version_is_refused_naming_the_latest()
    {
        var template = await _world.SeedTemplateAsync("Release Loop");
        _world.Db.Context.LoopTemplateVersions.Add(new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 2 });
        await _world.Db.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await ProposeAsync(Document(template.Name));

        Assert.Equal(LoopTemplateProposalOutcome.NotLatest, result.Outcome);
        Assert.Equal(2, result.LatestVersion);
        Assert.Equal(
            "Version 1 is not the latest version of loop template 'Release Loop'; the latest is version 2. "
            + "Read it with get_loop_template, make your change to that document and propose again.",
            result.Error);
        Assert.Empty(await StoredAsync());
    }

    // -- A document a save would refuse -------------------------------------------

    [Fact]
    public async Task A_document_that_cannot_be_read_is_refused_with_the_reason()
    {
        await _world.SeedTemplateAsync("Release Loop");
        _templates.Setup(t => t.CheckProposedDocument("{}")).Returns(new ProposedLoopDocument(null, null, "Not a loop document.", []));

        var result = await ProposeAsync(Document("Release Loop"));

        Assert.Equal((LoopTemplateProposalOutcome.Invalid, "Not a loop document."), (result.Outcome, result.Error));
        Assert.Empty(await StoredAsync());
    }

    [Fact]
    public async Task A_graph_a_save_would_refuse_is_refused_listing_every_error()
    {
        await _world.SeedTemplateAsync("Release Loop");
        _templates.Setup(t => t.CheckProposedDocument("{}")).Returns(new ProposedLoopDocument(null, null, null, ["no start node", "dangling edge"]));

        var result = await ProposeAsync(Document("Release Loop"));

        Assert.Equal("A save would refuse this loop. Fix every error listed and propose again.", result.Error);
        Assert.Equal(["no start node", "dangling edge"], result.ValidationErrors);
        Assert.Empty(await StoredAsync());
    }

    // -- Edit Lists ---------------------------------------------------------------

    private static AgentLoopTemplateProposalRequest EditList(string name, int baseVersion, params LoopTemplateEdit[] edits)
        => new() { NameOrId = name, BaseVersion = baseVersion, Rationale = "Because.", Edits = [.. edits] };

    private static LoopTemplateEdit EditFile(string oldString, string newString)
        => new() { Op = "editFile", OldString = oldString, NewString = newString };

    private void LatestIs(int version) => _templates
        .Setup(t => t.GetLatestDocumentAsync(It.IsAny<LoopTemplate>())).ReturnsAsync((version, LatestDocument));

    [Fact]
    public async Task An_edit_list_is_applied_to_the_latest_version_and_stored_with_the_list_as_sent_without_null_members()
    {
        var template = await _world.SeedTemplateAsync();
        LatestIs(1);

        var result = await ProposeAsync(EditList(template.Name, 1, EditFile("one", "two")));

        var proposal = Assert.Single(await StoredAsync());
        Assert.Equal(LoopTemplateProposalOutcome.Created, result.Outcome);
        Assert.Contains("two", proposal.Document);
        Assert.Equal("""[{"op":"editFile","oldString":"one","newString":"two"}]""", proposal.Edits);
    }

    [Fact]
    public async Task An_edit_list_against_an_older_version_is_refused_naming_the_latest_and_never_applied()
    {
        var template = await _world.SeedTemplateAsync("Release Loop");
        LatestIs(3);

        var result = await ProposeAsync(EditList(template.Name, 2, EditFile("one", "two")));

        Assert.Equal((LoopTemplateProposalOutcome.NotLatest, 3), (result.Outcome, result.LatestVersion));
        Assert.Contains("Version 2 is not the latest version of loop template 'Release Loop'; the latest is version 3.", result.Error);
        _templates.Verify(t => t.CheckProposedDocument(It.IsAny<string>()), Times.Never);
        Assert.Empty(await StoredAsync());
    }

    [Fact]
    public async Task An_edit_list_for_a_template_with_no_saved_version_names_version_zero_as_the_latest()
    {
        var template = await _world.SeedTemplateAsync();
        _templates.Setup(t => t.GetLatestDocumentAsync(It.IsAny<LoopTemplate>())).ReturnsAsync(((int, string)?)null);

        var result = await ProposeAsync(EditList(template.Name, 1, EditFile("one", "two")));

        Assert.Equal((LoopTemplateProposalOutcome.NotLatest, 0), (result.Outcome, result.LatestVersion));
    }

    [Fact]
    public async Task A_failing_edit_is_refused_naming_its_index_and_stores_nothing()
    {
        var template = await _world.SeedTemplateAsync();
        LatestIs(1);

        var result = await ProposeAsync(EditList(template.Name, 1, EditFile("one", "two"), EditFile("absent", "x")));

        Assert.Equal(LoopTemplateProposalOutcome.Invalid, result.Outcome);
        Assert.Equal(1, result.EditIndex);
        Assert.StartsWith("edits[1] (editFile): ", result.Error);
        Assert.Empty(await StoredAsync());
    }

    [Fact]
    public async Task An_edit_naming_its_node_and_field_says_so_in_its_failure()
    {
        var template = await _world.SeedTemplateAsync();
        LatestIs(1);

        var result = await ProposeAsync(EditList(template.Name, 1, new LoopTemplateEdit { Op = "setNodeField", NodeId = "n1", Field = "prompt", Value = "x" }));

        Assert.Equal(0, result.EditIndex);
        Assert.StartsWith("edits[0] (setNodeField, node 'n1', field 'prompt'): ", result.Error);
    }

    [Fact]
    public async Task An_edit_list_over_the_size_limit_is_refused_stating_what_was_too_large()
    {
        var template = await _world.SeedTemplateAsync();
        LatestIs(1);

        var result = await ProposeAsync(EditList(template.Name, 1, EditFile("one", new string('x', 1_000_001))));

        Assert.Equal(LoopTemplateProposalOutcome.Invalid, result.Outcome);
        Assert.StartsWith("edits is too large (", result.Error);
        Assert.Empty(await StoredAsync());
    }

    [Fact]
    public async Task An_edit_list_making_a_graph_a_save_would_refuse_gets_the_errors_the_whole_document_gets()
    {
        var template = await _world.SeedTemplateAsync();
        LatestIs(1);
        _templates.Setup(t => t.CheckProposedDocument(It.Is<string>(d => d.Contains("two"))))
            .Returns(new ProposedLoopDocument(null, null, null, ["no start node"]));

        var result = await ProposeAsync(EditList(template.Name, 1, EditFile("one", "two")));

        Assert.Equal(["no start node"], result.ValidationErrors);
        Assert.Empty(await StoredAsync());
    }
}
