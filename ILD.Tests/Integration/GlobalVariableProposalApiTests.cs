using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
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
using Moq;

namespace ILD.Tests.Integration;

/// <summary>
/// Global Variable Proposals ("suggestions") through ILD's two surfaces: an agent
/// (a loop run or a chat) reads the variables and suggests a whole new value for
/// one, and only a signed-in human approves or rejects it. Approve applies the
/// text only while the variable is as it was when the suggestion was made.
/// Agent calls are made the way the MCP server makes them: the agent service
/// token plus the session header.
/// </summary>
public class GlobalVariableProposalApiTests
{
    private const string RunHeader = "X-ILD-Run-Id";
    private const string ChatHeader = "X-ILD-Chat-Session-Id";
    private const string VariablesUrl = "/api/v1/global-variables";
    private const string ProposalsUrl = "/api/v1/global-variables/proposals";
    private const string AgentVariablesUrl = "/api/v1/agent/global-variables";
    private const string AgentProposalsUrl = "/api/v1/agent/global-variables/proposals";
    private const string LimitUrl = "/api/v1/settings/globalVariables.maxLength";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Host : IAsyncDisposable
    {
        public ApiFactory Factory { get; }
        public Mock<IWorkItemNotifier> WorkItemNotifier { get; } = new();
        public Mock<IChatNotifier> ChatNotifier { get; } = new();
        public RaceBeforeVariableWrite Race { get; } = new();
        public HttpClient Human { get; private set; } = null!;
        public Guid RepositoryId { get; private set; }

        public Host()
        {
            Factory = new ApiFactory(configureServices: services =>
            {
                services.ReplaceSingleton(WorkItemNotifier.Object);
                services.ReplaceSingleton(ChatNotifier.Object);
                var reclaimer = new Mock<IRunReclaimer>();
                reclaimer.Setup(r => r.ReclaimLocalStateAsync(It.IsAny<LoopRun>())).ReturnsAsync(true);
                services.ReplaceSingleton(reclaimer.Object);
                services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(Race));
            });
        }

        public async Task<Host> StartAsync()
        {
            Human = await Factory.CreateAuthenticatedClientAsync();
            RepositoryId = await SeedRepositoryAsync(Factory);
            return this;
        }

        public HttpClient Agent(Guid? runId = null, Guid? chatSessionId = null)
        {
            var client = Factory.CreateClient();
            var token = Factory.Services.GetRequiredService<ILD.Api.Configuration.AgentAuthTokenProvider>().Token;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (runId is { } run) client.DefaultRequestHeaders.Add(RunHeader, run.ToString());
            if (chatSessionId is { } chat) client.DefaultRequestHeaders.Add(ChatHeader, chat.ToString());
            return client;
        }

        public void ClearHints()
        {
            WorkItemNotifier.Invocations.Clear();
            ChatNotifier.Invocations.Clear();
        }

        /// <summary>The board was hinted that suggestions changed, whatever the event is called.</summary>
        public bool BoardHinted()
            => WorkItemNotifier.Invocations.Any(i => i.Method.Name.Contains("GlobalVariable", StringComparison.Ordinal));

        /// <summary>The chat was hinted that its suggestions changed.</summary>
        public bool ChatHinted(Guid chatId)
            => ChatNotifier.Invocations.Any(i =>
                i.Method.Name.Contains("GlobalVariable", StringComparison.Ordinal) && i.Arguments.Contains(chatId));

