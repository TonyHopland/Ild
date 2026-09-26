using System.Data.Common;
using System.Net.Http.Headers;
using ILD.Core.Services.Remote;
using ILD.WorkItemServer;
using ILD.WorkItemServer.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.WorkItemServer;

/// <summary>
/// Work item edit proposals end to end through the typed client and the live
/// WorkItem server: the server owns the proposal rows and the one atomic
/// compare-and-apply, and the client is what turns its answers (a 409 for a
/// stale or already-decided proposal, a 404 for a proposal of another item)
/// into outcomes ILD can tell apart. ILD's own tests run against a fake client,
/// so this is the only place the two halves of the contract meet.
/// </summary>
public sealed class WorkItemEditProposalClientTests : IAsyncLifetime
{
    private const string ApiKey = "edit-proposals-test-key";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly HumanEditBeforeWorkItemWrite _humanEdit = new();
    private WebApplicationFactory<WorkItemServerProgram> _factory = null!;
    private WorkItemServerClient _client = null!;
    private readonly WorkItemServerOptions _opts = new() { BaseUrl = "http://localhost", ApiKey = ApiKey };

    public ValueTask InitializeAsync()
    {
        _connection.Open();
        Environment.SetEnvironmentVariable("WORKITEM_DB_CONNECTION_STRING", null);
        _factory = new WebApplicationFactory<WorkItemServerProgram>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkItemServer:ApiKeys"] = ApiKey,
                ["Serilog:WriteToConsole"] = "false",
            }));
            b.ConfigureServices(services =>
            {
                services.RemoveHostedService<ILD.WorkItemServer.Hosting.StaleWorkItemReclaimer>();
                services.PostConfigure<ApiKeyOptions>(options => options.Keys = ApiKey);
                var existing = services.FirstOrDefault(d => d.ServiceType == typeof(DbContextOptions<WorkItemServerDbContext>));
                if (existing != null) services.Remove(existing);
                services.AddDbContext<WorkItemServerDbContext>(o =>
                {
                    o.UseSqlite(_connection);
                    o.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
                    o.AddInterceptors(_humanEdit);
                });
            });
        });
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        _client = new WorkItemServerClient(http);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        _connection.Dispose();
    }

    private async Task<RemoteWorkItem> CreateItemAsync(string title = "Old backlog item") =>
        await _client.CreateAsync(_opts, new RemoteCreateWorkItemRequest
        {
            Title = title,
            Description = "The original description.",
            Tags = new[] { "legacy-tag", "HIL" },
            BranchNameOverride = "feature/original",
            BaseBranchOverride = "develop",
        });

    private async Task<RemoteWorkItemEditProposal> ProposeAsync(string workItemId, RemoteCreateEditProposalRequest request)
    {
        var result = await _client.CreateEditProposalAsync(_opts, workItemId, request);
        Assert.Equal(EditProposalCreateOutcome.Created, result.Outcome);
        return result.Proposal!;
    }

    private async Task<RemoteWorkItemEditProposal> ReadProposalAsync(string workItemId, Guid proposalId)
        => Assert.Single((await _client.ListEditProposalsAsync(_opts, workItemId))!, p => p.Id == proposalId);

    private static void AssertFieldsEqual(RemoteWorkItem item, string title, string? description, string[] tags, string? branch, string? baseBranch)
    {
        Assert.Equal(title, item.Title);
        Assert.Equal(description, item.Description);
        Assert.Equal(tags, item.Tags);
        Assert.Equal(branch, item.BranchNameOverride);
        Assert.Equal(baseBranch, item.BaseBranchOverride);
    }

    [Fact]
    public async Task A_proposal_snapshots_the_item_changes_nothing_and_approving_it_applies_exactly_the_proposed_fields()
    {
        var item = await CreateItemAsync();
        await _client.TransitionAsync(_opts, item.Id, new RemoteTransitionRequest { TargetStatus = RemoteWorkItemStatus.WorkQueue }, TestContext.Current.CancellationToken);
        var runId = Guid.NewGuid();

        var proposal = await ProposeAsync(item.Id, new RemoteCreateEditProposalRequest
        {
            Description = "A sharper description.",
            Tags = new[] { "new-tag" },
            BaseBranchOverride = "",
            Rationale = "The old description no longer matches the code.",
            CreatedByLoopRunId = runId,
        });

        Assert.Equal(RemoteEditProposalStatus.Pending, proposal.Status);
        Assert.Equal(item.Id, proposal.WorkItemId);
        Assert.Null(proposal.Proposed.Title);
        Assert.Equal("A sharper description.", proposal.Proposed.Description);
        Assert.Equal(new[] { "new-tag" }, proposal.Proposed.Tags);
        Assert.Null(proposal.Proposed.BranchNameOverride);
        Assert.Equal("", proposal.Proposed.BaseBranchOverride);
        Assert.Equal("Old backlog item", proposal.Snapshot.Title);
        Assert.Equal("The original description.", proposal.Snapshot.Description);
        Assert.Equal(new[] { "legacy-tag", "HIL" }, proposal.Snapshot.Tags);
        Assert.Equal("feature/original", proposal.Snapshot.BranchNameOverride);
        Assert.Equal("develop", proposal.Snapshot.BaseBranchOverride);
        Assert.Equal("The old description no longer matches the code.", proposal.Rationale);
        Assert.Equal(runId, proposal.CreatedByLoopRunId);
        Assert.Null(proposal.CreatedByChatSessionId);
        Assert.NotEqual(default, proposal.CreatedAt);
        Assert.Null(proposal.DecidedAt);

        var untouched = (await _client.GetAsync(_opts, item.Id, TestContext.Current.CancellationToken))!;
        AssertFieldsEqual(untouched, "Old backlog item", "The original description.",
            new[] { "legacy-tag", "HIL" }, "feature/original", "develop");
        Assert.Equal(1, untouched.PendingEditProposalCount);
        var statusBefore = untouched.Status;

        var approved = await _client.ApproveEditProposalAsync(_opts, item.Id, proposal.Id, TestContext.Current.CancellationToken);

        Assert.Equal(EditProposalDecisionOutcome.Applied, approved.Outcome);
        Assert.Equal(RemoteEditProposalStatus.Approved, approved.Proposal!.Status);
        Assert.NotNull(approved.Proposal.DecidedAt);
        AssertFieldsEqual(approved.WorkItem!, "Old backlog item", "A sharper description.",
            new[] { "new-tag" }, "feature/original", null);

        var stored = (await _client.GetAsync(_opts, item.Id, TestContext.Current.CancellationToken))!;
        AssertFieldsEqual(stored, "Old backlog item", "A sharper description.",
            new[] { "new-tag" }, "feature/original", null);
        Assert.Equal(statusBefore, stored.Status);
        Assert.Equal(0, stored.PendingEditProposalCount);
        Assert.Equal(RemoteEditProposalStatus.Approved, (await ReadProposalAsync(item.Id, proposal.Id)).Status);
    }

    [Fact]
    public async Task Approving_after_a_human_edited_a_field_marks_the_proposal_stale_and_keeps_the_human_edit()
    {
        var item = await CreateItemAsync();
        var proposal = await ProposeAsync(item.Id, new RemoteCreateEditProposalRequest { Title = "Agent title" });

        await _client.UpdateAsync(_opts, item.Id, new RemoteUpdateWorkItemRequest { Description = "Edited by a human." }, TestContext.Current.CancellationToken);

        var result = await _client.ApproveEditProposalAsync(_opts, item.Id, proposal.Id, TestContext.Current.CancellationToken);

        Assert.Equal(EditProposalDecisionOutcome.Stale, result.Outcome);
        Assert.Equal(RemoteEditProposalStatus.Stale, result.Proposal!.Status);
        var stored = (await _client.GetAsync(_opts, item.Id, TestContext.Current.CancellationToken))!;
        AssertFieldsEqual(stored, "Old backlog item", "Edited by a human.",
            new[] { "legacy-tag", "HIL" }, "feature/original", "develop");
        Assert.Equal(RemoteEditProposalStatus.Stale, (await ReadProposalAsync(item.Id, proposal.Id)).Status);
    }

    /// <summary>
    /// Staleness is about the five editable fields only. A status transition or
    /// a conversation append moves the item's UpdatedAt without touching any of
    /// them, and must not make a proposal on an active item unapprovable.
    /// </summary>
    [Fact]
    public async Task Changes_outside_the_editable_fields_do_not_make_a_proposal_stale()
    {
        var item = await CreateItemAsync();
        var proposal = await ProposeAsync(item.Id, new RemoteCreateEditProposalRequest { Title = "Agent title" });

        await _client.TransitionAsync(_opts, item.Id, new RemoteTransitionRequest { TargetStatus = RemoteWorkItemStatus.WorkQueue }, TestContext.Current.CancellationToken);
        await _client.AppendConversationAsync(_opts, item.Id, "user", "a note", name: null, ct: TestContext.Current.CancellationToken);
        var statusBefore = (await _client.GetAsync(_opts, item.Id, TestContext.Current.CancellationToken))!.Status;
        Assert.NotEqual(RemoteWorkItemStatus.Backlog, statusBefore);

        var result = await _client.ApproveEditProposalAsync(_opts, item.Id, proposal.Id, TestContext.Current.CancellationToken);

        Assert.Equal(EditProposalDecisionOutcome.Applied, result.Outcome);
        var stored = (await _client.GetAsync(_opts, item.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal("Agent title", stored.Title);
        Assert.Equal(statusBefore, stored.Status);
    }

    /// <summary>
    /// The check and the write are one step. The human edit here lands after the
    /// approve has started — immediately before its write to the item — which is
    /// the window a read-compare-then-update approve leaves open. An atomic
    /// compare-and-apply sees the item no longer matches the snapshot and
    /// applies nothing.
    /// </summary>
    [Fact]
    public async Task A_human_edit_landing_while_the_approve_is_in_flight_is_never_overwritten()
    {
        var item = await CreateItemAsync();
        var proposal = await ProposeAsync(item.Id, new RemoteCreateEditProposalRequest { Title = "Agent title" });

        _humanEdit.Arm(item.Id, "Edited by a human mid-approve.");
        var result = await _client.ApproveEditProposalAsync(_opts, item.Id, proposal.Id, TestContext.Current.CancellationToken);

        Assert.True(_humanEdit.Fired, "the approve never wrote to the work item");
        Assert.Equal(EditProposalDecisionOutcome.Stale, result.Outcome);
        var stored = (await _client.GetAsync(_opts, item.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal("Old backlog item", stored.Title);
        Assert.Equal("Edited by a human mid-approve.", stored.Description);
        Assert.Equal(RemoteEditProposalStatus.Stale, (await ReadProposalAsync(item.Id, proposal.Id)).Status);
    }

    [Fact]
    public async Task Approving_one_of_several_pending_proposals_makes_the_others_stale_and_they_can_no_longer_apply()
    {
        var item = await CreateItemAsync();
        var first = await ProposeAsync(item.Id, new RemoteCreateEditProposalRequest { Title = "First title" });
        var second = await ProposeAsync(item.Id, new RemoteCreateEditProposalRequest { Description = "Second description." });
        var third = await ProposeAsync(item.Id, new RemoteCreateEditProposalRequest { BranchNameOverride = "feature/third" });
        Assert.Equal(3, (await _client.GetAsync(_opts, item.Id, TestContext.Current.CancellationToken))!.PendingEditProposalCount);

        var approved = await _client.ApproveEditProposalAsync(_opts, item.Id, first.Id, TestContext.Current.CancellationToken);
        Assert.Equal(EditProposalDecisionOutcome.Applied, approved.Outcome);

        Assert.Equal(RemoteEditProposalStatus.Stale, (await ReadProposalAsync(item.Id, second.Id)).Status);
        Assert.Equal(RemoteEditProposalStatus.Stale, (await ReadProposalAsync(item.Id, third.Id)).Status);
        Assert.Equal(0, (await _client.GetAsync(_opts, item.Id, TestContext.Current.CancellationToken))!.PendingEditProposalCount);

        var late = await _client.ApproveEditProposalAsync(_opts, item.Id, second.Id, TestContext.Current.CancellationToken);
        Assert.NotEqual(EditProposalDecisionOutcome.Applied, late.Outcome);
        var stored = (await _client.GetAsync(_opts, item.Id, TestContext.Current.CancellationToken))!;
        AssertFieldsEqual(stored, "First title", "The original description.",
            new[] { "legacy-tag", "HIL" }, "feature/original", "develop");
    }

    [Fact]
    public async Task Rejecting_stores_the_reason_applies_nothing_and_a_decided_proposal_cannot_be_decided_again()
    {
        var item = await CreateItemAsync();
        var proposal = await ProposeAsync(item.Id, new RemoteCreateEditProposalRequest { Title = "Agent title" });

        var rejected = await _client.RejectEditProposalAsync(_opts, item.Id, proposal.Id, "Too vague.", TestContext.Current.CancellationToken);

        Assert.Equal(EditProposalDecisionOutcome.Rejected, rejected.Outcome);
        Assert.Equal(RemoteEditProposalStatus.Rejected, rejected.Proposal!.Status);
        Assert.Equal("Too vague.", rejected.Proposal.RejectionReason);
        Assert.NotNull(rejected.Proposal.DecidedAt);
        var listed = await ReadProposalAsync(item.Id, proposal.Id);
        Assert.Equal(RemoteEditProposalStatus.Rejected, listed.Status);
        Assert.Equal("Too vague.", listed.RejectionReason);

        Assert.Equal(EditProposalDecisionOutcome.NotPending, (await _client.ApproveEditProposalAsync(_opts, item.Id, proposal.Id, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(EditProposalDecisionOutcome.NotPending, (await _client.RejectEditProposalAsync(_opts, item.Id, proposal.Id, "again", TestContext.Current.CancellationToken)).Outcome);

        var stored = (await _client.GetAsync(_opts, item.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal("Old backlog item", stored.Title);
        Assert.Equal("Too vague.", (await ReadProposalAsync(item.Id, proposal.Id)).RejectionReason);
    }

    [Fact]
    public async Task An_approved_proposal_cannot_be_approved_or_rejected_again()
    {
        var item = await CreateItemAsync();
        var proposal = await ProposeAsync(item.Id, new RemoteCreateEditProposalRequest { Title = "Agent title" });
        Assert.Equal(EditProposalDecisionOutcome.Applied, (await _client.ApproveEditProposalAsync(_opts, item.Id, proposal.Id, TestContext.Current.CancellationToken)).Outcome);
        await _client.UpdateAsync(_opts, item.Id, new RemoteUpdateWorkItemRequest { Title = "Human title" }, TestContext.Current.CancellationToken);

        Assert.Equal(EditProposalDecisionOutcome.NotPending, (await _client.ApproveEditProposalAsync(_opts, item.Id, proposal.Id, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(EditProposalDecisionOutcome.NotPending, (await _client.RejectEditProposalAsync(_opts, item.Id, proposal.Id, null, TestContext.Current.CancellationToken)).Outcome);

        Assert.Equal("Human title", (await _client.GetAsync(_opts, item.Id, TestContext.Current.CancellationToken))!.Title);
        Assert.Equal(RemoteEditProposalStatus.Approved, (await ReadProposalAsync(item.Id, proposal.Id)).Status);
    }

    [Fact]
    public async Task An_unknown_proposal_or_one_belonging_to_another_item_is_not_found()
    {
        var item = await CreateItemAsync();
        var other = await CreateItemAsync("Another item");
        var proposal = await ProposeAsync(item.Id, new RemoteCreateEditProposalRequest { Title = "Agent title" });

        Assert.Equal(EditProposalDecisionOutcome.NotFound, (await _client.ApproveEditProposalAsync(_opts, item.Id, Guid.NewGuid(), TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(EditProposalDecisionOutcome.NotFound, (await _client.ApproveEditProposalAsync(_opts, other.Id, proposal.Id, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(EditProposalDecisionOutcome.NotFound, (await _client.RejectEditProposalAsync(_opts, other.Id, proposal.Id, null, TestContext.Current.CancellationToken)).Outcome);

        Assert.Equal("Another item", (await _client.GetAsync(_opts, other.Id, TestContext.Current.CancellationToken))!.Title);
        Assert.Equal(RemoteEditProposalStatus.Pending, (await ReadProposalAsync(item.Id, proposal.Id)).Status);
        Assert.Null(await _client.ListEditProposalsAsync(_opts, "no-such-item", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Creating_a_proposal_is_refused_for_an_unknown_item_and_past_twenty_pending_on_one_item()
    {
        var unknown = await _client.CreateEditProposalAsync(_opts, "no-such-item",
            new RemoteCreateEditProposalRequest { Title = "Agent title" }, TestContext.Current.CancellationToken);
        Assert.Equal(EditProposalCreateOutcome.NotFound, unknown.Outcome);

        var item = await CreateItemAsync();
        for (var i = 0; i < 20; i++)
            await ProposeAsync(item.Id, new RemoteCreateEditProposalRequest { Title = $"Title {i}" });

        var overCap = await _client.CreateEditProposalAsync(_opts, item.Id,
            new RemoteCreateEditProposalRequest { Title = "One too many" }, TestContext.Current.CancellationToken);

        Assert.Equal(EditProposalCreateOutcome.TooManyPending, overCap.Outcome);
        Assert.Equal(20, (await _client.ListEditProposalsAsync(_opts, item.Id, TestContext.Current.CancellationToken))!.Count);
    }

    [Fact]
    public async Task An_invalid_proposal_is_refused_with_the_servers_reason_and_stores_nothing()
    {
        var item = await CreateItemAsync();

        var blankTitle = await _client.CreateEditProposalAsync(_opts, item.Id,
            new RemoteCreateEditProposalRequest { Title = "   " }, TestContext.Current.CancellationToken);
        var nothingProposed = await _client.CreateEditProposalAsync(_opts, item.Id,
            new RemoteCreateEditProposalRequest { Rationale = "no fields" }, TestContext.Current.CancellationToken);

        Assert.Equal(EditProposalCreateOutcome.Invalid, blankTitle.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(blankTitle.Error));
        Assert.Equal(EditProposalCreateOutcome.Invalid, nothingProposed.Outcome);
        Assert.Empty((await _client.ListEditProposalsAsync(_opts, item.Id, TestContext.Current.CancellationToken))!);
    }

    [Fact]
    public async Task A_chat_sessions_decided_proposals_are_listed_until_their_decision_is_marked_delivered()
    {
        var chat = Guid.NewGuid();
        var otherChat = Guid.NewGuid();
        var item = await CreateItemAsync();
        var otherItem = await CreateItemAsync("Another item");
        var rejected = await ProposeAsync(item.Id, new RemoteCreateEditProposalRequest { Title = "Rejected title", CreatedByChatSessionId = chat });
        var stillPending = await ProposeAsync(otherItem.Id, new RemoteCreateEditProposalRequest { Title = "Pending title", CreatedByChatSessionId = chat });
        var otherChats = await ProposeAsync(item.Id, new RemoteCreateEditProposalRequest { Title = "Other chat title", CreatedByChatSessionId = otherChat });
        await _client.RejectEditProposalAsync(_opts, item.Id, rejected.Id, "Too vague.", TestContext.Current.CancellationToken);
        await _client.RejectEditProposalAsync(_opts, item.Id, otherChats.Id, null, TestContext.Current.CancellationToken);

        var undelivered = new RemoteEditProposalQuery { CreatedByChatSessionId = chat, UndeliveredOnly = true };
        var due = await _client.QueryEditProposalsAsync(_opts, undelivered, TestContext.Current.CancellationToken);
        Assert.Equal(rejected.Id, Assert.Single(due).Id);
        Assert.Equal("Too vague.", due[0].RejectionReason);

        // A pending proposal has no decision to deliver, so acknowledging it early
        // must not swallow the decision it gets later.
        await _client.MarkEditProposalDecisionsDeliveredAsync(_opts, new[] { rejected.Id, stillPending.Id }, TestContext.Current.CancellationToken);
        Assert.Empty(await _client.QueryEditProposalsAsync(_opts, undelivered, TestContext.Current.CancellationToken));

        await _client.RejectEditProposalAsync(_opts, otherItem.Id, stillPending.Id, "No.", TestContext.Current.CancellationToken);
        Assert.Equal(stillPending.Id, Assert.Single(await _client.QueryEditProposalsAsync(_opts, undelivered, TestContext.Current.CancellationToken)).Id);

        var pending = await _client.QueryEditProposalsAsync(_opts, new RemoteEditProposalQuery { Status = RemoteEditProposalStatus.Pending }, TestContext.Current.CancellationToken);
        Assert.Empty(pending);
        var chatsAll = await _client.QueryEditProposalsAsync(_opts, new RemoteEditProposalQuery { CreatedByChatSessionId = chat }, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { rejected.Id, stillPending.Id }.OrderBy(x => x), chatsAll.Select(p => p.Id).OrderBy(x => x));
    }

    [Fact]
    public async Task Pending_proposals_are_listed_across_items()
    {
        var item = await CreateItemAsync();
        var otherItem = await CreateItemAsync("Another item");
        var a = await ProposeAsync(item.Id, new RemoteCreateEditProposalRequest { Title = "A" });
        var b = await ProposeAsync(otherItem.Id, new RemoteCreateEditProposalRequest { Title = "B" });
        var decided = await ProposeAsync(otherItem.Id, new RemoteCreateEditProposalRequest { Title = "C" });
        await _client.RejectEditProposalAsync(_opts, otherItem.Id, decided.Id, null, TestContext.Current.CancellationToken);

        var pending = await _client.QueryEditProposalsAsync(_opts, new RemoteEditProposalQuery { Status = RemoteEditProposalStatus.Pending }, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { a.Id, b.Id }.OrderBy(x => x), pending.Select(p => p.Id).OrderBy(x => x));
        Assert.Equal(1, Assert.Single(await _client.ListAsync(_opts, null, null, TestContext.Current.CancellationToken), w => w.Id == item.Id).PendingEditProposalCount);
    }

    [Fact]
    public async Task Deleting_a_work_item_removes_its_proposals()
    {
        var item = await CreateItemAsync();
        var keep = await CreateItemAsync("Another item");
        await ProposeAsync(item.Id, new RemoteCreateEditProposalRequest { Title = "Gone with the item" });
        var kept = await ProposeAsync(keep.Id, new RemoteCreateEditProposalRequest { Title = "Stays" });

        Assert.True(await _client.DeleteAsync(_opts, item.Id, TestContext.Current.CancellationToken));

        var remaining = await _client.QueryEditProposalsAsync(_opts, new RemoteEditProposalQuery(), TestContext.Current.CancellationToken);
        Assert.Equal(kept.Id, Assert.Single(remaining).Id);
    }

    /// <summary>
    /// Plays a human's edit to one work item immediately before the next EF
    /// command that writes the WorkItems table — on the approving command's own
    /// connection and transaction, so it is exactly as if the human's save
    /// committed in the gap between an approve starting and it writing.
    /// </summary>
    private sealed class HumanEditBeforeWorkItemWrite : DbCommandInterceptor
    {
        private string? _workItemId;
        private string? _description;

        public bool Fired { get; private set; }

        public void Arm(string workItemId, string description)
        {
            _workItemId = workItemId;
            _description = description;
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            MaybeEdit(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            MaybeEdit(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            MaybeEdit(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            MaybeEdit(command);
            return ValueTask.FromResult(result);
        }

        private void MaybeEdit(DbCommand command)
        {
            if (_workItemId is null || !command.CommandText.Contains("UPDATE \"WorkItems\"", StringComparison.Ordinal))
                return;
            var workItemId = _workItemId;
            _workItemId = null;

            using var edit = command.Connection!.CreateCommand();
            edit.Transaction = command.Transaction;
            edit.CommandText = "UPDATE \"WorkItems\" SET \"Description\" = $description WHERE \"Id\" = $id";
            var description = edit.CreateParameter();
            description.ParameterName = "$description";
            description.Value = _description;
            edit.Parameters.Add(description);
            var id = edit.CreateParameter();
            id.ParameterName = "$id";
            id.Value = workItemId;
            edit.Parameters.Add(id);
            Assert.Equal(1, edit.ExecuteNonQuery());
            Fired = true;
        }
    }
}
