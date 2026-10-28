using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using static ILD.Tests.Integration.ProposalApiTestSupport;

namespace ILD.Tests.Integration;

/// <summary>
/// Human Input Proposals through ILD's two surfaces: an agent (a loop run or a
/// chat) suggests the answer for a work item parked at a Human node, and only a
/// signed-in human approves or rejects it. Approving answers the parked visit
/// the way the matching Action-tab button does, or marks the suggestion Stale
/// when that visit is no longer open. The parked item is seeded in the database
/// and the real engine takes the answer; its relaunch of the run is held off by
/// raising the shutdown flag, so the run stays where the answer left it.
/// </summary>
// A host per test: the claim race rewrites every proposal row and each test signals shutdown on its host.
public class HumanInputProposalApiTests
{
    private const string AgentUrl = "/api/v1/agent/workitems";
    private const string ProposalsUrl = "/api/v1/human-input-proposals";
    private const string Rework = "Rework";
    private static readonly string[] Offered = ["OnSuccess", "OnFailure", Rework];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Host() : ProposalApiTestHost<ClaimRace>("HumanInputProposals")
    {
        public int Hints(string workItemId)
            => WorkItemNotifier.Invocations.Count(i =>
                i.Method.Name == "HumanInputProposalsChangedAsync" && i.Arguments.Contains(workItemId));
    }

    /// <summary>Plays a second approver that wins the claim just before this one's claim statement runs. Armed per approve.</summary>
    internal sealed class ClaimRace : DbCommandInterceptor
    {
        private static readonly Regex ProposalUpdate = new(@"^\s*UPDATE\s+""?HumanInputProposals""?[\s(]", RegexOptions.IgnoreCase);
        private static readonly Regex RunUpdate = new(@"^\s*UPDATE\s+""?LoopRuns""?[\s(]", RegexOptions.IgnoreCase);
        private bool _armed;
        private bool _failDelivery;

        public void Arm() => _armed = true;

        /// <summary>The next write to a run, which is the answer being delivered, fails.</summary>
        public void ArmDeliveryFailure() => _failDelivery = true;

        private void RaceIfClaim(DbCommand command)
        {
            if (_failDelivery && RunUpdate.IsMatch(command.CommandText))
            {
                _failDelivery = false;
                throw new InvalidOperationException("The database went away while the answer was delivered.");
            }
            if (!ProposalUpdate.IsMatch(command.CommandText) || !_armed) return;
            _armed = false;
            using var race = command.Connection!.CreateCommand();
            race.Transaction = command.Transaction;
            race.CommandText = "UPDATE \"HumanInputProposals\" SET \"Status\" = 1";
            race.ExecuteNonQuery();
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            RaceIfClaim(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            RaceIfClaim(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static async Task<Host> StartHostAsync()
    {
        var host = new Host();
        await host.StartAsync();
        host.Factory.Services.GetRequiredService<IShutdownState>().SignalStopping();
        return host;
    }

    private sealed record Parked(string WorkItemId, Guid RunId, Guid RunNodeId, Guid NodeId, Guid VersionId);

    private static async Task<Parked> SeedParkedAsync(
        Host host,
        NodeType nodeType = NodeType.Human,
        string? reason = HumanFeedbackReasons.HumanInputNeeded,
        LoopRunStatus status = LoopRunStatus.WaitingHuman,
        bool halted = false,
        bool seedRun = true,
        LoopRunNodeStatus visitStatus = LoopRunNodeStatus.WaitingHuman)
    {
        var workItemId = await CreateHumanItemAsync(host);
        using var scope = host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = $"parked-{Guid.NewGuid():N}" };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1, CreatedAt = DateTime.UtcNow };
        var node = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = version.Id, NodeType = nodeType, Label = "ask", CreatedAt = DateTime.UtcNow };
        var next = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = version.Id, NodeType = NodeType.Cleanup, Label = "next", CreatedAt = DateTime.UtcNow };
        db.LoopTemplates.Add(template);
        db.LoopTemplateVersions.Add(version);
        db.LoopNodes.AddRange(node, next);
        db.LoopNodeEdges.AddRange(
            new LoopNodeEdge { Id = Guid.NewGuid(), SourceNodeId = node.Id, TargetNodeId = next.Id, EdgeType = EdgeType.OnSuccess, CreatedAt = DateTime.UtcNow },
            new LoopNodeEdge { Id = Guid.NewGuid(), SourceNodeId = node.Id, TargetNodeId = next.Id, EdgeType = EdgeType.OnFailure, CreatedAt = DateTime.UtcNow },
            new LoopNodeEdge { Id = Guid.NewGuid(), SourceNodeId = node.Id, TargetNodeId = next.Id, EdgeType = EdgeType.Custom, Name = Rework, CreatedAt = DateTime.UtcNow });
        var runId = Guid.NewGuid();
        var visitId = Guid.NewGuid();
        if (seedRun)
        {
            db.LoopRuns.Add(new LoopRun
            {
                Id = runId,
                WorkItemId = workItemId,
                LoopTemplateVersionId = version.Id,
                Status = status,
                RecoveryPolicy = RecoveryPolicy.AutoResume,
                StartedAt = DateTime.UtcNow.AddMinutes(-10),
                CurrentNodeId = node.Id,
                HumanFeedbackReason = reason,
                IsHalted = halted,
                CompletedAt = status is LoopRunStatus.Completed or LoopRunStatus.Cancelled ? DateTime.UtcNow : null,
            });
            db.LoopRunNodes.Add(new LoopRunNode
            {
                Id = visitId,
                LoopRunId = runId,
                LoopNodeId = node.Id,
                Status = visitStatus,
                StartedAt = DateTime.UtcNow.AddMinutes(-5),
                CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            });
        }
        await db.SaveChangesAsync(Ct);
        return new Parked(workItemId, runId, visitId, node.Id, version.Id);
    }