        public async ValueTask DisposeAsync() => await Factory.DisposeAsync();
    }

    /// <summary>
    /// Plays a second writer that commits a change to the variable at the last
    /// moment: just before the first statement that writes a GlobalVariables row
    /// runs, so after anything an approve may have read. Armed per approve.
    /// </summary>
    internal sealed class RaceBeforeVariableWrite : DbCommandInterceptor
    {
        private static readonly Regex VariableWrite = new(
            @"(^|;)\s*(UPDATE|INSERT\s+INTO)\s+""?GlobalVariables""?[\s(]",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);

        private string? _armed;

        public bool Raced { get; private set; }

        public void Arm(string sql)
        {
            Raced = false;
            _armed = sql;
        }

        private void RaceIfVariableWrite(DbCommand command)
        {
            if (!VariableWrite.IsMatch(command.CommandText)) return;
            var sql = Interlocked.Exchange(ref _armed, null);
            if (sql is null) return;
            using var race = command.Connection!.CreateCommand();
            race.Transaction = command.Transaction;
            race.CommandText = sql;
            race.ExecuteNonQuery();
            Raced = true;
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            RaceIfVariableWrite(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            RaceIfVariableWrite(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            RaceIfVariableWrite(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            RaceIfVariableWrite(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            RaceIfVariableWrite(command);
            return base.ScalarExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            RaceIfVariableWrite(command);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static async Task<Host> StartHostAsync() => await new Host().StartAsync();

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp)
        => JsonDocument.Parse(await resp.Content.ReadAsStringAsync(Ct)).RootElement;

    private static bool IsNullOrAbsent(JsonElement obj, string property)
        => !obj.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null;

    private static string Status(JsonElement proposal) => proposal.GetProperty("status").GetString()!;

    private static async Task<JsonElement> CreateVariableAsync(Host host, string name, string value)
    {
        var resp = await host.Human.PostAsJsonAsync(VariablesUrl, new { name, value }, Ct);
        Assert.True(resp.StatusCode == HttpStatusCode.Created,
            $"create {name}: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(Ct)}");
        return await ReadJsonAsync(resp);
    }

    private static async Task<JsonElement[]> VariablesAsync(Host host)
    {
        var resp = await host.Human.GetAsync(VariablesUrl, Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await ReadJsonAsync(resp)).EnumerateArray().ToArray();
    }

    private static async Task<JsonElement?> VariableNamedAsync(Host host, string name)
        => (await VariablesAsync(host)).Cast<JsonElement?>()
            .SingleOrDefault(v => string.Equals(v!.Value.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase));

    private static async Task SetVariableUpdatedAtAsync(Host host, string name, DateTime updatedAt)
    {
        using var scope = host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var normalized = GlobalVariable.Normalize(name);
        var row = await db.GlobalVariables.SingleAsync(g => g.NormalizedName == normalized, Ct);
        row.UpdatedAt = updatedAt;
        await db.SaveChangesAsync(Ct);
    }

    private static Task<HttpResponseMessage> ProposeAsync(HttpClient agent, string name, string value, string rationale = "It reads better.")
        => agent.PostAsJsonAsync(AgentProposalsUrl, new { name, value, rationale }, Ct);

    private static Task<HttpResponseMessage> ProposeRawAsync(HttpClient agent, string json)
        => agent.PostAsync(AgentProposalsUrl, new StringContent(json, Encoding.UTF8, "application/json"), Ct);

    private static async Task<string> ProposeOkAsync(HttpClient agent, string name, string value, string rationale = "It reads better.")
    {
        var resp = await ProposeAsync(agent, name, value, rationale);
        Assert.True(resp.StatusCode == HttpStatusCode.Created,
            $"propose {name}: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(Ct)}");
        var json = await ReadJsonAsync(resp);
        Assert.Equal("Pending", Status(json));
        return json.GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement[]> ListAsHumanAsync(Host host, string query = "")
    {
        var resp = await host.Human.GetAsync(ProposalsUrl + query, Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await ReadJsonAsync(resp)).EnumerateArray().ToArray();
    }

    private static async Task<JsonElement> ReadProposalAsync(Host host, string id)
        => Assert.Single(await ListAsHumanAsync(host), p => p.GetProperty("id").GetString() == id);

    private static async Task<JsonElement[]> ListAsAgentAsync(HttpClient agent)
    {
        var resp = await agent.GetAsync(AgentProposalsUrl, Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await ReadJsonAsync(resp)).EnumerateArray().ToArray();
    }

    private static async Task<int> PendingCountAsync(Host host)
    {
        var resp = await host.Human.GetAsync($"{ProposalsUrl}/pending-count", Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await ReadJsonAsync(resp)).GetProperty("count").GetInt32();
    }

    private static Task<HttpResponseMessage> ApproveAsync(HttpClient client, string id)
        => client.PostAsync($"{ProposalsUrl}/{id}/approve", null, Ct);

    private static Task<HttpResponseMessage> RejectAsync(HttpClient client, string id, string? reason)
        => client.PostAsJsonAsync($"{ProposalsUrl}/{id}/reject", new { reason }, Ct);

    private static async Task<JsonElement> ConflictAsync(HttpResponseMessage resp, string because)
    {
        Assert.True(resp.StatusCode == HttpStatusCode.Conflict,
            $"{because}: expected 409, got {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(Ct)}");
        var body = await ReadJsonAsync(resp);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()), $"{because}: the 409 gives no reason");
        return body.GetProperty("proposal");
    }

    public enum Caller { LoopRun, ChatSession }

    private static async Task<(Guid? RunId, Guid? ChatId)> SeedCallerAsync(Host host, Caller caller)
        => caller == Caller.LoopRun
            ? (await SeedRunAsync(host.Factory), null)
            : (null, await SeedChatSessionAsync(host.Factory));

    // ── Reading the variables ─────────────────────────────────────────────

    [Fact]
    public async Task get_global_variables_gives_every_variable_by_name_ignoring_case_to_the_agent_token_alone()
    {
        await using var host = await StartHostAsync();
        await CreateVariableAsync(host, "zeta", "last");
        await CreateVariableAsync(host, "Alpha", "first");
        await CreateVariableAsync(host, "mid", "");
        var stamped = (await VariablesAsync(host)).ToDictionary(
            v => v.GetProperty("name").GetString()!, v => v.GetProperty("updatedAt").GetDateTime());

        var resp = await host.Agent().GetAsync(AgentVariablesUrl, Ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var listed = (await ReadJsonAsync(resp)).EnumerateArray().ToArray();
        Assert.Equal(new[] { "Alpha", "mid", "zeta" }, listed.Select(v => v.GetProperty("name").GetString()));
        Assert.Equal(new[] { "first", "", "last" }, listed.Select(v => v.GetProperty("value").GetString()));
        Assert.All(listed, v => Assert.Equal(stamped[v.GetProperty("name").GetString()!], v.GetProperty("updatedAt").GetDateTime()));
    }

    // ── Making a suggestion ───────────────────────────────────────────────

    [Theory]
    [InlineData(Caller.LoopRun)]
    [InlineData(Caller.ChatSession)]
    public async Task A_suggestion_snapshots_its_variable_and_the_caller_reads_back_only_its_own_newest_first(Caller caller)
    {
        await using var host = await StartHostAsync();
        await CreateVariableAsync(host, "rules", "Keep it short.");
        var snapshot = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        await SetVariableUpdatedAtAsync(host, "rules", snapshot);
        var rulesUpdatedAt = (await VariableNamedAsync(host, "rules"))!.Value.GetProperty("updatedAt").GetDateTime();
        var (runId, chatId) = await SeedCallerAsync(host, caller);
        var agent = host.Agent(runId, chatId);
        var other = host.Agent(await SeedRunAsync(host.Factory));
        await ProposeOkAsync(other, "rules", "Another source's text.");

        var resp = await ProposeAsync(agent, "  RULES  ", "Keep it short.\nName the file.", "Reviews keep missing the file.");

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var created = await ReadJsonAsync(resp);
        var existingId = created.GetProperty("id").GetString()!;
        Assert.Equal("RULES", created.GetProperty("name").GetString());
        Assert.Equal("Pending", Status(created));
        var newId = await ProposeOkAsync(agent, "brand_new", "", "Nothing says this yet.");

        var asHuman = await ReadProposalAsync(host, existingId);
        Assert.Equal(rulesUpdatedAt, asHuman.GetProperty("snapshotUpdatedAt").GetDateTime());
        Assert.True(asHuman.GetProperty("variableExists").GetBoolean());
        Assert.Equal("Keep it short.", asHuman.GetProperty("currentValue").GetString());
        var missing = await ReadProposalAsync(host, newId);
        Assert.True(IsNullOrAbsent(missing, "snapshotUpdatedAt"));
        Assert.False(missing.GetProperty("variableExists").GetBoolean());
        Assert.Equal("Keep it short.", (await VariableNamedAsync(host, "rules"))!.Value.GetProperty("value").GetString());
        Assert.Null(await VariableNamedAsync(host, "brand_new"));

        var own = await ListAsAgentAsync(agent);
        Assert.Equal(new[] { newId, existingId }, own.Select(p => p.GetProperty("id").GetString()));
        var first = own[1];
        Assert.Equal("RULES", first.GetProperty("name").GetString());
        Assert.Equal("Keep it short.\nName the file.", first.GetProperty("value").GetString());
        Assert.Equal("Reviews keep missing the file.", first.GetProperty("rationale").GetString());
        Assert.Equal("Pending", Status(first));
        Assert.True(IsNullOrAbsent(first, "rejectionReason"));
        Assert.True(IsNullOrAbsent(first, "decidedAt"));
        Assert.False(IsNullOrAbsent(first, "createdAt"));
        Assert.Equal("", own[0].GetProperty("value").GetString());
    }

    public static TheoryData<string, string> InvalidSuggestions => new()
    {
        { "no name", """{"value":"x","rationale":"r"}""" },
        { "blank name", """{"name":"   ","value":"x","rationale":"r"}""" },
        { "name starting with a digit", """{"name":"9lives","value":"x","rationale":"r"}""" },
        { "name with a hyphen", """{"name":"bad-name","value":"x","rationale":"r"}""" },
        { "name with a dot", """{"name":"Global.rules","value":"x","rationale":"r"}""" },
        { "name over 128 characters", $$"""{"name":"a{{new string('b', 128)}}","value":"x","rationale":"r"}""" },
        { "no value", """{"name":"rules","rationale":"r"}""" },
        { "null value", """{"name":"rules","value":null,"rationale":"r"}""" },
        { "no rationale", """{"name":"rules","value":"x"}""" },
        { "blank rationale", """{"name":"rules","value":"x","rationale":"  "}""" },
        { "rationale over 2000", $$"""{"name":"rules","value":"x","rationale":"{{new string('r', 2001)}}"}""" },
    };

    [Theory]
    [MemberData(nameof(InvalidSuggestions))]
    public async Task An_invalid_suggestion_is_a_400_that_stores_nothing(string _, string json)
    {
        await using var host = await StartHostAsync();
        var agent = host.Agent(await SeedRunAsync(host.Factory));

        var resp = await ProposeRawAsync(agent, json);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Empty(await ListAsHumanAsync(host));
    }

    public enum SessionHeader { None, Unparseable, UnknownRun, UnknownChat }

    [Theory]
    [InlineData(SessionHeader.None, HttpStatusCode.BadRequest)]
    [InlineData(SessionHeader.Unparseable, HttpStatusCode.BadRequest)]
    [InlineData(SessionHeader.UnknownRun, HttpStatusCode.Forbidden)]
    [InlineData(SessionHeader.UnknownChat, HttpStatusCode.Forbidden)]
    public async Task A_suggestion_needs_a_session_that_exists(SessionHeader header, HttpStatusCode expected)
    {
        await using var host = await StartHostAsync();
        var agent = host.Agent(
            runId: header == SessionHeader.UnknownRun ? Guid.NewGuid() : null,
            chatSessionId: header == SessionHeader.UnknownChat ? Guid.NewGuid() : null);
        if (header == SessionHeader.Unparseable)
            agent.DefaultRequestHeaders.Add(RunHeader, "not-a-guid");

        var resp = await ProposeAsync(agent, "rules", "x");

        Assert.Equal(expected, resp.StatusCode);
        Assert.Empty(await ListAsHumanAsync(host));
    }

    [Theory]
    [InlineData(4000, 4812, "4,812 / 4,000 characters")]
    [InlineData(1000, 1001, "1,001 / 1,000 characters")]
    public async Task A_value_over_the_length_setting_is_refused_stating_its_length_and_the_limit(int limit, int length, string stated)
    {
        await using var host = await StartHostAsync();
        using (var set = await host.Human.PutAsJsonAsync(LimitUrl, new { value = limit.ToString() }, Ct))
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var agent = host.Agent(await SeedRunAsync(host.Factory));

        var resp = await ProposeAsync(agent, "rules", new string('v', length));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var error = (await ReadJsonAsync(resp)).GetProperty("error").GetString()!;
        Assert.Contains(stated, error);
        Assert.Contains("consolidate", error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await ListAsHumanAsync(host));

        await ProposeOkAsync(agent, "rules", new string('v', limit));
    }

    [Fact]
    public async Task A_new_suggestion_from_the_same_source_for_the_same_variable_supersedes_only_its_earlier_pending_one()
    {
        await using var host = await StartHostAsync();
        await CreateVariableAsync(host, "rules", "Keep it short.");
        var runId = await SeedRunAsync(host.Factory);
        var chatId = await SeedChatSessionAsync(host.Factory);
        var run = host.Agent(runId);
        var chat = host.Agent(chatSessionId: chatId);

        var runFirst = await ProposeOkAsync(run, "rules", "Run's first text.");
        var rejectedEarlier = await ProposeOkAsync(run, "style", "Plain words.");
        Assert.Equal(HttpStatusCode.OK, (await RejectAsync(host.Human, rejectedEarlier, "No.")).StatusCode);
        var chatsOwn = await ProposeOkAsync(chat, "rules", "Chat's text.");
        var runOther = await ProposeOkAsync(run, "brand_new", "Run's other variable.");
        var chatFirst = await ProposeOkAsync(chat, "brand_new", "Chat's first new text.");

        var runSecond = await ProposeOkAsync(run, "Rules", "Run's second text.");
        var styleAgain = await ProposeOkAsync(run, "style", "Plainer words.");
        var chatSecond = await ProposeOkAsync(chat, "BRAND_NEW", "Chat's second new text.");

        var superseded = await ReadProposalAsync(host, runFirst);
        Assert.Equal("Superseded", Status(superseded));
        Assert.False(IsNullOrAbsent(superseded, "decidedAt"));
        Assert.Equal("Superseded", Status(await ReadProposalAsync(host, chatFirst)));
        foreach (var id in new[] { runSecond, chatsOwn, runOther, styleAgain, chatSecond })
            Assert.Equal("Pending", Status(await ReadProposalAsync(host, id)));
        Assert.Equal("Rejected", Status(await ReadProposalAsync(host, rejectedEarlier)));
        Assert.Equal(5, await PendingCountAsync(host));
    }

    [Fact]
    public async Task Making_a_suggestion_hints_the_board_and_the_chat_that_made_it()
    {
        await using var host = await StartHostAsync();
        var chatId = await SeedChatSessionAsync(host.Factory);
        host.ClearHints();

        await ProposeOkAsync(host.Agent(chatSessionId: chatId), "rules", "New text.");

        Assert.True(host.BoardHinted(), "the board was not hinted");
        Assert.True(host.ChatHinted(chatId), "the chat was not hinted");
    }

    // ── Approving and rejecting ───────────────────────────────────────────

    [Fact]
    public async Task Approving_writes_exactly_the_suggested_text_and_makes_the_other_pending_suggestions_for_it_stale()
    {
        await using var host = await StartHostAsync();
        await CreateVariableAsync(host, "rules", "Keep it short.");
        var longAgo = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await SetVariableUpdatedAtAsync(host, "rules", longAgo);
        var otherChatId = await SeedChatSessionAsync(host.Factory);
        var approved = await ProposeOkAsync(host.Agent(await SeedRunAsync(host.Factory)), "rules", "Keep it short.\nName the file.");
        var competing = await ProposeOkAsync(host.Agent(chatSessionId: otherChatId), "RULES", "Write at length.");
        var newVariable = await ProposeOkAsync(host.Agent(await SeedRunAsync(host.Factory)), "Fresh_Rules", "Brand new text.");
        // Approve re-checks nothing about length: the limit applied when the suggestion was made.
        using (var set = await host.Human.PutAsJsonAsync(LimitUrl, new { value = "5" }, Ct))
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        host.ClearHints();

        // Approve takes no text: anything sent with it is ignored.
        var resp = await host.Human.PostAsJsonAsync($"{ProposalsUrl}/{approved}/approve", new { value = "Tampered." }, Ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        Assert.Equal("Approved", Status(body.GetProperty("proposal")));
        Assert.False(IsNullOrAbsent(body.GetProperty("proposal"), "decidedAt"));
        Assert.Equal("Keep it short.\nName the file.", body.GetProperty("variable").GetProperty("value").GetString());
        var rules = (await VariableNamedAsync(host, "rules"))!.Value;
        Assert.Equal("rules", rules.GetProperty("name").GetString());
        Assert.Equal("Keep it short.\nName the file.", rules.GetProperty("value").GetString());
        Assert.True(rules.GetProperty("updatedAt").GetDateTime() > longAgo.AddDays(1), "approve did not stamp the variable");
        Assert.Equal("Stale", Status(await ReadProposalAsync(host, competing)));
        Assert.Equal("Pending", Status(await ReadProposalAsync(host, newVariable)));
        Assert.True(host.BoardHinted(), "the board was not hinted");
        Assert.True(host.ChatHinted(otherChatId), "the chat whose suggestion went stale was not hinted");

        var stale = await ConflictAsync(await ApproveAsync(host.Human, competing), "approving the stale one");
        Assert.Equal("Stale", Status(stale));
        Assert.Equal("Keep it short.\nName the file.", (await VariableNamedAsync(host, "rules"))!.Value.GetProperty("value").GetString());

        var created = await ApproveAsync(host.Human, newVariable);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var fresh = (await VariableNamedAsync(host, "fresh_rules"))!.Value;
        Assert.Equal("Fresh_Rules", fresh.GetProperty("name").GetString());
        Assert.Equal("Brand new text.", fresh.GetProperty("value").GetString());
        Assert.Equal(0, await PendingCountAsync(host));
    }

    public enum RacingWrite { EditedByAHuman, DeletedByAHuman, CreatedByAHumanSinceMissing }

    /// <summary>
    /// The other writer commits just before approve's own write runs, after
    /// anything approve read: only a compare-and-set in that write sees it.
    /// </summary>
    [Theory]
    [InlineData(RacingWrite.EditedByAHuman)]
    [InlineData(RacingWrite.DeletedByAHuman)]
    [InlineData(RacingWrite.CreatedByAHumanSinceMissing)]
    public async Task An_approve_raced_by_another_write_to_the_variable_applies_nothing_and_goes_stale(RacingWrite race)
    {
        await using var host = await StartHostAsync();
        var agent = host.Agent(await SeedRunAsync(host.Factory));
        string id;
        if (race == RacingWrite.CreatedByAHumanSinceMissing)
        {
            id = await ProposeOkAsync(agent, "Fresh_Rules", "The agent's text.");
            host.Race.Arm($"""
                INSERT INTO "GlobalVariables" ("Id", "Name", "NormalizedName", "Value", "UpdatedAt")
                VALUES ('{Guid.NewGuid().ToString().ToUpperInvariant()}', 'fresh_rules', 'FRESH_RULES', 'The human''s text.', '2031-01-01 00:00:00')
                """);
        }
        else
        {
            await CreateVariableAsync(host, "rules", "Keep it short.");
            id = await ProposeOkAsync(agent, "rules", "The agent's text.");
            host.Race.Arm(race == RacingWrite.EditedByAHuman
                ? """UPDATE "GlobalVariables" SET "Value" = 'The human''s text.', "UpdatedAt" = '2031-01-01 00:00:00' WHERE "NormalizedName" = 'RULES'"""
                : """DELETE FROM "GlobalVariables" WHERE "NormalizedName" = 'RULES'""");
        }

        var resp = await ApproveAsync(host.Human, id);

        Assert.True(host.Race.Raced, "approve wrote no GlobalVariables row, so the race never ran");
        var proposal = await ConflictAsync(resp, "approving after the variable changed");
        Assert.Equal("Stale", Status(proposal));
        Assert.Equal("Stale", Status(await ReadProposalAsync(host, id)));
        var variable = await VariableNamedAsync(host, race == RacingWrite.CreatedByAHumanSinceMissing ? "fresh_rules" : "rules");
        if (race == RacingWrite.DeletedByAHuman)
            Assert.Null(variable);
        else
            Assert.Equal("The human's text.", variable!.Value.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Rejecting_keeps_a_trimmed_reason_and_leaves_the_variable_alone()
    {
        await using var host = await StartHostAsync();
        await CreateVariableAsync(host, "rules", "Keep it short.");
        var chatId = await SeedChatSessionAsync(host.Factory);
        var agent = host.Agent(chatSessionId: chatId);
        var withReason = await ProposeOkAsync(agent, "rules", "Write at length.");
        var blankReason = await ProposeOkAsync(agent, "style", "Plain words.");
        var tooLong = await ProposeOkAsync(agent, "tone", "Warm.");
        host.ClearHints();

        var resp = await RejectAsync(host.Human, withReason, "  Too long, keep it short.  ");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(host.BoardHinted(), "the board was not hinted");
        Assert.True(host.ChatHinted(chatId), "the chat was not hinted");
        Assert.Equal(HttpStatusCode.OK, (await RejectAsync(host.Human, blankReason, "   ")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await RejectAsync(host.Human, tooLong, new string('r', 2001))).StatusCode);

        Assert.Equal("Keep it short.", (await VariableNamedAsync(host, "rules"))!.Value.GetProperty("value").GetString());
        var own = (await ListAsAgentAsync(agent)).ToDictionary(p => p.GetProperty("id").GetString()!);
        Assert.Equal("Rejected", Status(own[withReason]));
        Assert.Equal("Too long, keep it short.", own[withReason].GetProperty("rejectionReason").GetString());
        Assert.False(IsNullOrAbsent(own[withReason], "decidedAt"));
        Assert.Equal("Rejected", Status(own[blankReason]));
        Assert.True(IsNullOrAbsent(own[blankReason], "rejectionReason"));
        Assert.Equal("Pending", Status(own[tooLong]));
    }

    [Fact]
    public async Task Deciding_a_suggestion_that_is_no_longer_pending_is_a_409_and_an_unknown_one_a_404()
    {
        await using var host = await StartHostAsync();
        var agent = host.Agent(await SeedRunAsync(host.Factory));
        var rejected = await ProposeOkAsync(agent, "rules", "First.");
        Assert.Equal(HttpStatusCode.OK, (await RejectAsync(host.Human, rejected, "No.")).StatusCode);
        var superseded = await ProposeOkAsync(agent, "style", "First.");
        await ProposeOkAsync(agent, "style", "Second.");

        foreach (var (id, status) in new[] { (rejected, "Rejected"), (superseded, "Superseded") })
        {
            Assert.Equal(status, Status(await ConflictAsync(await ApproveAsync(host.Human, id), $"approving a {status} one")));
            Assert.Equal(status, Status(await ConflictAsync(await RejectAsync(host.Human, id, "Again."), $"rejecting a {status} one")));
        }
        Assert.Equal("No.", (await ReadProposalAsync(host, rejected)).GetProperty("rejectionReason").GetString());
        Assert.Empty(await VariablesAsync(host));

        Assert.Equal(HttpStatusCode.NotFound, (await ApproveAsync(host.Human, Guid.NewGuid().ToString())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await RejectAsync(host.Human, Guid.NewGuid().ToString(), null)).StatusCode);
    }

    public enum HumanWrite { Edit, Delete, Create }

    [Theory]
    [InlineData(HumanWrite.Edit)]
    [InlineData(HumanWrite.Delete)]
    [InlineData(HumanWrite.Create)]
    public async Task A_human_writing_a_variable_on_the_page_makes_its_pending_suggestions_stale(HumanWrite write)
    {
        await using var host = await StartHostAsync();
        string? variableId = null;
        if (write != HumanWrite.Create)
            variableId = (await CreateVariableAsync(host, "rules", "Keep it short.")).GetProperty("id").GetString();
        await CreateVariableAsync(host, "style", "Plain words.");
        var chatId = await SeedChatSessionAsync(host.Factory);
        var fromRun = await ProposeOkAsync(host.Agent(await SeedRunAsync(host.Factory)), "rules", "The run's text.");
        var fromChat = await ProposeOkAsync(host.Agent(chatSessionId: chatId), "RULES", "The chat's text.");
        var otherVariable = await ProposeOkAsync(host.Agent(await SeedRunAsync(host.Factory)), "style", "Plainer words.");
        host.ClearHints();

        var resp = write switch
        {
            HumanWrite.Edit => await host.Human.PutAsJsonAsync($"{VariablesUrl}/{variableId}", new { value = "The human's text." }, Ct),
            HumanWrite.Delete => await host.Human.DeleteAsync($"{VariablesUrl}/{variableId}", Ct),
            _ => await host.Human.PostAsJsonAsync(VariablesUrl, new { name = "Rules", value = "The human's text." }, Ct),
        };

        Assert.True(resp.IsSuccessStatusCode, $"the human's write answered {(int)resp.StatusCode}");
        Assert.Equal("Stale", Status(await ReadProposalAsync(host, fromRun)));
        Assert.Equal("Stale", Status(await ReadProposalAsync(host, fromChat)));
        Assert.Equal("Pending", Status(await ReadProposalAsync(host, otherVariable)));
        Assert.Equal(1, await PendingCountAsync(host));
        Assert.True(host.BoardHinted(), "the board was not hinted");
        Assert.True(host.ChatHinted(chatId), "the chat whose suggestion went stale was not hinted");
    }

    // ── Where a suggestion came from ──────────────────────────────────────

    [Fact]
    public async Task The_human_lists_suggestions_by_status_work_item_or_chat_each_naming_its_source_and_anchor()
    {
        await using var host = await StartHostAsync();
        var (runOfA, stepOfA) = await SeedRunWithRunningStepAsync(host.Factory, "wi-a");
        var secondRunOfA = await SeedRunAsync(host.Factory, "wi-a", LoopRunStatus.Completed);
        var runOfB = await SeedRunAsync(host.Factory, "wi-b");
        var ownChat = await SeedChatSessionAsync(host.Factory, owner: host.Factory.AdminUsername);
        await SeedChatMessagesAsync(host.Factory, ownChat, "user", "assistant", "user");
        var othersChat = await SeedChatSessionAsync(host.Factory, owner: "someone-else");

        var fromA = await ProposeOkAsync(host.Agent(runOfA), "rules", "A's text.");
        var fromSecondA = await ProposeOkAsync(host.Agent(secondRunOfA), "style", "A's second run's text.");
        Assert.Equal(HttpStatusCode.OK, (await RejectAsync(host.Human, fromSecondA, "No.")).StatusCode);
        var fromB = await ProposeOkAsync(host.Agent(runOfB), "rules", "B's text.");
        var fromOwnChat = await ProposeOkAsync(host.Agent(chatSessionId: ownChat), "rules", "Own chat's text.");
        var fromOthersChat = await ProposeOkAsync(host.Agent(chatSessionId: othersChat), "rules", "Other user's chat's text.");

        static string[] Ids(JsonElement[] list) => list.Select(p => p.GetProperty("id").GetString()!).OrderBy(i => i).ToArray();
        static string[] Sorted(params string[] ids) => ids.OrderBy(i => i).ToArray();
        Assert.Equal(Sorted(fromA, fromSecondA), Ids(await ListAsHumanAsync(host, "?workItemId=wi-a")));
        Assert.Equal(Sorted(fromOwnChat), Ids(await ListAsHumanAsync(host, $"?chatSessionId={ownChat}")));
        Assert.Equal(Sorted(fromA, fromB, fromOwnChat, fromOthersChat), Ids(await ListAsHumanAsync(host, "?status=Pending")));

        var all = (await ListAsHumanAsync(host)).ToDictionary(p => p.GetProperty("id").GetString()!);
        Assert.Equal(5, all.Count);

        var a = all[fromA];
        Assert.Equal(runOfA.ToString(), a.GetProperty("createdByLoopRunId").GetString());
        Assert.Equal(stepOfA.ToString(), a.GetProperty("createdByRunNodeId").GetString());
        Assert.True(IsNullOrAbsent(a, "createdByChatSessionId"));
        Assert.True(IsNullOrAbsent(a, "chatReplySequence"));
        Assert.Equal("run", a.GetProperty("source").GetProperty("kind").GetString());
        Assert.Equal("wi-a", a.GetProperty("source").GetProperty("workItemId").GetString());
        Assert.Equal("wi-b", all[fromB].GetProperty("source").GetProperty("workItemId").GetString());

        var own = all[fromOwnChat];
        Assert.Equal(ownChat.ToString(), own.GetProperty("createdByChatSessionId").GetString());
        Assert.Equal(3, own.GetProperty("chatReplySequence").GetInt32());
        Assert.Equal("chat", own.GetProperty("source").GetProperty("kind").GetString());
        Assert.Equal(ownChat.ToString(), own.GetProperty("source").GetProperty("chatSessionId").GetString());
        Assert.True(own.GetProperty("source").GetProperty("linkable").GetBoolean());

        var others = all[fromOthersChat].GetProperty("source");
        Assert.Equal("chat", others.GetProperty("kind").GetString());
        Assert.False(others.GetProperty("linkable").GetBoolean());
    }

    public enum SourceDeletion { AUserDeletesTheRun, ItsWorkItemIsDeleted, AUserDeletesTheChat }

    [Theory]
    [InlineData(SourceDeletion.AUserDeletesTheRun)]
    [InlineData(SourceDeletion.ItsWorkItemIsDeleted)]
    [InlineData(SourceDeletion.AUserDeletesTheChat)]
    public async Task Deleting_the_source_leaves_its_suggestions_pending_and_decidable_from_the_page(SourceDeletion deletion)
    {
        await using var host = await StartHostAsync();
        await CreateVariableAsync(host, "rules", "Keep it short.");
        var itemId = await CreateHumanItemAsync(host);
        var runId = await SeedRunAsync(host.Factory, itemId);
        var chatId = await SeedChatSessionAsync(host.Factory, owner: host.Factory.AdminUsername);
        var agent = deletion == SourceDeletion.AUserDeletesTheChat ? host.Agent(chatSessionId: chatId) : host.Agent(runId);
        var toApprove = await ProposeOkAsync(agent, "rules", "The agent's text.");
        var toReject = await ProposeOkAsync(agent, "style", "Plain words.");
        await SetRunStatusAsync(host.Factory, runId, LoopRunStatus.Completed);

        var resp = deletion switch
        {
            SourceDeletion.AUserDeletesTheRun => await host.Human.DeleteAsync($"/api/v1/loopruns/{runId}", Ct),
            SourceDeletion.ItsWorkItemIsDeleted => await host.Human.DeleteAsync($"/api/v1/workitems/{itemId}", Ct),
            _ => await host.Human.DeleteAsync($"/api/v1/chat/{chatId}", Ct),
        };

        Assert.True(resp.IsSuccessStatusCode, $"delete answered {(int)resp.StatusCode}");
        Assert.False(await SourceExistsAsync(host.Factory, deletion == SourceDeletion.AUserDeletesTheChat ? null : runId,
            deletion == SourceDeletion.AUserDeletesTheChat ? chatId : null), "the source was not deleted");
        foreach (var id in new[] { toApprove, toReject })
        {
            var left = await ReadProposalAsync(host, id);
            Assert.Equal("Pending", Status(left));
            Assert.Equal("deleted", left.GetProperty("source").GetProperty("kind").GetString());
        }
        Assert.Equal(2, await PendingCountAsync(host));

        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(host.Human, toApprove)).StatusCode);
        Assert.Equal("The agent's text.", (await VariableNamedAsync(host, "rules"))!.Value.GetProperty("value").GetString());
        Assert.Equal(HttpStatusCode.OK, (await RejectAsync(host.Human, toReject, "No.")).StatusCode);
        Assert.Equal("Rejected", Status(await ReadProposalAsync(host, toReject)));
    }

    // ── The agent cannot decide ───────────────────────────────────────────

    [Fact]
    public async Task The_agent_token_cannot_reach_the_human_routes_and_has_no_route_that_decides_or_deletes()
    {
        await using var host = await StartHostAsync();
        var runId = await SeedRunAsync(host.Factory);
        var agent = host.Agent(runId);
        var id = await ProposeOkAsync(agent, "rules", "The agent's text.");

        var humanRoutes = new[]
        {
            await agent.GetAsync(ProposalsUrl, Ct),
            await agent.GetAsync($"{ProposalsUrl}/pending-count", Ct),
            await ApproveAsync(agent, id),
            await RejectAsync(agent, id, "Self-rejected."),
        };
        foreach (var resp in humanRoutes)
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        var agentAttempts = new[]
        {
            await agent.PostAsync($"{AgentProposalsUrl}/{id}/approve", null, Ct),
            await agent.PostAsJsonAsync($"{AgentProposalsUrl}/{id}/reject", new { reason = "Self-rejected." }, Ct),
            await agent.DeleteAsync($"{AgentProposalsUrl}/{id}", Ct),
            await agent.PutAsJsonAsync($"{AgentProposalsUrl}/{id}", new { status = "Approved" }, Ct),
        };
        foreach (var resp in agentAttempts)
            Assert.False(resp.IsSuccessStatusCode, $"{resp.RequestMessage!.Method} {resp.RequestMessage.RequestUri} answered {(int)resp.StatusCode}");

        Assert.Equal("Pending", Status(await ReadProposalAsync(host, id)));
        Assert.Empty(await VariablesAsync(host));
    }

    // ── Seeding ───────────────────────────────────────────────────────────

    private static async Task<string> CreateHumanItemAsync(Host host)
    {
        var resp = await host.Human.PostAsJsonAsync("/api/v1/workitems", new
        {
            title = "An item",
            description = "",
            repositoryId = host.RepositoryId.ToString(),
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await ReadJsonAsync(resp)).GetProperty("id").GetString()!;
    }

    private static async Task SetRunStatusAsync(ApiFactory factory, Guid runId, LoopRunStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.LoopRuns.SingleAsync(r => r.Id == runId, Ct);
        run.Status = status;
        run.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<bool> SourceExistsAsync(ApiFactory factory, Guid? runId, Guid? chatId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return runId is { } run
            ? await db.LoopRuns.AnyAsync(r => r.Id == run, Ct)
            : await db.ChatSessions.AnyAsync(c => c.Id == chatId, Ct);
    }

    private static async Task SeedChatMessagesAsync(ApiFactory factory, Guid chatId, params string[] roles)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        for (var i = 0; i < roles.Length; i++)
        {
            db.ChatMessages.Add(new ChatMessage
            {
                Id = Guid.NewGuid(),
                ChatSessionId = chatId,
                Role = roles[i],
                Content = $"message {i}",
                Sequence = i,
                CreatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<(Guid RunId, Guid RunningNodeId)> SeedRunWithRunningStepAsync(ApiFactory factory, string workItemId)
    {
        var runId = await SeedRunAsync(factory, workItemId);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var versionId = await db.LoopRuns.Where(r => r.Id == runId).Select(r => r.LoopTemplateVersionId).SingleAsync(Ct);
        var node = new LoopNode
        {
            Id = Guid.NewGuid(),
            LoopTemplateVersionId = versionId,
            NodeType = NodeType.AI,
            Label = "ai",
            CreatedAt = DateTime.UtcNow,
        };
        db.LoopNodes.Add(node);
        var step = new LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = runId,
            LoopNodeId = node.Id,
            Status = LoopRunNodeStatus.Running,
            StartedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        };
        db.LoopRunNodes.Add(step);
        await db.SaveChangesAsync(Ct);
        return (runId, step.Id);
    }

    private static async Task<Guid> SeedChatSessionAsync(ApiFactory factory, string owner = "suggestion-tester")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = new ChatSession
        {
            Id = Guid.NewGuid(),
            UserId = owner,
            AiProviderId = Guid.NewGuid(),
            ProviderType = "claude-code",
            ToolAllowlistCsv = "ild",
            ScratchPath = Path.Combine(Path.GetTempPath(), "ild-test-gv-chat", Guid.NewGuid().ToString("N")),
            CreatedAt = DateTime.UtcNow,
        };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync(Ct);
        return session.Id;
    }

    private static async Task<Guid> SeedRunAsync(ApiFactory factory, string? workItemId = null, LoopRunStatus status = LoopRunStatus.Running)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = $"suggestions-{Guid.NewGuid():N}" };
        db.LoopTemplates.Add(template);
        var version = new LoopTemplateVersion
        {
            Id = Guid.NewGuid(),
            LoopTemplateId = template.Id,
            VersionNumber = 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.LoopTemplateVersions.Add(version);
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItemId ?? Guid.NewGuid().ToString(),
            LoopTemplateVersionId = version.Id,
            Status = status,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            StartedAt = DateTime.UtcNow,
        };
        db.LoopRuns.Add(run);
        await db.SaveChangesAsync(Ct);
        return run.Id;
    }

    private static async Task<Guid> SeedRepositoryAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var provider = new RemoteProvider
        {
            Id = Guid.NewGuid(),
            Name = "test-provider",
            Type = "forgejo",
            Url = "https://example.invalid",
            CreatedAt = DateTime.UtcNow,
        };
        db.RemoteProviders.Add(provider);
        var repo = new Repository
        {
            Id = Guid.NewGuid(),
            Name = "repo",
            CloneUrl = "https://example.invalid/repo.git",
            RemoteProviderId = provider.Id,
            DefaultIntakeStatus = WorkItemStatus.Backlog,
            CreatedAt = DateTime.UtcNow,
        };
        db.Repositories.Add(repo);
        await db.SaveChangesAsync(Ct);
        return repo.Id;
    }
}