    private static async Task MutateAsync(Host host, Func<AppDbContext, Task> change)
    {
        using var scope = host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await change(db);
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<LoopRun> RunAsync(Host host, Guid runId)
    {
        using var scope = host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.LoopRuns.AsNoTracking().SingleAsync(r => r.Id == runId, Ct);
    }

    private static Task<HttpResponseMessage> ProposeAsync(
        HttpClient agent, string workItemId, string output, string text = "Ship it.", string rationale = "The tests pass.")
        => agent.PostAsJsonAsync($"{AgentUrl}/{Uri.EscapeDataString(workItemId)}/human-input-proposals", new { output, text, rationale }, Ct);

    private static Task<HttpResponseMessage> ProposeRawAsync(HttpClient agent, string workItemId, string json)
        => agent.PostAsync($"{AgentUrl}/{Uri.EscapeDataString(workItemId)}/human-input-proposals",
            new StringContent(json, Encoding.UTF8, "application/json"), Ct);

    private static async Task<string> ProposeOkAsync(
        HttpClient agent, string workItemId, string output, string text = "Ship it.", string rationale = "The tests pass.")
    {
        var resp = await ProposeAsync(agent, workItemId, output, text, rationale);
        Assert.True(resp.StatusCode == HttpStatusCode.Created,
            $"propose {output}: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(Ct)}");
        var json = await ReadJsonAsync(resp);
        Assert.Equal("Pending", Status(json));
        Assert.Equal(workItemId, json.GetProperty("workItemId").GetString());
        Assert.Equal(output, json.GetProperty("output").GetString());
        return json.GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement[]> ListAsync(Host host, string workItemId, string query = "")
    {
        var resp = await host.Human.GetAsync($"{ProposalsUrl}?workItemId={Uri.EscapeDataString(workItemId)}{query}", Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await ReadJsonAsync(resp)).EnumerateArray().ToArray();
    }

    private static async Task<JsonElement> ReadAsync(Host host, string workItemId, string id)
        => Assert.Single(await ListAsync(host, workItemId), p => p.GetProperty("id").GetString() == id);

    private static async Task<string> StatusOfAsync(Host host, string workItemId, string id)
        => Status(await ReadAsync(host, workItemId, id));

    private static Task<HttpResponseMessage> ApproveAsync(HttpClient client, string id)
        => client.PostAsync($"{ProposalsUrl}/{id}/approve", null, Ct);

    private static Task<HttpResponseMessage> RejectAsync(HttpClient client, string id, string? reason)
        => client.PostAsJsonAsync($"{ProposalsUrl}/{id}/reject", new { reason }, Ct);

    private static async Task<JsonElement> ConflictProposalAsync(HttpResponseMessage resp, string because)
        => (await ConflictAsync(resp, because)).GetProperty("proposal");

    private static async Task<JsonElement[]> AnswersAsync(Host host, Guid runId)
    {
        var resp = await host.Human.GetAsync($"/api/v1/loopruns/{runId}/events?limit=500", Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await ReadJsonAsync(resp)).GetProperty("entries").EnumerateArray()
            .Where(e => e.GetProperty("eventType").GetString() == "HumanFeedbackReceived").ToArray();
    }

    private static async Task PressButtonAsync(Host host, Parked parked, string output, string text)
    {
        var (path, body) = output switch
        {
            "OnSuccess" => ("input", (object)new { runId = parked.RunId, input = text }),
            "OnFailure" => ("reject", new { runId = parked.RunId, input = text }),
            _ => ("edge", new { runId = parked.RunId, name = output, input = text }),
        };
        var resp = await host.Human.PostAsJsonAsync($"/api/v1/workitems/{parked.WorkItemId}/human-feedback/{path}", body, Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ── Making a suggestion ───────────────────────────────────────────────

    [Theory]
    [InlineData(Caller.LoopRun)]
    [InlineData(Caller.ChatSession)]
    public async Task A_suggestion_for_a_parked_item_is_stored_with_its_visit_and_source_and_listed_newest_first(Caller caller)
    {
        await using var host = await StartHostAsync();
        var parked = await SeedParkedAsync(host);
        var (runId, chatId) = await SeedCallerAsync(host, caller);
        var agent = host.Agent(runId, chatId);
        host.ClearHints();

        var first = await ProposeOkAsync(agent, parked.WorkItemId, "OnFailure", "Needs another look.", "The diff is partial.");
        Assert.Equal(1, host.Hints(parked.WorkItemId));
        var second = await ProposeOkAsync(host.Agent(await SeedRunAsync(host.Factory)), parked.WorkItemId, Rework, "", "Split it up.");

        var listed = await ListAsync(host, parked.WorkItemId);
        Assert.Equal(new[] { second, first }, listed.Select(p => p.GetProperty("id").GetString()));
        var stored = listed[1];
        Assert.Equal(parked.WorkItemId, stored.GetProperty("workItemId").GetString());
        Assert.Equal(parked.RunId, stored.GetProperty("runId").GetGuid());
        Assert.Equal(parked.RunNodeId, stored.GetProperty("runNodeId").GetGuid());
        Assert.Equal("OnFailure", stored.GetProperty("output").GetString());
        Assert.Equal("Needs another look.", stored.GetProperty("text").GetString());
        Assert.Equal("The diff is partial.", stored.GetProperty("rationale").GetString());
        Assert.Equal("Pending", Status(stored));
        Assert.True(IsNullOrAbsent(stored, "rejectionReason"));
        Assert.True(IsNullOrAbsent(stored, "decidedBy"));
        Assert.True(IsNullOrAbsent(stored, "decidedAt"));
        Assert.False(IsNullOrAbsent(stored, "createdAt"));
        Assert.Equal(runId, IsNullOrAbsent(stored, "createdByLoopRunId") ? null : stored.GetProperty("createdByLoopRunId").GetGuid());
        Assert.Equal(chatId, IsNullOrAbsent(stored, "createdByChatSessionId") ? null : stored.GetProperty("createdByChatSessionId").GetGuid());
        Assert.Equal("", listed[0].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(2000, 8192)]
    [InlineData(1, 0)]
    public async Task A_rationale_and_text_at_their_limits_are_accepted_and_the_text_may_be_empty(int rationaleLength, int textLength)
    {
        await using var host = await StartHostAsync();
        var parked = await SeedParkedAsync(host);

        await ProposeOkAsync(host.Agent(await SeedRunAsync(host.Factory)), parked.WorkItemId, "OnSuccess",
            new string('t', textLength), new string('r', rationaleLength));
    }

    public enum Refusal { NoSession, UnknownRun, UnknownChat, UnknownItem }

    [Theory]
    [InlineData(Refusal.NoSession, HttpStatusCode.BadRequest)]
    [InlineData(Refusal.UnknownRun, HttpStatusCode.Forbidden)]
    [InlineData(Refusal.UnknownChat, HttpStatusCode.Forbidden)]
    [InlineData(Refusal.UnknownItem, HttpStatusCode.NotFound)]
    public async Task A_suggestion_needs_a_known_session_and_a_known_work_item(Refusal refusal, HttpStatusCode expected)
    {
        await using var host = await StartHostAsync();
        var parked = await SeedParkedAsync(host);
        var agent = refusal switch
        {
            Refusal.NoSession => host.Agent(),
            Refusal.UnknownRun => host.Agent(runId: Guid.NewGuid()),
            Refusal.UnknownChat => host.Agent(chatSessionId: Guid.NewGuid()),
            _ => host.Agent(await SeedRunAsync(host.Factory)),
        };
        host.ClearHints();

        var resp = await ProposeAsync(agent, refusal == Refusal.UnknownItem ? "no-such-item" : parked.WorkItemId, "OnSuccess");

        Assert.Equal(expected, resp.StatusCode);
        Assert.Empty(await ListAsync(host, parked.WorkItemId));
        Assert.Equal(0, host.Hints(parked.WorkItemId));
    }

    public enum NotParked { Running, Done, NoRun, PrNode, OnFailure, Halted, NoWaitingVisit }

    [Theory]
    [InlineData(NotParked.Running)]
    [InlineData(NotParked.Done)]
    [InlineData(NotParked.NoRun)]
    [InlineData(NotParked.PrNode)]
    [InlineData(NotParked.OnFailure)]
    [InlineData(NotParked.Halted)]
    [InlineData(NotParked.NoWaitingVisit)]
    public async Task An_item_that_is_not_waiting_for_human_input_is_a_409_that_stores_nothing(NotParked state)
    {
        await using var host = await StartHostAsync();
        var parked = state switch
        {
            NotParked.Running => await SeedParkedAsync(host, status: LoopRunStatus.Running, reason: null),
            NotParked.Done => await SeedParkedAsync(host, status: LoopRunStatus.Completed, reason: null),
            NotParked.NoRun => await SeedParkedAsync(host, seedRun: false),
            NotParked.PrNode => await SeedParkedAsync(host, nodeType: NodeType.PR, reason: HumanFeedbackReasons.PrAwaitingMerge),
            NotParked.OnFailure => await SeedParkedAsync(host, nodeType: NodeType.AI, reason: HumanFeedbackReasons.NodeFailed),
            NotParked.Halted => await SeedParkedAsync(host, halted: true),
            _ => await SeedParkedAsync(host, visitStatus: LoopRunNodeStatus.Succeeded),
        };
        host.ClearHints();

        var resp = await ProposeAsync(host.Agent(await SeedRunAsync(host.Factory)), parked.WorkItemId, "OnSuccess");

        var error = (await ConflictAsync(resp, state.ToString())).GetProperty("error").GetString()!;
        Assert.Contains("waiting for human input", error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await ListAsync(host, parked.WorkItemId));
        Assert.Equal(0, host.Hints(parked.WorkItemId));
    }

    [Fact]
    public async Task An_output_the_node_does_not_offer_is_a_400_listing_the_offered_ones()
    {
        await using var host = await StartHostAsync();
        var parked = await SeedParkedAsync(host);

        var resp = await ProposeAsync(host.Agent(await SeedRunAsync(host.Factory)), parked.WorkItemId, "Nope");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var error = (await ReadJsonAsync(resp)).GetProperty("error").GetString()!;
        Assert.All(Offered, output => Assert.Contains(output, error));
        Assert.Empty(await ListAsync(host, parked.WorkItemId));
    }

    public static TheoryData<string, string> InvalidSuggestions => new()
    {
        { "no output", """{"text":"x","rationale":"r"}""" },
        { "blank output", """{"output":"  ","text":"x","rationale":"r"}""" },
        { "no rationale", """{"output":"OnSuccess","text":"x"}""" },
        { "blank rationale", """{"output":"OnSuccess","text":"x","rationale":"  "}""" },
        { "rationale over 2000", $$"""{"output":"OnSuccess","text":"x","rationale":"{{new string('r', 2001)}}"}""" },
        { "text over 8192", $$"""{"output":"OnSuccess","text":"{{new string('t', 8193)}}","rationale":"r"}""" },
    };

    [Theory]
    [MemberData(nameof(InvalidSuggestions))]
    public async Task An_invalid_suggestion_is_a_400_that_stores_nothing(string _, string json)
    {
        await using var host = await StartHostAsync();
        var parked = await SeedParkedAsync(host);
        host.ClearHints();

        var resp = await ProposeRawAsync(host.Agent(await SeedRunAsync(host.Factory)), parked.WorkItemId, json);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Empty(await ListAsync(host, parked.WorkItemId));
        Assert.Equal(0, host.Hints(parked.WorkItemId));
    }

    [Fact]
    public async Task A_new_suggestion_supersedes_only_the_same_sources_pending_one_for_the_same_item()
    {
        await using var host = await StartHostAsync();
        var item = await SeedParkedAsync(host);
        var other = await SeedParkedAsync(host);
        var run = host.Agent(await SeedRunAsync(host.Factory));
        var chat = host.Agent(chatSessionId: await SeedChatSessionAsync(host.Factory));
        var first = await ProposeOkAsync(run, item.WorkItemId, "OnSuccess");
        var chats = await ProposeOkAsync(chat, item.WorkItemId, "OnFailure");
        var elsewhere = await ProposeOkAsync(run, other.WorkItemId, "OnSuccess");

        var second = await ProposeOkAsync(run, item.WorkItemId, Rework);

        Assert.Equal("Superseded", await StatusOfAsync(host, item.WorkItemId, first));
        Assert.Equal("Pending", await StatusOfAsync(host, item.WorkItemId, chats));
        Assert.Equal("Pending", await StatusOfAsync(host, item.WorkItemId, second));
        Assert.Equal("Pending", await StatusOfAsync(host, other.WorkItemId, elsewhere));
    }

    // ── Reading ───────────────────────────────────────────────────────────

    [Fact]
    public async Task The_list_filters_by_status_and_refuses_an_unknown_status_or_no_work_item()
    {
        await using var host = await StartHostAsync();
        var parked = await SeedParkedAsync(host);
        var agent = host.Agent(await SeedRunAsync(host.Factory));
        var first = await ProposeOkAsync(agent, parked.WorkItemId, "OnSuccess");
        var second = await ProposeOkAsync(agent, parked.WorkItemId, "OnFailure");

        Assert.Equal(new[] { second }, (await ListAsync(host, parked.WorkItemId, "&status=Pending")).Select(p => p.GetProperty("id").GetString()));
        Assert.Equal(new[] { first }, (await ListAsync(host, parked.WorkItemId, "&status=Superseded")).Select(p => p.GetProperty("id").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Human.GetAsync($"{ProposalsUrl}?workItemId={parked.WorkItemId}&status=Bogus", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Human.GetAsync(ProposalsUrl, Ct)).StatusCode);
    }

    [Fact]
    public async Task The_agent_token_cannot_reach_the_human_routes()
    {
        await using var host = await StartHostAsync();
        var parked = await SeedParkedAsync(host);
        var agent = host.Agent(await SeedRunAsync(host.Factory));
        var id = await ProposeOkAsync(agent, parked.WorkItemId, "OnSuccess");

        var responses = new[]
        {
            await agent.GetAsync($"{ProposalsUrl}?workItemId={parked.WorkItemId}", Ct),
            await ApproveAsync(agent, id),
            await RejectAsync(agent, id, "Self-rejected."),
        };

        foreach (var resp in responses)
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal("Pending", await StatusOfAsync(host, parked.WorkItemId, id));
        Assert.Empty(await AnswersAsync(host, parked.RunId));
    }

    // ── Approving ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("OnSuccess", "Ship it.")]
    [InlineData("OnSuccess", "")]
    [InlineData("OnFailure", "Looks wrong.")]
    [InlineData("OnFailure", "")]
    [InlineData(Rework, "Split it up.")]
    public async Task Approving_answers_the_parked_visit_as_the_matching_button_does(string output, string text)
    {
        await using var host = await StartHostAsync();
        var pressed = await SeedParkedAsync(host);
        var approved = await SeedParkedAsync(host);
        await PressButtonAsync(host, pressed, output, text);
        var id = await ProposeOkAsync(host.Agent(await SeedRunAsync(host.Factory)), approved.WorkItemId, output, text);
        host.ClearHints();

        var resp = await ApproveAsync(host.Human, id);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var proposal = (await ReadJsonAsync(resp)).GetProperty("proposal");
        Assert.Equal("Approved", Status(proposal));
        Assert.Equal(host.Factory.AdminUsername, proposal.GetProperty("decidedBy").GetString());
        Assert.False(IsNullOrAbsent(proposal, "decidedAt"));
        var listed = await ReadAsync(host, approved.WorkItemId, id);
        Assert.Equal("Approved", Status(listed));
        Assert.Equal(host.Factory.AdminUsername, listed.GetProperty("decidedBy").GetString());

        var byButton = Assert.Single(await AnswersAsync(host, pressed.RunId));
        var byApproval = Assert.Single(await AnswersAsync(host, approved.RunId));
        Assert.Equal(approved.RunNodeId, byApproval.GetProperty("runNodeId").GetGuid());
        Assert.Equal(byButton.GetProperty("payload").GetString(), byApproval.GetProperty("payload").GetString());
        Assert.Equal(
            IsNullOrAbsent(byButton, "edgeName") ? null : byButton.GetProperty("edgeName").GetString(),
            IsNullOrAbsent(byApproval, "edgeName") ? null : byApproval.GetProperty("edgeName").GetString());
        var buttonRun = await RunAsync(host, pressed.RunId);
        var approvedRun = await RunAsync(host, approved.RunId);
        Assert.Equal(LoopRunStatus.Running, approvedRun.Status);
        Assert.Equal(buttonRun.Status, approvedRun.Status);
        Assert.Equal(buttonRun.ExternalActionResult, approvedRun.ExternalActionResult);
        Assert.Equal(buttonRun.ExternalActionResultType, approvedRun.ExternalActionResultType);
        Assert.Equal(buttonRun.ExternalActionEdgeName, approvedRun.ExternalActionEdgeName);
        Assert.Equal(buttonRun.HumanFeedbackReason, approvedRun.HumanFeedbackReason);
        Assert.True(host.Hints(approved.WorkItemId) >= 1, "the board was not hinted");
    }

    public enum Moved { Resumed, Cancelled, Deleted, AnotherRun, LaterVisit }

    [Theory]
    [InlineData(Moved.Resumed)]
    [InlineData(Moved.Cancelled)]
    [InlineData(Moved.Deleted)]
    [InlineData(Moved.AnotherRun)]
    [InlineData(Moved.LaterVisit)]
    public async Task Approving_after_the_visit_is_no_longer_open_delivers_nothing_and_marks_it_stale(Moved moved)
    {
        await using var host = await StartHostAsync();
        var parked = await SeedParkedAsync(host);
        var id = await ProposeOkAsync(host.Agent(await SeedRunAsync(host.Factory)), parked.WorkItemId, "OnSuccess");
        await MutateAsync(host, async db =>
        {
            var run = await db.LoopRuns.SingleAsync(r => r.Id == parked.RunId, Ct);
            switch (moved)
            {
                case Moved.Resumed:
                    run.Status = LoopRunStatus.Running;
                    run.HumanFeedbackReason = null;
                    break;
                case Moved.Cancelled:
                    run.Status = LoopRunStatus.Cancelled;
                    run.CompletedAt = DateTime.UtcNow;
                    break;
                case Moved.Deleted:
                    db.LoopRunNodes.RemoveRange(db.LoopRunNodes.Where(n => n.LoopRunId == run.Id));
                    db.LoopRuns.Remove(run);
                    break;
                case Moved.AnotherRun:
                    run.Status = LoopRunStatus.Cancelled;
                    run.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(Ct);
                    db.LoopRuns.Add(new LoopRun
                    {
                        Id = Guid.NewGuid(),
                        WorkItemId = parked.WorkItemId,
                        LoopTemplateVersionId = parked.VersionId,
                        Status = LoopRunStatus.Running,
                        RecoveryPolicy = RecoveryPolicy.AutoResume,
                        StartedAt = DateTime.UtcNow.AddMinutes(1),
                    });
                    break;
                default:
                    db.LoopRunNodes.Add(new LoopRunNode
                    {
                        Id = Guid.NewGuid(),
                        LoopRunId = run.Id,
                        LoopNodeId = parked.NodeId,
                        Status = LoopRunNodeStatus.WaitingHuman,
                        StartedAt = DateTime.UtcNow.AddMinutes(1),
                        CreatedAt = DateTime.UtcNow,
                    });
                    break;
            }
        });
        host.ClearHints();

        var resp = await ApproveAsync(host.Human, id);

        Assert.Equal("Stale", Status(await ConflictProposalAsync(resp, moved.ToString())));
        Assert.Equal("Stale", await StatusOfAsync(host, parked.WorkItemId, id));
        Assert.Empty(await AnswersAsync(host, parked.RunId));
        Assert.True(host.Hints(parked.WorkItemId) >= 1, "the board was not hinted");
    }

    [Fact]
    public async Task Approving_a_suggestion_that_is_not_pending_or_does_not_exist_changes_nothing()
    {
        await using var host = await StartHostAsync();
        var parked = await SeedParkedAsync(host);
        var agent = host.Agent(await SeedRunAsync(host.Factory));
        var superseded = await ProposeOkAsync(agent, parked.WorkItemId, "OnFailure");
        var rejected = await ProposeOkAsync(agent, parked.WorkItemId, Rework);
        Assert.Equal(HttpStatusCode.OK, (await RejectAsync(host.Human, rejected, "No.")).StatusCode);
        var approved = await ProposeOkAsync(agent, parked.WorkItemId, "OnSuccess");
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(host.Human, approved)).StatusCode);
        host.ClearHints();

        foreach (var (id, status) in new[] { (superseded, "Superseded"), (rejected, "Rejected"), (approved, "Approved") })
        {
            Assert.Equal(status, Status(await ConflictProposalAsync(await ApproveAsync(host.Human, id), $"approving a {status} one")));
            Assert.Equal(status, Status(await ConflictProposalAsync(await RejectAsync(host.Human, id, "Again."), $"rejecting a {status} one")));
            Assert.Equal(status, await StatusOfAsync(host, parked.WorkItemId, id));
        }

        Assert.Single(await AnswersAsync(host, parked.RunId));
        Assert.Equal(0, host.Hints(parked.WorkItemId));
        var unknown = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await ApproveAsync(host.Human, unknown.ToString())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await RejectAsync(host.Human, unknown.ToString(), null)).StatusCode);
    }

    [Fact]
    public async Task An_approve_that_loses_the_claim_to_another_approver_delivers_nothing()
    {
        await using var host = await StartHostAsync();
        var parked = await SeedParkedAsync(host);
        var id = await ProposeOkAsync(host.Agent(await SeedRunAsync(host.Factory)), parked.WorkItemId, "OnSuccess");
        host.Race.Arm();

        var resp = await ApproveAsync(host.Human, id);

        Assert.Equal("Approved", Status(await ConflictProposalAsync(resp, "approving after another approver claimed it")));
        Assert.Empty(await AnswersAsync(host, parked.RunId));
        Assert.Equal(LoopRunStatus.WaitingHuman, (await RunAsync(host, parked.RunId)).Status);
    }

    [Fact]
    public async Task A_delivery_that_fails_unexpectedly_leaves_the_suggestion_stale_not_approved()
    {
        await using var host = await StartHostAsync();
        var parked = await SeedParkedAsync(host);
        var id = await ProposeOkAsync(host.Agent(await SeedRunAsync(host.Factory)), parked.WorkItemId, "OnSuccess");
        host.ClearHints();
        host.Race.ArmDeliveryFailure();

        try { Assert.Equal(HttpStatusCode.InternalServerError, (await ApproveAsync(host.Human, id)).StatusCode); }
        catch (InvalidOperationException) { }

        Assert.Equal("Stale", await StatusOfAsync(host, parked.WorkItemId, id));
        Assert.Empty(await AnswersAsync(host, parked.RunId));
        Assert.Equal(LoopRunStatus.WaitingHuman, (await RunAsync(host, parked.RunId)).Status);
        Assert.True(host.Hints(parked.WorkItemId) >= 1, "the board was not hinted");
    }

    // ── Rejecting ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Too vague.", "Too vague.")]
    [InlineData("  Too vague.  ", "Too vague.")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public async Task Rejecting_keeps_the_trimmed_reason_and_who_decided_and_delivers_nothing(string? reason, string? stored)
    {
        await using var host = await StartHostAsync();
        var parked = await SeedParkedAsync(host);
        var id = await ProposeOkAsync(host.Agent(await SeedRunAsync(host.Factory)), parked.WorkItemId, "OnSuccess");
        host.ClearHints();

        var resp = await RejectAsync(host.Human, id, reason);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var listed = await ReadAsync(host, parked.WorkItemId, id);
        Assert.Equal("Rejected", Status(listed));
        Assert.Equal(stored, IsNullOrAbsent(listed, "rejectionReason") ? null : listed.GetProperty("rejectionReason").GetString());
        Assert.Equal(host.Factory.AdminUsername, listed.GetProperty("decidedBy").GetString());
        Assert.False(IsNullOrAbsent(listed, "decidedAt"));
        Assert.Empty(await AnswersAsync(host, parked.RunId));
        Assert.Equal(LoopRunStatus.WaitingHuman, (await RunAsync(host, parked.RunId)).Status);
        Assert.True(host.Hints(parked.WorkItemId) >= 1, "the board was not hinted");
    }

    [Fact]
    public async Task A_rejection_reason_over_2000_characters_is_a_400_and_leaves_it_pending()
    {
        await using var host = await StartHostAsync();
        var parked = await SeedParkedAsync(host);
        var id = await ProposeOkAsync(host.Agent(await SeedRunAsync(host.Factory)), parked.WorkItemId, "OnSuccess");

        var resp = await RejectAsync(host.Human, id, new string('r', 2001));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("Pending", await StatusOfAsync(host, parked.WorkItemId, id));
    }

    // ── Answers that close the visit ──────────────────────────────────────

    [Fact]
    public async Task A_button_answer_makes_every_other_pending_suggestion_for_that_visit_stale()
    {
        await using var host = await StartHostAsync();
        var parked = await SeedParkedAsync(host);
        var other = await SeedParkedAsync(host);
        var run = host.Agent(await SeedRunAsync(host.Factory));
        var chat = host.Agent(chatSessionId: await SeedChatSessionAsync(host.Factory));
        var byRun = await ProposeOkAsync(run, parked.WorkItemId, "OnSuccess");
        var byChat = await ProposeOkAsync(chat, parked.WorkItemId, "OnFailure");
        var elsewhere = await ProposeOkAsync(run, other.WorkItemId, "OnSuccess");
        host.ClearHints();

        await PressButtonAsync(host, parked, Rework, "Do this instead.");

        Assert.Equal("Stale", await StatusOfAsync(host, parked.WorkItemId, byRun));
        Assert.Equal("Stale", await StatusOfAsync(host, parked.WorkItemId, byChat));
        Assert.Equal("Pending", await StatusOfAsync(host, other.WorkItemId, elsewhere));
        Assert.True(host.Hints(parked.WorkItemId) >= 1, "the board was not hinted");
        Assert.Equal(0, host.Hints(other.WorkItemId));
        Assert.Equal("Stale", Status(await ConflictProposalAsync(await ApproveAsync(host.Human, byRun), "approving after the button")));
        Assert.Single(await AnswersAsync(host, parked.RunId));
    }

    public enum Left { Cancelled, Retried, Deleted }

    [Theory]
    [InlineData(Left.Cancelled)]
    [InlineData(Left.Retried)]
    [InlineData(Left.Deleted)]
    public async Task A_run_that_leaves_the_question_unanswered_makes_its_pending_suggestions_stale(Left left)
    {
        await using var host = await StartHostAsync();
        var parked = await SeedParkedAsync(host);
        var other = await SeedParkedAsync(host);
        var byRun = await ProposeOkAsync(host.Agent(await SeedRunAsync(host.Factory)), parked.WorkItemId, "OnSuccess");
        var byChat = await ProposeOkAsync(host.Agent(chatSessionId: await SeedChatSessionAsync(host.Factory)), parked.WorkItemId, Rework);
        var elsewhere = await ProposeOkAsync(host.Agent(await SeedRunAsync(host.Factory)), other.WorkItemId, "OnSuccess");
        host.ClearHints();

        var resp = left switch
        {
            Left.Cancelled => await host.Human.PostAsync($"/api/v1/loopruns/{parked.RunId}/cancel", null, Ct),
            Left.Retried => await host.Human.PostAsync($"/api/v1/loopruns/{parked.RunId}/nodes/{parked.RunNodeId}/retry", null, Ct),
            _ => await host.Human.DeleteAsync($"/api/v1/loopruns/{parked.RunId}", Ct),
        };

        Assert.True(resp.IsSuccessStatusCode, $"{left}: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(Ct)}");
        Assert.Equal("Stale", await StatusOfAsync(host, parked.WorkItemId, byRun));
        Assert.Equal("Stale", await StatusOfAsync(host, parked.WorkItemId, byChat));
        Assert.Equal("Pending", await StatusOfAsync(host, other.WorkItemId, elsewhere));
        Assert.True(host.Hints(parked.WorkItemId) >= 1, "the board was not hinted");
    }

    [Fact]
    public async Task An_approved_suggestion_makes_the_other_pending_ones_for_that_visit_stale()
    {
        await using var host = await StartHostAsync();
        var parked = await SeedParkedAsync(host);
        var byRun = await ProposeOkAsync(host.Agent(await SeedRunAsync(host.Factory)), parked.WorkItemId, "OnSuccess");
        var byChat = await ProposeOkAsync(host.Agent(chatSessionId: await SeedChatSessionAsync(host.Factory)), parked.WorkItemId, Rework);

        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(host.Human, byRun)).StatusCode);

        Assert.Equal("Approved", await StatusOfAsync(host, parked.WorkItemId, byRun));
        Assert.Equal("Stale", await StatusOfAsync(host, parked.WorkItemId, byChat));
        Assert.Equal("Stale", Status(await ConflictProposalAsync(await ApproveAsync(host.Human, byChat), "approving the other one")));
        Assert.Single(await AnswersAsync(host, parked.RunId));
    }
}
