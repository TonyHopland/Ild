using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using static ILD.Tests.Integration.ProposalApiTestSupport;

namespace ILD.Tests.Integration;

/// <summary>
/// Loop Template Proposals through ILD's two surfaces: an agent (a loop run or a
/// chat) reads a template with get_loop_template, edits the whole document and
/// proposes it against the version it read, which must be the latest; only a
/// signed-in human saves it as a new version, rejects it, or saves it from the
/// Loop Editor after reviewing it. Any new version makes the template's other
/// pending proposals Stale. Agent calls are made the way the MCP server makes
/// them: the agent service token plus the session header.
/// </summary>
// A host per test: template names and the proposal list are host-wide, and the race interceptor is armed per host.
public class LoopTemplateProposalApiTests
{
    private const string TemplatesUrl = "/api/v1/looptemplates";
    private const string ProposalsUrl = "/api/v1/loop-template-proposals";
    private const string AgentDocumentUrl = "/api/v1/agent/loop-templates/document";
    private const string AgentProposalsUrl = "/api/v1/agent/loop-templates/proposals";
    private const string V2 = "ild-loop-template/v2";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Host() : ProposalApiTestHost<RaceBeforeVersionInsert>("LoopTemplateProposal");

    /// <summary>
    /// Plays a second save that commits a new version at the last moment: just
    /// before the first statement that inserts a LoopTemplateVersions row runs,
    /// so after anything a save may have read. Armed per save.
    /// </summary>
    internal sealed class RaceBeforeVersionInsert : DbCommandInterceptor
    {
        private static readonly Regex VersionInsert = new(
            @"(^|;)\s*INSERT\s+INTO\s+""?LoopTemplateVersions""?[\s(]",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);

        private string? _armed;

        public bool Raced { get; private set; }

        public void Arm(string sql)
        {
            Raced = false;
            _armed = sql;
        }

        private void RaceIfVersionInsert(DbCommand command)
        {
            if (!VersionInsert.IsMatch(command.CommandText)) return;
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
            RaceIfVersionInsert(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            RaceIfVersionInsert(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            RaceIfVersionInsert(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            RaceIfVersionInsert(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            RaceIfVersionInsert(command);
            return base.ScalarExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            RaceIfVersionInsert(command);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static async Task<Host> StartHostAsync()
    {
        var host = new Host();
        await host.StartAsync();
        return host;
    }

    private static string Id(JsonElement proposal) => proposal.GetProperty("id").GetString()!;

    // ── Templates ─────────────────────────────────────────────────────────

    /// <summary>Start → Build → Test → Cleanup, every step a command.</summary>
    private static object Graph(string buildCommand = "make") => new
    {
        nodes = new object[]
        {
            new { id = "start", type = "Start", label = "Start", config = new { } },
            new { id = "build", type = "Cmd", label = "Build", config = new { command = buildCommand } },
            new { id = "test", type = "Cmd", label = "Test", config = new { command = "make test" } },
            new { id = "cleanup", type = "Cleanup", label = "Cleanup", config = new { } },
        },
        edges = new object[]
        {
            new { id = "e1", sourceNodeId = "start", targetNodeId = "build", edgeType = "OnSuccess", name = (string?)null },
            new { id = "e2", sourceNodeId = "build", targetNodeId = "test", edgeType = "OnSuccess", name = (string?)null },
            new { id = "e3", sourceNodeId = "test", targetNodeId = "cleanup", edgeType = "OnSuccess", name = (string?)null },
        },
    };

    private static object SaveBody(string name, string description, string recoveryPolicy, object graph)
    {
        var body = JsonSerializer.SerializeToNode(graph)!.AsObject();
        body["name"] = name;
        body["description"] = description;
        body["recoveryPolicy"] = recoveryPolicy;
        return body;
    }

    private static async Task<Guid> CreateTemplateAsync(Host host, string name, string description = "A loop.")
    {
        var resp = await host.Human.PostAsJsonAsync(TemplatesUrl, SaveBody(name, description, "AutoResume", Graph()), Ct);
        Assert.True(resp.StatusCode == HttpStatusCode.Created,
            $"create {name}: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(Ct)}");
        return (await ReadJsonAsync(resp)).GetProperty("id").GetGuid();
    }

    /// <summary>A plain Loop Editor save: a new version, naming no proposal.</summary>
    private static async Task PlainSaveAsync(Host host, Guid templateId, string buildCommand)
    {
        var template = await TemplateAsync(host, templateId);
        var resp = await host.Human.PutAsJsonAsync($"{TemplatesUrl}/{templateId}", SaveBody(
            template.GetProperty("name").GetString()!,
            template.GetProperty("description").GetString()!,
            template.GetProperty("recoveryPolicy").GetString()!,
            Graph(buildCommand)), Ct);
        Assert.True(resp.IsSuccessStatusCode, $"save: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(Ct)}");
    }

    private static async Task<JsonElement> TemplateAsync(Host host, Guid templateId)
    {
        var resp = await host.Human.GetAsync($"{TemplatesUrl}/{templateId}", Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await ReadJsonAsync(resp);
    }

    private static async Task<int[]> VersionNumbersAsync(Host host, Guid templateId)
    {
        using var scope = host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.LoopTemplateVersions.Where(v => v.LoopTemplateId == templateId)
            .OrderBy(v => v.VersionNumber).Select(v => v.VersionNumber).ToArrayAsync(Ct);
    }

    /// <summary>What get_loop_template gives the agent: the latest version and its whole document.</summary>
    private static async Task<(int Version, JsonObject Document)> ReadDocumentAsync(HttpClient agent, string nameOrId)
    {
        var resp = await agent.GetAsync($"{AgentDocumentUrl}?nameOrId={Uri.EscapeDataString(nameOrId)}", Ct);
        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"get_loop_template: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(Ct)}");
        var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync(Ct))!.AsObject();
        return (body["version"]!.GetValue<int>(), body["document"]!.AsObject());
    }

    private static JsonObject NodeLabelled(JsonObject document, string label)
        => document["nodes"]!.AsArray().Select(n => n!.AsObject()).Single(n => n["label"]!.GetValue<string>() == label);

    /// <summary>The template's latest document with the Build step's command changed.</summary>
    private static async Task<(int Version, JsonObject Document)> EditedDocumentAsync(HttpClient agent, Guid templateId, string buildCommand)
    {
        var (version, document) = await ReadDocumentAsync(agent, templateId.ToString());
        NodeLabelled(document, "Build")["config"]!["command"] = buildCommand;
        return (version, document);
    }

    private static Task<HttpResponseMessage> ProposeAsync(HttpClient agent, string nameOrId, int baseVersion, string document, string rationale = "Builds keep failing.")
        => agent.PostAsJsonAsync(AgentProposalsUrl, new { nameOrId, baseVersion, document, rationale }, Ct);

    private static Task<HttpResponseMessage> ProposeRawAsync(HttpClient agent, string json)
        => agent.PostAsync(AgentProposalsUrl, new StringContent(json, Encoding.UTF8, "application/json"), Ct);

    private static async Task<string> ProposeOkAsync(HttpClient agent, string nameOrId, int baseVersion, JsonObject document, string rationale = "Builds keep failing.")
    {
        var resp = await ProposeAsync(agent, nameOrId, baseVersion, document.ToJsonString(), rationale);
        Assert.True(resp.StatusCode == HttpStatusCode.Created,
            $"propose for {nameOrId}: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(Ct)}");
        var json = await ReadJsonAsync(resp);
        Assert.Equal("Pending", Status(json));
        return Id(json);
    }

    /// <summary>Reads the template's latest version and proposes it with the Build step's command changed.</summary>
    private static async Task<string> ProposeBuildCommandAsync(HttpClient agent, Guid templateId, string buildCommand, string rationale = "Builds keep failing.")
    {
        var (version, document) = await EditedDocumentAsync(agent, templateId, buildCommand);
        return await ProposeOkAsync(agent, templateId.ToString(), version, document, rationale);
    }

    private static async Task<JsonElement[]> ListAsHumanAsync(Host host, string query = "")
    {
        var resp = await host.Human.GetAsync(ProposalsUrl + query, Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await ReadJsonAsync(resp)).EnumerateArray().ToArray();
    }

    private static async Task<JsonElement> ReadProposalAsync(Host host, string id)
    {
        var resp = await host.Human.GetAsync($"{ProposalsUrl}/{id}", Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await ReadJsonAsync(resp);
    }

    /// <summary>The proposal's stored document; the view carries it as JSON text.</summary>
    private static JsonObject StoredDocument(JsonElement proposal)
        => JsonNode.Parse(proposal.GetProperty("document").GetString()!)!.AsObject();

    private static async Task<JsonElement[]> ListAsAgentAsync(HttpClient agent)
    {
        var resp = await agent.GetAsync(AgentProposalsUrl, Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await ReadJsonAsync(resp)).EnumerateArray().ToArray();
    }

    private static async Task<int> PendingCountAsync(Host host, Guid templateId)
    {
        var resp = await host.Human.GetAsync($"{ProposalsUrl}/pending-counts", Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var counts = await ReadJsonAsync(resp);
        var match = counts.EnumerateObject().Where(p => Guid.TryParse(p.Name, out var id) && id == templateId).ToList();
        return match.Count == 0 ? 0 : match.Single().Value.GetInt32();
    }

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, string id)
        => client.PostAsync($"{ProposalsUrl}/{id}/save", null, Ct);

    private static Task<HttpResponseMessage> RejectAsync(HttpClient client, string id, string? reason)
        => client.PostAsJsonAsync($"{ProposalsUrl}/{id}/reject", new { reason }, Ct);

    /// <summary>A Loop Editor save of <paramref name="document"/>, naming the proposal it was reviewed from.</summary>
    private static async Task<JsonElement> SaveFromReviewAsync(Host host, Guid templateId, JsonObject document, string? fromProposalId)
    {
        var body = new JsonObject
        {
            ["name"] = document["name"]!.DeepClone(),
            ["description"] = document["description"]!.DeepClone(),
            ["recoveryPolicy"] = document["recoveryPolicy"]!.DeepClone(),
            ["nodes"] = document["nodes"]!.DeepClone(),
            ["edges"] = document["edges"]!.DeepClone(),
            ["fromProposalId"] = fromProposalId,
        };
        var resp = await host.Human.PutAsJsonAsync($"{TemplatesUrl}/{templateId}", body, Ct);
        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"save from review: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(Ct)}");
        return await ReadJsonAsync(resp);
    }

    /// <summary>A node as a save compares it: type, label and config without layout.</summary>
    private static string NodeKey(JsonNode node)
    {
        var config = node["config"]?.DeepClone() as JsonObject ?? new JsonObject();
        config.Remove("__pos");
        var sorted = new JsonObject(config.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));
        return $"{node["type"]!.GetValue<string>()}|{node["label"]!.GetValue<string>()}|{sorted.ToJsonString()}";
    }

    /// <summary>The template's latest graph and <paramref name="document"/> hold the same nodes and edges, ids aside.</summary>
    private static void AssertSameGraph(JsonObject document, JsonElement template)
    {
        var saved = JsonNode.Parse(template.GetRawText())!.AsObject();
        Assert.Equal(
            document["nodes"]!.AsArray().Select(n => NodeKey(n!)).OrderBy(k => k, StringComparer.Ordinal),
            saved["nodes"]!.AsArray().Select(n => NodeKey(n!)).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(EdgeKeys(document), EdgeKeys(saved));
    }

    private static string[] EdgeKeys(JsonObject graph)
    {
        var labels = graph["nodes"]!.AsArray().ToDictionary(n => n!["id"]!.GetValue<string>(), n => n!["label"]!.GetValue<string>());
        return graph["edges"]!.AsArray()
            .Select(e => $"{labels[e!["sourceNodeId"]!.GetValue<string>()]}->{labels[e["targetNodeId"]!.GetValue<string>()]}|{e["edgeType"]!.GetValue<string>()}|{e["name"]?.GetValue<string>()}")
            .OrderBy(k => k, StringComparer.Ordinal).ToArray();
    }

    // ── Proposing ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Caller.LoopRun)]
    [InlineData(Caller.ChatSession)]
    public async Task A_proposal_against_the_latest_version_is_stored_pending_changes_nothing_and_the_caller_reads_back_only_its_own(Caller caller)
    {
        await using var host = await StartHostAsync();
        var templateId = await CreateTemplateAsync(host, "Release Loop", "Ships a release.");
        await PlainSaveAsync(host, templateId, "make all");
        var otherTemplateId = await CreateTemplateAsync(host, "Docs Loop");
        var (runId, chatId) = await SeedCallerAsync(host, caller);
        var agent = host.Agent(runId, chatId);
        var other = host.Agent(await SeedRunAsync(host.Factory));
        await ProposeBuildCommandAsync(other, templateId, "make other");
        var (version, document) = await EditedDocumentAsync(agent, templateId, "make -j4");
        Assert.Equal(2, version);
        document["name"] = "Release Loop Renamed";
        host.ClearHints();

        var resp = await ProposeAsync(agent, "  release LOOP  ", 2, document.ToJsonString(), "Builds are slow.");

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var created = await ReadJsonAsync(resp);
        var firstId = Id(created);
        Assert.Equal(templateId, created.GetProperty("templateId").GetGuid());
        Assert.Equal(2, created.GetProperty("baseVersion").GetInt32());
        Assert.Equal("Pending", Status(created));
        Assert.True(host.BoardHinted(templateId), "the board was not hinted");
        if (chatId is { } chat) Assert.True(host.ChatHinted(chat), "the chat was not hinted");

        Assert.Equal(new[] { 1, 2 }, await VersionNumbersAsync(host, templateId));
        var template = await TemplateAsync(host, templateId);
        Assert.Equal("Release Loop", template.GetProperty("name").GetString());
        Assert.Equal("Ships a release.", template.GetProperty("description").GetString());
        Assert.Equal("AutoResume", template.GetProperty("recoveryPolicy").GetString());
        Assert.Equal("make all", NodeLabelled(JsonNode.Parse(template.GetRawText())!.AsObject(), "Build")["config"]!["command"]!.GetValue<string>());

        var asHuman = await ReadProposalAsync(host, firstId);
        Assert.Equal("Release Loop", asHuman.GetProperty("templateName").GetString());
        Assert.True(asHuman.GetProperty("templateExists").GetBoolean());
        Assert.Equal(2, asHuman.GetProperty("baseVersion").GetInt32());
        Assert.Equal(2, asHuman.GetProperty("latestVersion").GetInt32());
        Assert.Equal("Builds are slow.", asHuman.GetProperty("rationale").GetString());
        var stored = StoredDocument(asHuman);
        Assert.Equal(V2, stored["$schema"]!.GetValue<string>());
        Assert.Equal("Release Loop Renamed", stored["name"]!.GetValue<string>());
        Assert.Equal("make -j4", NodeLabelled(stored, "Build")["config"]!["command"]!.GetValue<string>());

        var (otherVersion, otherDocument) = await ReadDocumentAsync(agent, otherTemplateId.ToString());
        var secondId = await ProposeOkAsync(agent, otherTemplateId.ToString(), otherVersion, otherDocument, "Nothing changes, on purpose.");

        var own = await ListAsAgentAsync(agent);
        Assert.Equal(new[] { secondId, firstId }, own.Select(Id));
        var first = own[1];
        Assert.Equal(templateId, first.GetProperty("templateId").GetGuid());
        Assert.Equal("Release Loop", first.GetProperty("templateName").GetString());
        Assert.Equal(2, first.GetProperty("baseVersion").GetInt32());
        Assert.Equal("Builds are slow.", first.GetProperty("rationale").GetString());
        Assert.Equal("Pending", Status(first));
        Assert.False(first.GetProperty("editedBeforeSave").GetBoolean());
        Assert.True(IsNullOrAbsent(first, "appliedVersion"));
        Assert.True(IsNullOrAbsent(first, "rejectionReason"));
        Assert.True(IsNullOrAbsent(first, "decidedAt"));
        Assert.False(IsNullOrAbsent(first, "createdAt"));
    }

    [Fact]
    public async Task A_template_is_found_as_get_loop_template_finds_it_and_an_unknown_or_ambiguous_one_stores_nothing()
    {
        await using var host = await StartHostAsync();
        var agent = host.Agent(await SeedRunAsync(host.Factory));
        var archivedId = await CreateTemplateAsync(host, "Archived Loop");
        Assert.Equal(HttpStatusCode.NoContent, (await host.Human.PostAsync($"{TemplatesUrl}/{archivedId}/archive", null, Ct)).StatusCode);
        var (archivedVersion, archivedDocument) = await ReadDocumentAsync(agent, archivedId.ToString());
        await CreateTemplateAsync(host, "Twin Loop");
        await CreateTemplateAsync(host, " twin loop ");
        var (twinVersion, twinDocument) = await ReadDocumentAsync(agent, (await ListTemplateIdsNamedAsync(host, "Twin Loop")).First().ToString());

        var unknown = await ProposeAsync(agent, "No Such Loop", 1, twinDocument.ToJsonString());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var ambiguous = await ConflictAsync(await ProposeAsync(agent, "TWIN LOOP", twinVersion, twinDocument.ToJsonString()), "an ambiguous name");
        Assert.Equal(2, ambiguous.GetProperty("candidates").GetArrayLength());
        Assert.Empty(await ListAsHumanAsync(host));

        var byName = await ProposeOkAsync(agent, "archived loop", archivedVersion, archivedDocument);
        Assert.Equal(archivedId.ToString(), (await ReadProposalAsync(host, byName)).GetProperty("templateId").GetString());
    }

    [Fact]
    public async Task Get_loop_template_returns_the_document_escaped_as_the_template_document_renderer_writes_it()
    {
        await using var host = await StartHostAsync();
        var graph = JsonSerializer.SerializeToNode(Graph())!.AsObject();
        graph["nodes"]!.AsArray()[2] = new JsonObject
        {
            ["id"] = "test", ["type"] = "Prompt", ["label"] = "Review",
            ["config"] = new JsonObject { ["prompt"] = "Don't <stop> & add 1+1 — café" },
        };
        var created = await host.Human.PostAsJsonAsync(TemplatesUrl, SaveBody("Escaped Loop", "A loop.", "AutoResume", graph), Ct);
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"create: {(int)created.StatusCode} {await created.Content.ReadAsStringAsync(Ct)}");
        var templateId = (await ReadJsonAsync(created)).GetProperty("id").GetGuid();
        var agent = host.Agent(await SeedRunAsync(host.Factory));

        var resp = await agent.GetAsync($"{AgentDocumentUrl}?nameOrId={templateId}", Ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var document = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("document");
        Assert.Equal(JsonValueKind.Object, document.ValueKind);
        Assert.Contains(@"""prompt"":""Don\u0027t \u003Cstop\u003E \u0026 add 1\u002B1 \u2014 caf\u00E9""", document.GetRawText());
    }

    private static async Task<Guid[]> ListTemplateIdsNamedAsync(Host host, string name)
    {
        using var scope = host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.LoopTemplates.AsNoTracking().ToListAsync(Ct))
            .Where(t => string.Equals(t.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Id).ToArray();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task A_proposal_against_any_version_but_the_latest_is_refused_naming_the_latest(int baseVersion)
    {
        await using var host = await StartHostAsync();
        var templateId = await CreateTemplateAsync(host, "Release Loop");
        await PlainSaveAsync(host, templateId, "make two");
        await PlainSaveAsync(host, templateId, "make three");
        var agent = host.Agent(await SeedRunAsync(host.Factory));
        var (_, document) = await EditedDocumentAsync(agent, templateId, "make four");

        var body = await ConflictAsync(await ProposeAsync(agent, templateId.ToString(), baseVersion, document.ToJsonString()), "an old base version");

        Assert.Equal(3, body.GetProperty("latestVersion").GetInt32());
        var error = body.GetProperty("error").GetString()!;
        Assert.Contains("3", error);
        Assert.Contains("get_loop_template", error);
        Assert.Empty(await ListAsHumanAsync(host));
    }

    public enum Invalid { UnknownPolicy, BlankRationale }

    [Theory]
    [InlineData(Invalid.UnknownPolicy)]
    [InlineData(Invalid.BlankRationale)]
    public async Task An_invalid_proposal_is_a_400_with_a_reason_that_stores_nothing(Invalid invalid)
    {
        await using var host = await StartHostAsync();
        var templateId = await CreateTemplateAsync(host, "Release Loop");
        var agent = host.Agent(await SeedRunAsync(host.Factory));
        var (version, document) = await EditedDocumentAsync(agent, templateId, "make -j4");
        var rationale = "Builds are slow.";
        switch (invalid)
        {
            case Invalid.UnknownPolicy: document["recoveryPolicy"] = "Sometimes"; break;
            case Invalid.BlankRationale: rationale = "  \t "; break;
        }
        var text = document.ToJsonString();

        var resp = await ProposeAsync(agent, templateId.ToString(), version, text, rationale);

        Assert.True(resp.StatusCode == HttpStatusCode.BadRequest, $"expected 400, got {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(Ct)}");
        Assert.False(string.IsNullOrWhiteSpace(await resp.Content.ReadAsStringAsync(Ct)), "the 400 gives no reason");
        Assert.Empty(await ListAsHumanAsync(host));
        Assert.Equal(new[] { 1 }, await VersionNumbersAsync(host, templateId));
    }

    [Fact]
    public async Task A_graph_a_save_would_refuse_returns_every_error_the_save_gives_and_stores_nothing()
    {
        await using var host = await StartHostAsync();
        var templateId = await CreateTemplateAsync(host, "Release Loop");
        var agent = host.Agent(await SeedRunAsync(host.Factory));
        var (version, document) = await ReadDocumentAsync(agent, templateId.ToString());
        var cleanupId = NodeLabelled(document, "Cleanup")["id"]!.GetValue<string>();
        var nodes = document["nodes"]!.AsArray();
        nodes.Remove(nodes.Single(n => n!["id"]!.GetValue<string>() == cleanupId));
        var edges = document["edges"]!.AsArray();
        edges.Remove(edges.Single(e => e!["targetNodeId"]!.GetValue<string>() == cleanupId));
        edges.Add(new JsonObject
        {
            ["id"] = "sideways",
            ["sourceNodeId"] = NodeLabelled(document, "Build")["id"]!.GetValue<string>(),
            ["targetNodeId"] = NodeLabelled(document, "Test")["id"]!.GetValue<string>(),
            ["edgeType"] = "Sideways",
            ["name"] = null,
        });
        var validate = await host.Human.PostAsJsonAsync($"{TemplatesUrl}/validate", new JsonObject
        {
            ["nodes"] = document["nodes"]!.DeepClone(),
            ["edges"] = document["edges"]!.DeepClone(),
        }, Ct);
        var saveErrors = (await ReadJsonAsync(validate)).GetProperty("errors").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.True(saveErrors.Length >= 2, "the graph should break more than one rule");

        var resp = await ProposeAsync(agent, templateId.ToString(), version, document.ToJsonString());

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var errors = (await ReadJsonAsync(resp)).GetProperty("validationErrors").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(saveErrors.OrderBy(e => e, StringComparer.Ordinal), errors.OrderBy(e => e, StringComparer.Ordinal));
        Assert.Empty(await ListAsHumanAsync(host));
    }

    [Fact]
    public async Task A_v1_document_is_upgraded_and_stored_as_v2()
    {
        await using var host = await StartHostAsync();
        var templateId = await CreateTemplateAsync(host, "Release Loop");
        var agent = host.Agent(await SeedRunAsync(host.Factory));
        var (version, document) = await EditedDocumentAsync(agent, templateId, "make -j4");
        document["$schema"] = "ild-loop-template/v1";

        var id = await ProposeOkAsync(agent, templateId.ToString(), version, document);

        var stored = StoredDocument(await ReadProposalAsync(host, id));
        Assert.Equal(V2, stored["$schema"]!.GetValue<string>());
        Assert.Equal("make -j4", NodeLabelled(stored, "Build")["config"]!["command"]!.GetValue<string>());
    }

    public enum SessionHeader { None, UnknownRun, UnknownChat }

    [Theory]
    [InlineData(SessionHeader.None, HttpStatusCode.BadRequest)]
    [InlineData(SessionHeader.UnknownRun, HttpStatusCode.Forbidden)]
    [InlineData(SessionHeader.UnknownChat, HttpStatusCode.Forbidden)]
    public async Task A_proposal_needs_a_session_that_exists(SessionHeader header, HttpStatusCode expected)
    {
        await using var host = await StartHostAsync();
        var templateId = await CreateTemplateAsync(host, "Release Loop");
        var (version, document) = await EditedDocumentAsync(host.Agent(), templateId, "make -j4");
        var agent = host.Agent(
            runId: header == SessionHeader.UnknownRun ? Guid.NewGuid() : null,
            chatSessionId: header == SessionHeader.UnknownChat ? Guid.NewGuid() : null);

        var resp = await ProposeAsync(agent, templateId.ToString(), version, document.ToJsonString());

        Assert.Equal(expected, resp.StatusCode);
        Assert.Empty(await ListAsHumanAsync(host));
    }

    [Fact]
    public async Task A_new_proposal_from_the_same_source_for_the_same_template_supersedes_only_its_earlier_pending_one()
    {
        await using var host = await StartHostAsync();
        var release = await CreateTemplateAsync(host, "Release Loop");
        var docs = await CreateTemplateAsync(host, "Docs Loop");
        var run = host.Agent(await SeedRunAsync(host.Factory));
        var chat = host.Agent(chatSessionId: await SeedChatSessionAsync(host.Factory));

        var runFirst = await ProposeBuildCommandAsync(run, release, "make one");
        var chatFirst = await ProposeBuildCommandAsync(chat, release, "make chat one");
        var runOnDocs = await ProposeBuildCommandAsync(run, docs, "make docs");

        var runSecond = await ProposeBuildCommandAsync(run, release, "make two");
        var chatSecond = await ProposeBuildCommandAsync(chat, release, "make chat two");

        foreach (var id in new[] { runFirst, chatFirst })
        {
            var superseded = await ReadProposalAsync(host, id);
            Assert.Equal("Superseded", Status(superseded));
            Assert.False(IsNullOrAbsent(superseded, "decidedAt"));
        }
        foreach (var id in new[] { runSecond, chatSecond, runOnDocs })
            Assert.Equal("Pending", Status(await ReadProposalAsync(host, id)));
        Assert.Equal(2, await PendingCountAsync(host, release));
        Assert.Equal(1, await PendingCountAsync(host, docs));
        Assert.Equal(new[] { 1 }, await VersionNumbersAsync(host, release));
    }

    // ── Edit Lists ────────────────────────────────────────────────────────

    private static Task<HttpResponseMessage> ProposeEditsAsync(HttpClient agent, string nameOrId, int baseVersion, JsonArray edits, string rationale = "Builds keep failing.")
        => agent.PostAsJsonAsync(AgentProposalsUrl, new JsonObject
        {
            ["nameOrId"] = nameOrId,
            ["baseVersion"] = baseVersion,
            ["rationale"] = rationale,
            ["edits"] = edits,
        }, Ct);

    private static async Task<string> ProposeEditsOkAsync(HttpClient agent, Guid templateId, int baseVersion, JsonArray edits)
    {
        var resp = await ProposeEditsAsync(agent, templateId.ToString(), baseVersion, edits);
        Assert.True(resp.StatusCode == HttpStatusCode.Created,
            $"propose edits: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(Ct)}");
        var json = await ReadJsonAsync(resp);
        Assert.Equal("Pending", Status(json));
        return Id(json);
    }

    private static JsonObject EditNodeField(string nodeId, string field, string oldString, string newString) => new()
    {
        ["op"] = "editNodeField", ["nodeId"] = nodeId, ["field"] = field, ["oldString"] = oldString, ["newString"] = newString,
    };

    private static JsonObject SetNodeField(string nodeId, string field, string value) => new()
    {
        ["op"] = "setNodeField", ["nodeId"] = nodeId, ["field"] = field, ["value"] = value,
    };

    private static JsonObject EditFile(string oldString, string newString) => new()
    {
        ["op"] = "editFile", ["oldString"] = oldString, ["newString"] = newString,
    };

    private static string NodeIdLabelled(JsonObject document, string label) => NodeLabelled(document, label)["id"]!.GetValue<string>();

    [Fact]
    public async Task An_edit_list_proposal_is_stored_pending_holding_the_base_document_with_the_edits_applied_and_the_list_as_sent()
    {
        await using var host = await StartHostAsync();
        var templateId = await CreateTemplateAsync(host, "Release Loop");
        var otherTemplateId = await CreateTemplateAsync(host, "Docs Loop");
        var agent = host.Agent(await SeedRunAsync(host.Factory));
        var (version, document) = await ReadDocumentAsync(agent, templateId.ToString());
        var edits = new JsonArray
        {
            EditNodeField(NodeIdLabelled(document, "Build"), "command", "make", "make -j4"),
            SetNodeField(NodeIdLabelled(document, "Test"), "command", "make check"),
            EditFile("\"label\":\"Cleanup\"", "\"label\":\"Tidy up\""),
        };

        var id = await ProposeEditsOkAsync(agent, templateId, version, edits);

        var expected = document.DeepClone().AsObject();
        NodeLabelled(expected, "Build")["config"]!["command"] = "make -j4";
        NodeLabelled(expected, "Test")["config"]!["command"] = "make check";
        NodeLabelled(expected, "Cleanup")["label"] = "Tidy up";
        var asHuman = await ReadProposalAsync(host, id);
        Assert.True(JsonNode.DeepEquals(expected, StoredDocument(asHuman)), StoredDocument(asHuman).ToJsonString());
        Assert.True(JsonNode.DeepEquals(edits, JsonNode.Parse(asHuman.GetProperty("edits").GetRawText())), asHuman.GetProperty("edits").GetRawText());
        Assert.Equal(new[] { 1 }, await VersionNumbersAsync(host, templateId));

        var (otherVersion, otherDocument) = await ReadDocumentAsync(agent, otherTemplateId.ToString());
        var wholeId = await ProposeOkAsync(agent, otherTemplateId.ToString(), otherVersion, otherDocument);
        Assert.True(IsNullOrAbsent(await ReadProposalAsync(host, wholeId), "edits"));
        var listed = await ListAsHumanAsync(host);
        Assert.True(JsonNode.DeepEquals(edits, JsonNode.Parse(listed.Single(p => Id(p) == id).GetProperty("edits").GetRawText())));
        Assert.True(IsNullOrAbsent(listed.Single(p => Id(p) == wholeId), "edits"));
        var own = await ListAsAgentAsync(agent);
        Assert.Equal(3, own.Single(p => Id(p) == id).GetProperty("editCount").GetInt32());
        Assert.True(IsNullOrAbsent(own.Single(p => Id(p) == wholeId), "editCount"));
    }

    public enum Ambiguous { DocumentAndEdits, Neither, EmptyEdits }

    [Theory]
    [InlineData(Ambiguous.DocumentAndEdits)]
    [InlineData(Ambiguous.Neither)]
    [InlineData(Ambiguous.EmptyEdits)]
    public async Task A_proposal_needs_exactly_one_of_a_document_or_a_non_empty_edit_list(Ambiguous ambiguous)
    {
        await using var host = await StartHostAsync();
        var templateId = await CreateTemplateAsync(host, "Release Loop");
        var agent = host.Agent(await SeedRunAsync(host.Factory));
        var (version, document) = await EditedDocumentAsync(agent, templateId, "make -j4");
        var body = new JsonObject { ["nameOrId"] = templateId.ToString(), ["baseVersion"] = version, ["rationale"] = "Builds are slow." };
        if (ambiguous == Ambiguous.DocumentAndEdits) body["document"] = document.ToJsonString();
        if (ambiguous != Ambiguous.Neither)
            body["edits"] = ambiguous == Ambiguous.EmptyEdits ? new JsonArray() : new JsonArray { EditNodeField(NodeIdLabelled(document, "Build"), "command", "make", "make -j4") };

        var resp = await ProposeRawAsync(agent, body.ToJsonString());

        Assert.True(resp.StatusCode == HttpStatusCode.BadRequest, $"expected 400, got {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(Ct)}");
        Assert.False(string.IsNullOrWhiteSpace((await ReadJsonAsync(resp)).GetProperty("error").GetString()));
        Assert.Empty(await ListAsHumanAsync(host));
    }

    [Fact]
    public async Task An_edit_file_copied_from_the_get_loop_template_text_matches_escaped_characters_and_all()
    {
        await using var host = await StartHostAsync();
        var graph = JsonSerializer.SerializeToNode(Graph())!.AsObject();
        graph["nodes"]![2]!["label"] = "Bob's tests";
        var created = await host.Human.PostAsJsonAsync(TemplatesUrl, SaveBody("Escaped Loop", "A loop.", "AutoResume", graph), Ct);
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"create: {(int)created.StatusCode} {await created.Content.ReadAsStringAsync(Ct)}");
        var templateId = (await ReadJsonAsync(created)).GetProperty("id").GetGuid();
        var agent = host.Agent(await SeedRunAsync(host.Factory));
        var read = JsonDocument.Parse(await (await agent.GetAsync($"{AgentDocumentUrl}?nameOrId={templateId}", Ct)).Content.ReadAsStringAsync(Ct)).RootElement;
        const string asRead = @"""label"":""Bob\u0027s tests""";
        Assert.Contains(asRead, read.GetProperty("document").GetRawText());

        var id = await ProposeEditsOkAsync(agent, templateId, read.GetProperty("version").GetInt32(),
            [EditFile(asRead, @"""label"":""Integration tests""")]);

        var stored = StoredDocument(await ReadProposalAsync(host, id));
        Assert.Equal("make test", NodeLabelled(stored, "Integration tests")["config"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_failing_edit_is_a_400_naming_its_index_that_stores_nothing_and_supersedes_nothing()
    {
        await using var host = await StartHostAsync();
        var templateId = await CreateTemplateAsync(host, "Release Loop");
        var agent = host.Agent(await SeedRunAsync(host.Factory));
        var earlier = await ProposeBuildCommandAsync(agent, templateId, "make one");
        var (version, document) = await ReadDocumentAsync(agent, templateId.ToString());
        var testId = NodeIdLabelled(document, "Test");

        var resp = await ProposeEditsAsync(agent, templateId.ToString(), version,
        [
            EditNodeField(NodeIdLabelled(document, "Build"), "command", "make", "make -j4"),
            EditNodeField(testId, "command", "ninja", "ninja test"),
            EditFile("\"label\":\"Cleanup\"", "\"label\":\"Tidy up\""),
        ]);

        Assert.True(resp.StatusCode == HttpStatusCode.BadRequest, $"expected 400, got {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(Ct)}");
        var body = await ReadJsonAsync(resp);
        Assert.Equal(1, body.GetProperty("editIndex").GetInt32());
        Assert.Equal(
            $"edits[1] (editNodeField, node '{testId}', field 'command'): old_string was not found in field 'command' of node '{testId}'; no change made.",
            body.GetProperty("error").GetString());
        Assert.Equal(earlier, Id((await ListAsHumanAsync(host)).Single()));
        Assert.Equal("Pending", Status(await ReadProposalAsync(host, earlier)));
    }

    [Fact]
    public async Task An_edit_file_leaving_a_duplicate_key_is_a_400_naming_its_index_that_stores_nothing()
    {
        await using var host = await StartHostAsync();
        var templateId = await CreateTemplateAsync(host, "Release Loop");
        var agent = host.Agent(await SeedRunAsync(host.Factory));
        var (version, document) = await ReadDocumentAsync(agent, templateId.ToString());

        var resp = await ProposeEditsAsync(agent, templateId.ToString(), version,
        [
            EditFile("\"label\":\"Build\"", "\"label\":\"Build\",\"label\":\"B2\""),
            EditNodeField(NodeIdLabelled(document, "Test"), "command", "make test", "make check"),
        ]);

        Assert.True(resp.StatusCode == HttpStatusCode.BadRequest, $"expected 400, got {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(Ct)}");
        var body = await ReadJsonAsync(resp);
        Assert.Equal(0, body.GetProperty("editIndex").GetInt32());
        Assert.StartsWith("edits[0] (editFile): The edit produced invalid JSON:", body.GetProperty("error").GetString());
        Assert.Contains("'label'", body.GetProperty("error").GetString());
        Assert.Empty(await ListAsHumanAsync(host));
    }

    [Fact]
    public async Task An_edit_list_producing_a_document_over_the_size_limit_is_refused_as_a_whole_document_is()
    {
        await using var host = await StartHostAsync();
        var templateId = await CreateTemplateAsync(host, "Release Loop");
        var agent = host.Agent(await SeedRunAsync(host.Factory));
        var (version, document) = await ReadDocumentAsync(agent, templateId.ToString());
        var edits = new JsonArray { SetNodeField(NodeIdLabelled(document, "Build"), "notes", new string('x', 1_000_000 - 500)) };
        Assert.True(edits.ToJsonString().Length < 1_000_000, "the edit list itself is under the limit");

        var resp = await ProposeEditsAsync(agent, templateId.ToString(), version, edits);

        Assert.True(resp.StatusCode == HttpStatusCode.BadRequest, $"expected 400, got {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(Ct)}");
        Assert.Matches(@"^document is too large \(\d+ chars\); the limit is 1000000\.$", (await ReadJsonAsync(resp)).GetProperty("error").GetString());
        Assert.Empty(await ListAsHumanAsync(host));
    }

    [Fact]
    public async Task An_edit_list_making_a_graph_a_save_would_refuse_gets_the_errors_the_whole_document_gets()
    {
        await using var host = await StartHostAsync();
        var templateId = await CreateTemplateAsync(host, "Release Loop");
        var agent = host.Agent(await SeedRunAsync(host.Factory));
        var (version, document) = await ReadDocumentAsync(agent, templateId.ToString());
        var (cleanupId, testId) = (NodeIdLabelled(document, "Cleanup"), NodeIdLabelled(document, "Test"));
        var edges = document["edges"]!.AsArray();
        edges.Single(e => e!["targetNodeId"]!.GetValue<string>() == cleanupId)!["targetNodeId"] = "nowhere";
        edges.Single(e => e!["targetNodeId"]!.GetValue<string>() == testId)!["edgeType"] = "Sideways";
        var whole = await ProposeAsync(agent, templateId.ToString(), version, document.ToJsonString());
        Assert.Equal(HttpStatusCode.BadRequest, whole.StatusCode);
        var wholeErrors = (await ReadJsonAsync(whole)).GetProperty("validationErrors").EnumerateArray().Select(e => e.GetString()!).ToArray();

        var resp = await ProposeEditsAsync(agent, templateId.ToString(), version,
        [
            EditFile($"\"targetNodeId\":\"{cleanupId}\"", "\"targetNodeId\":\"nowhere\""),
            EditFile($"\"targetNodeId\":\"{testId}\",\"edgeType\":\"OnSuccess\"", $"\"targetNodeId\":\"{testId}\",\"edgeType\":\"Sideways\""),
        ]);

        Assert.True(resp.StatusCode == HttpStatusCode.BadRequest, $"expected 400, got {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(Ct)}");
        var errors = (await ReadJsonAsync(resp)).GetProperty("validationErrors").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.True(wholeErrors.Length >= 2, string.Join("; ", wholeErrors));
        Assert.Equal(wholeErrors, errors);
        Assert.Empty(await ListAsHumanAsync(host));
    }

    [Fact]
    public async Task An_edit_list_against_an_older_version_is_refused_naming_the_latest_and_never_applied_to_it()
    {
        await using var host = await StartHostAsync();
        var templateId = await CreateTemplateAsync(host, "Release Loop");
        await PlainSaveAsync(host, templateId, "make two");
        await PlainSaveAsync(host, templateId, "make three");
        var agent = host.Agent(await SeedRunAsync(host.Factory));

        var body = await ConflictAsync(
            await ProposeEditsAsync(agent, templateId.ToString(), 1, [EditFile("\"command\":\"make\"}", "\"command\":\"make -j4\"}")]),
            "an edit list against version 1");

        Assert.Equal(3, body.GetProperty("latestVersion").GetInt32());
        Assert.Contains("the latest is version 3", body.GetProperty("error").GetString());
        Assert.Empty(await ListAsHumanAsync(host));
    }

    [Fact]
    public async Task An_edit_list_proposal_supersedes_its_sources_earlier_one_saves_as_the_next_version_and_goes_stale_on_a_newer_one()
    {
        await using var host = await StartHostAsync();
        var templateId = await CreateTemplateAsync(host, "Release Loop");
        var run = host.Agent(await SeedRunAsync(host.Factory));
        var chat = host.Agent(chatSessionId: await SeedChatSessionAsync(host.Factory));
        var earlier = await ProposeBuildCommandAsync(run, templateId, "make one");
        var (_, document) = await ReadDocumentAsync(run, templateId.ToString());

        var saved = await ProposeEditsOkAsync(run, templateId, 1, [EditNodeField(NodeIdLabelled(document, "Build"), "command", "make", "make -j4")]);
        var competing = await ProposeEditsOkAsync(chat, templateId, 1, [SetNodeField(NodeIdLabelled(document, "Test"), "command", "make check")]);
        Assert.Equal("Superseded", Status(await ReadProposalAsync(host, earlier)));

        var resp = await SaveAsync(host.Human, saved);

        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"save: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(Ct)}");
        Assert.Equal("Applied", Status((await ReadJsonAsync(resp)).GetProperty("proposal")));
        Assert.Equal(new[] { 1, 2 }, await VersionNumbersAsync(host, templateId));
        var template = await TemplateAsync(host, templateId);
        AssertSameGraph(StoredDocument(await ReadProposalAsync(host, saved)), template);
        Assert.Equal("make -j4", NodeLabelled(JsonNode.Parse(template.GetRawText())!.AsObject(), "Build")["config"]!["command"]!.GetValue<string>());
        Assert.Equal("Stale", Status(await ReadProposalAsync(host, competing)));
    }

    // ── What changed ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_proposal_view_summarises_its_changes_against_the_base_version_ignoring_layout_and_edge_ids()
    {
        await using var host = await StartHostAsync();
        var templateId = await CreateTemplateAsync(host, "Release Loop", "Ships a release.");
        var agent = host.Agent(await SeedRunAsync(host.Factory));
        var (version, document) = await ReadDocumentAsync(agent, templateId.ToString());
        var start = NodeLabelled(document, "Start");
        var build = NodeLabelled(document, "Build");
        var test = NodeLabelled(document, "Test");
        var cleanup = NodeLabelled(document, "Cleanup");
        string IdOf(JsonObject node) => node["id"]!.GetValue<string>();

        build["config"]!["command"] = "make -j4";
        cleanup["config"]!["__pos"] = new JsonObject { ["x"] = 400, ["y"] = 120 };
        var nodes = document["nodes"]!.AsArray();
        nodes.Remove(test);
        nodes.Add(new JsonObject
        {
            ["id"] = "lint", ["type"] = "Cmd", ["label"] = "Lint",
            ["config"] = new JsonObject { ["command"] = "make lint" },
        });
        var edges = document["edges"]!.AsArray();
        foreach (var edge in edges.Where(e => e!["sourceNodeId"]!.GetValue<string>() == IdOf(test) || e["targetNodeId"]!.GetValue<string>() == IdOf(test)).ToList())
            edges.Remove(edge);
        edges.Single(e => e!["sourceNodeId"]!.GetValue<string>() == IdOf(start))!["id"] = "renamed-edge-id";
        edges.Add(new JsonObject { ["id"] = "n1", ["sourceNodeId"] = IdOf(build), ["targetNodeId"] = "lint", ["edgeType"] = "OnSuccess", ["name"] = null });
        edges.Add(new JsonObject { ["id"] = "n2", ["sourceNodeId"] = "lint", ["targetNodeId"] = IdOf(cleanup), ["edgeType"] = "OnSuccess", ["name"] = null });
        document["description"] = "Ships a linted release.";

        var id = await ProposeOkAsync(agent, templateId.ToString(), version, document);

        var changes = (await ReadProposalAsync(host, id)).GetProperty("changes");
        static string[] Labels(JsonElement list) => list.EnumerateArray().Select(n => n.GetProperty("label").GetString()!).ToArray();
        Assert.Equal(new[] { "Lint" }, Labels(changes.GetProperty("nodesAdded")));
        Assert.Equal("Cmd", changes.GetProperty("nodesAdded")[0].GetProperty("type").GetString());
        Assert.Equal(new[] { "Test" }, Labels(changes.GetProperty("nodesRemoved")));
        Assert.Equal("Cmd", changes.GetProperty("nodesRemoved")[0].GetProperty("type").GetString());
        var changed = Assert.Single(changes.GetProperty("nodesChanged").EnumerateArray());
        Assert.Equal("Build", changed.GetProperty("label").GetString());
        Assert.Equal("Cmd", changed.GetProperty("type").GetString());
        Assert.Contains(changed.GetProperty("fields").EnumerateArray(), f => f.GetString()!.Contains("command", StringComparison.Ordinal));
        Assert.DoesNotContain(changed.GetProperty("fields").EnumerateArray(), f => f.GetString()!.Contains("__pos", StringComparison.Ordinal));

        static string[] EdgeLabels(JsonElement list) => list.EnumerateArray()
            .Select(e => $"{e.GetProperty("sourceLabel").GetString()}->{e.GetProperty("targetLabel").GetString()}|{e.GetProperty("edgeType").GetString()}")
            .OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "Build->Lint|OnSuccess", "Lint->Cleanup|OnSuccess" }, EdgeLabels(changes.GetProperty("edgesAdded")));
        Assert.Equal(new[] { "Build->Test|OnSuccess", "Test->Cleanup|OnSuccess" }, EdgeLabels(changes.GetProperty("edgesRemoved")));
        Assert.False(changes.GetProperty("nameChanged").GetBoolean());
        Assert.True(changes.GetProperty("descriptionChanged").GetBoolean());
        Assert.False(changes.GetProperty("recoveryPolicyChanged").GetBoolean());
    }

    // ── Saving and rejecting ──────────────────────────────────────────────

    [Fact]
    public async Task Saving_creates_the_next_version_equal_to_the_proposal_and_makes_the_templates_other_pending_proposals_stale()
    {
        await using var host = await StartHostAsync();
        var templateId = await CreateTemplateAsync(host, "Release Loop", "Ships a release.");
        var otherTemplateId = await CreateTemplateAsync(host, "Docs Loop");
        var competingChat = await SeedChatSessionAsync(host.Factory);
        var agent = host.Agent(await SeedRunAsync(host.Factory));
        var (version, document) = await EditedDocumentAsync(agent, templateId, "make -j4");
        document["name"] = "Release Loop Renamed";
        document["description"] = "Ships a faster release.";
        document["recoveryPolicy"] = "NeedsReview";
        var saved = await ProposeOkAsync(agent, templateId.ToString(), version, document);
        var competing = await ProposeBuildCommandAsync(host.Agent(chatSessionId: competingChat), templateId, "make -j8");
        var elsewhere = await ProposeBuildCommandAsync(host.Agent(await SeedRunAsync(host.Factory)), otherTemplateId, "make docs");
        host.ClearHints();

        var resp = await SaveAsync(host.Human, saved);

        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"save: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(Ct)}");
        var proposal = (await ReadJsonAsync(resp)).GetProperty("proposal");
        Assert.Equal("Applied", Status(proposal));
        Assert.False(proposal.GetProperty("editedBeforeSave").GetBoolean());
        Assert.Equal(2, proposal.GetProperty("appliedVersion").GetInt32());
        Assert.False(IsNullOrAbsent(proposal, "decidedAt"));

        Assert.Equal(new[] { 1, 2 }, await VersionNumbersAsync(host, templateId));
        var template = await TemplateAsync(host, templateId);
        Assert.Equal(2, template.GetProperty("version").GetInt32());
        Assert.Equal("Release Loop Renamed", template.GetProperty("name").GetString());
        Assert.Equal("Ships a faster release.", template.GetProperty("description").GetString());
        Assert.Equal("NeedsReview", template.GetProperty("recoveryPolicy").GetString());
        AssertSameGraph(StoredDocument(await ReadProposalAsync(host, saved)), template);

        Assert.Equal("Stale", Status(await ReadProposalAsync(host, competing)));
        Assert.Equal("Pending", Status(await ReadProposalAsync(host, elsewhere)));
        Assert.Equal(0, await PendingCountAsync(host, templateId));
        Assert.Equal(1, await PendingCountAsync(host, otherTemplateId));
        Assert.True(host.BoardHinted(templateId), "the board was not hinted");
        Assert.True(host.ChatHinted(competingChat), "the chat whose proposal went stale was not hinted");

        var stale = await ConflictAsync(await SaveAsync(host.Human, competing), "saving the stale one");
        Assert.Equal("Stale", Status(stale.GetProperty("proposal")));
        await ConflictAsync(await RejectAsync(host.Human, competing, "No."), "rejecting the stale one");
        Assert.Equal(new[] { 1, 2 }, await VersionNumbersAsync(host, templateId));
        Assert.Equal("Stale", Status(await ReadProposalAsync(host, competing)));
    }

    /// <summary>
    /// Another save commits a new version just before this save's own version
    /// insert, after anything this save read: only the database's guard sees it.
    /// </summary>
    [Fact]
    public async Task A_save_raced_by_another_new_version_writes_nothing_of_its_own_and_goes_stale()
    {
        await using var host = await StartHostAsync();
        var templateId = await CreateTemplateAsync(host, "Release Loop");
        var agent = host.Agent(await SeedRunAsync(host.Factory));
        var id = await ProposeBuildCommandAsync(agent, templateId, "make proposed");
        host.Race.Arm($"""
            INSERT INTO "LoopTemplateVersions" ("Id", "LoopTemplateId", "VersionNumber", "CreatedAt")
            VALUES ('{Guid.NewGuid().ToString().ToUpperInvariant()}', '{templateId.ToString().ToUpperInvariant()}', 2, '2031-01-01 00:00:00')
            """);

        var resp = await SaveAsync(host.Human, id);

        Assert.True(host.Race.Raced, "the save inserted no version, so the race never ran");
        var body = await ConflictAsync(resp, "saving after another version landed");
        Assert.Equal("Stale", Status(body.GetProperty("proposal")));
        Assert.Equal("Stale", Status(await ReadProposalAsync(host, id)));
        Assert.True((await VersionNumbersAsync(host, templateId)).Length <= 2, "the proposal's own version was written");
        using var scope = host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.LoopNodes.AnyAsync(n => n.Config.Contains("make proposed"), Ct), "the proposal's nodes were written");
    }

    [Fact]
    public async Task Rejecting_keeps_a_trimmed_reason_without_NUL_writes_nothing_and_the_agent_reads_it()
    {
        await using var host = await StartHostAsync();
        var templateId = await CreateTemplateAsync(host, "Release Loop");
        var docs = await CreateTemplateAsync(host, "Docs Loop");
        var lint = await CreateTemplateAsync(host, "Lint Loop");
        var chatId = await SeedChatSessionAsync(host.Factory);
        var agent = host.Agent(chatSessionId: chatId);
        var withReason = await ProposeBuildCommandAsync(agent, templateId, "make -j4");
        var blankReason = await ProposeBuildCommandAsync(agent, docs, "make docs");
        var tooLong = await ProposeBuildCommandAsync(agent, lint, "make lint");
        host.ClearHints();

        var resp = await RejectAsync(host.Human, withReason, "  Too\0 risky, keep one job.  ");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(host.BoardHinted(templateId), "the board was not hinted");
        Assert.True(host.ChatHinted(chatId), "the chat was not hinted");
        Assert.Equal(HttpStatusCode.OK, (await RejectAsync(host.Human, blankReason, "   ")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await RejectAsync(host.Human, tooLong, new string('r', 2001))).StatusCode);

        Assert.Equal(new[] { 1 }, await VersionNumbersAsync(host, templateId));
        Assert.Equal("make", NodeLabelled(JsonNode.Parse((await TemplateAsync(host, templateId)).GetRawText())!.AsObject(), "Build")["config"]!["command"]!.GetValue<string>());
        var own = (await ListAsAgentAsync(agent)).ToDictionary(Id);
        Assert.Equal("Rejected", Status(own[withReason]));
        Assert.Equal("Too risky, keep one job.", own[withReason].GetProperty("rejectionReason").GetString());
        Assert.False(IsNullOrAbsent(own[withReason], "decidedAt"));
        Assert.Equal("Rejected", Status(own[blankReason]));
        Assert.True(IsNullOrAbsent(own[blankReason], "rejectionReason"));
        Assert.Equal("Pending", Status(own[tooLong]));
    }

    [Fact]
    public async Task Deciding_a_proposal_that_is_no_longer_pending_is_a_409_naming_its_status_and_an_unknown_one_a_404()
    {
        await using var host = await StartHostAsync();
        var release = await CreateTemplateAsync(host, "Release Loop");
        var docs = await CreateTemplateAsync(host, "Docs Loop");
        var agent = host.Agent(await SeedRunAsync(host.Factory));
        var rejected = await ProposeBuildCommandAsync(agent, release, "make -j4");
        Assert.Equal(HttpStatusCode.OK, (await RejectAsync(host.Human, rejected, "No.")).StatusCode);
        var superseded = await ProposeBuildCommandAsync(agent, docs, "make one");
        await ProposeBuildCommandAsync(agent, docs, "make two");
        var applied = await ProposeBuildCommandAsync(host.Agent(await SeedRunAsync(host.Factory)), release, "make applied");
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(host.Human, applied)).StatusCode);

        foreach (var (id, status) in new[] { (rejected, "Rejected"), (superseded, "Superseded"), (applied, "Applied") })
        {
            var onSave = await ConflictAsync(await SaveAsync(host.Human, id), $"saving a {status} one");
            Assert.Equal(status, Status(onSave.GetProperty("proposal")));
            Assert.Contains(status, onSave.GetProperty("error").GetString()!, StringComparison.OrdinalIgnoreCase);
            var onReject = await ConflictAsync(await RejectAsync(host.Human, id, "Again."), $"rejecting a {status} one");
            Assert.Equal(status, Status(onReject.GetProperty("proposal")));
        }
        Assert.Equal("No.", (await ReadProposalAsync(host, rejected)).GetProperty("rejectionReason").GetString());
        Assert.Equal(new[] { 1, 2 }, await VersionNumbersAsync(host, release));
        Assert.Equal(new[] { 1 }, await VersionNumbersAsync(host, docs));

        Assert.Equal(HttpStatusCode.NotFound, (await SaveAsync(host.Human, Guid.NewGuid().ToString())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await RejectAsync(host.Human, Guid.NewGuid().ToString(), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Human.GetAsync($"{ProposalsUrl}/{Guid.NewGuid()}", Ct)).StatusCode);
    }

    // ── Stale ─────────────────────────────────────────────────────────────

    public enum HumanWrite { EditorSave, Delete }

    [Theory]
    [InlineData(HumanWrite.EditorSave)]
    [InlineData(HumanWrite.Delete)]
    public async Task A_human_saving_or_deleting_a_template_makes_its_pending_proposals_stale_and_creating_or_cloning_does_not(HumanWrite write)
    {
        await using var host = await StartHostAsync();
        var templateId = await CreateTemplateAsync(host, "Release Loop");
        var otherTemplateId = await CreateTemplateAsync(host, "Docs Loop");
        var chatId = await SeedChatSessionAsync(host.Factory);
        var fromRun = await ProposeBuildCommandAsync(host.Agent(await SeedRunAsync(host.Factory)), templateId, "make run");
        var fromChat = await ProposeBuildCommandAsync(host.Agent(chatSessionId: chatId), templateId, "make chat");
        var otherTemplate = await ProposeBuildCommandAsync(host.Agent(await SeedRunAsync(host.Factory)), otherTemplateId, "make docs");

        Assert.Equal(HttpStatusCode.OK, (await host.Human.PostAsync($"{TemplatesUrl}/{templateId}/clone?newName=Release%20Copy", null, Ct)).StatusCode);
        await CreateTemplateAsync(host, "Brand New Loop");
        Assert.Equal(2, await PendingCountAsync(host, templateId));
        host.ClearHints();

        if (write == HumanWrite.EditorSave)
            await PlainSaveAsync(host, templateId, "make human");
        else
            Assert.True((await host.Human.DeleteAsync($"{TemplatesUrl}/{templateId}", Ct)).IsSuccessStatusCode);

        foreach (var id in new[] { fromRun, fromChat })
        {
            var stale = await ReadProposalAsync(host, id);
            Assert.Equal("Stale", Status(stale));
            Assert.False(IsNullOrAbsent(stale, "decidedAt"));
            await ConflictAsync(await SaveAsync(host.Human, id), "saving a stale one");
            await ConflictAsync(await RejectAsync(host.Human, id, "No."), "rejecting a stale one");
        }
        if (write == HumanWrite.Delete)
            Assert.False((await ReadProposalAsync(host, fromRun)).GetProperty("templateExists").GetBoolean());
        Assert.Equal("Pending", Status(await ReadProposalAsync(host, otherTemplate)));
        Assert.Equal(0, await PendingCountAsync(host, templateId));
        Assert.True(host.BoardHinted(templateId), "the board was not hinted");
        Assert.True(host.ChatHinted(chatId), "the chat whose proposal went stale was not hinted");
    }

    // ── Saving from review in the Loop Editor ─────────────────────────────

    public enum ReviewEdit { None, LayoutOrderAndEdgeIdsOnly, Label, Config, AddedEdge, Name, Description, Policy }

    [Theory]
    [InlineData(ReviewEdit.None, false)]
    [InlineData(ReviewEdit.LayoutOrderAndEdgeIdsOnly, false)]
    [InlineData(ReviewEdit.Label, true)]
    [InlineData(ReviewEdit.Config, true)]
    [InlineData(ReviewEdit.AddedEdge, true)]
    [InlineData(ReviewEdit.Name, true)]
    [InlineData(ReviewEdit.Description, true)]
    [InlineData(ReviewEdit.Policy, true)]
    public async Task Saving_from_review_applies_the_proposal_marked_edited_exactly_when_the_saved_loop_differs(ReviewEdit edit, bool edited)
    {
        await using var host = await StartHostAsync();
        var templateId = await CreateTemplateAsync(host, "Release Loop");
        var competingChat = await SeedChatSessionAsync(host.Factory);
        var reviewed = await ProposeBuildCommandAsync(host.Agent(await SeedRunAsync(host.Factory)), templateId, "make -j4");
        var competing = await ProposeBuildCommandAsync(host.Agent(chatSessionId: competingChat), templateId, "make -j8");
        var copy = StoredDocument(await ReadProposalAsync(host, reviewed));
        switch (edit)
        {
            case ReviewEdit.LayoutOrderAndEdgeIdsOnly:
                var i = 0;
                foreach (var node in copy["nodes"]!.AsArray())
                    node!["config"]!["__pos"] = new JsonObject { ["x"] = 100 * ++i, ["y"] = 40 };
                copy["nodes"] = new JsonArray(copy["nodes"]!.AsArray().Reverse().Select(n => n!.DeepClone()).ToArray());
                copy["edges"] = new JsonArray(copy["edges"]!.AsArray().Reverse().Select(e =>
                {
                    var clone = e!.DeepClone();
                    clone["id"] = $"xy-edge-{Guid.NewGuid():N}";
                    return clone;
                }).ToArray());
                break;
            case ReviewEdit.Label: NodeLabelled(copy, "Test")["label"] = "Unit Test"; break;
            case ReviewEdit.Config: NodeLabelled(copy, "Test")["config"]!["command"] = "make check"; break;
            case ReviewEdit.AddedEdge:
                copy["edges"]!.AsArray().Add(new JsonObject
                {
                    ["id"] = "fail",
                    ["sourceNodeId"] = NodeLabelled(copy, "Build")["id"]!.GetValue<string>(),
                    ["targetNodeId"] = NodeLabelled(copy, "Cleanup")["id"]!.GetValue<string>(),
                    ["edgeType"] = "OnFailure",
                    ["name"] = null,
                });
                break;
            case ReviewEdit.Name: copy["name"] = "Release Loop Renamed"; break;
            case ReviewEdit.Description: copy["description"] = "A changed description."; break;
            case ReviewEdit.Policy: copy["recoveryPolicy"] = "Cancel"; break;
        }
        host.ClearHints();

        var body = await SaveFromReviewAsync(host, templateId, copy, reviewed);

        Assert.True(body.GetProperty("proposalApplied").GetBoolean());
        var applied = await ReadProposalAsync(host, reviewed);
        Assert.Equal("Applied", Status(applied));
        Assert.Equal(edited, applied.GetProperty("editedBeforeSave").GetBoolean());
        Assert.Equal(2, applied.GetProperty("appliedVersion").GetInt32());
        Assert.Equal(new[] { 1, 2 }, await VersionNumbersAsync(host, templateId));
        AssertSameGraph(copy, await TemplateAsync(host, templateId));
        Assert.Equal("Stale", Status(await ReadProposalAsync(host, competing)));
        Assert.True(host.BoardHinted(templateId), "the board was not hinted");
        Assert.True(host.ChatHinted(competingChat), "the chat whose proposal went stale was not hinted");
    }

    public enum NamedProposal { WentStale, Rejected, OfAnotherTemplate }

    [Theory]
    [InlineData(NamedProposal.WentStale)]
    [InlineData(NamedProposal.Rejected)]
    [InlineData(NamedProposal.OfAnotherTemplate)]
    public async Task Saving_from_review_of_a_proposal_that_cannot_apply_is_a_plain_save_that_leaves_it_alone(NamedProposal named)
    {
        await using var host = await StartHostAsync();
        var templateId = await CreateTemplateAsync(host, "Release Loop");
        var otherTemplateId = await CreateTemplateAsync(host, "Docs Loop");
        var agent = host.Agent(await SeedRunAsync(host.Factory));
        var reviewed = await ProposeBuildCommandAsync(agent, named == NamedProposal.OfAnotherTemplate ? otherTemplateId : templateId, "make -j4");
        var copy = StoredDocument(await ReadProposalAsync(host, reviewed));
        var expected = "Pending";
        if (named == NamedProposal.WentStale)
        {
            await PlainSaveAsync(host, templateId, "make by someone else");
            expected = "Stale";
        }
        else if (named == NamedProposal.Rejected)
        {
            Assert.Equal(HttpStatusCode.OK, (await RejectAsync(host.Human, reviewed, "No.")).StatusCode);
            expected = "Rejected";
        }
        var before = (await VersionNumbersAsync(host, templateId)).Length;
        if (named == NamedProposal.OfAnotherTemplate)
            copy = (await ReadDocumentAsync(agent, templateId.ToString())).Document;
        NodeLabelled(copy, "Build")["config"]!["command"] = "make from the editor";

        var body = await SaveFromReviewAsync(host, templateId, copy, reviewed);

        Assert.False(body.GetProperty("proposalApplied").GetBoolean());
        Assert.Equal(before + 1, (await VersionNumbersAsync(host, templateId)).Length);
        AssertSameGraph(copy, await TemplateAsync(host, templateId));
        var left = await ReadProposalAsync(host, reviewed);
        Assert.Equal(expected, Status(left));
        Assert.True(IsNullOrAbsent(left, "appliedVersion"));
    }

    // ── Where a proposal came from ────────────────────────────────────────

    [Fact]
    public async Task The_human_lists_proposals_by_status_template_work_item_or_chat_each_naming_its_source_and_anchor()
    {
        await using var host = await StartHostAsync();
        var release = await CreateTemplateAsync(host, "Release Loop");
        var docs = await CreateTemplateAsync(host, "Docs Loop");
        var (runOfA, stepOfA) = await SeedRunWithRunningStepAsync(host.Factory, "wi-a");
        var secondRunOfA = await SeedRunAsync(host.Factory, "wi-a", LoopRunStatus.Completed);
        var runOfB = await SeedRunAsync(host.Factory, "wi-b");
        var ownChat = await SeedChatSessionAsync(host.Factory, owner: host.Factory.AdminUsername);
        await SeedChatMessagesAsync(host.Factory, ownChat, "user", "assistant", "user");
        var othersChat = await SeedChatSessionAsync(host.Factory, owner: "someone-else");

        var fromA = await ProposeBuildCommandAsync(host.Agent(runOfA), release, "make a");
        var fromSecondA = await ProposeBuildCommandAsync(host.Agent(secondRunOfA), docs, "make a2");
        Assert.Equal(HttpStatusCode.OK, (await RejectAsync(host.Human, fromSecondA, "No.")).StatusCode);
        var fromB = await ProposeBuildCommandAsync(host.Agent(runOfB), release, "make b");
        var fromOwnChat = await ProposeBuildCommandAsync(host.Agent(chatSessionId: ownChat), docs, "make own");
        var fromOthersChat = await ProposeBuildCommandAsync(host.Agent(chatSessionId: othersChat), release, "make others");

        static string[] Ids(JsonElement[] list) => list.Select(Id).OrderBy(i => i).ToArray();
        static string[] Sorted(params string[] ids) => ids.OrderBy(i => i).ToArray();
        Assert.Equal(Sorted(fromA, fromSecondA), Ids(await ListAsHumanAsync(host, "?workItemId=wi-a")));
        Assert.Equal(Sorted(fromOwnChat), Ids(await ListAsHumanAsync(host, $"?chatSessionId={ownChat}")));
        Assert.Equal(Sorted(fromA, fromB, fromOthersChat), Ids(await ListAsHumanAsync(host, $"?templateId={release}")));
        Assert.Equal(Sorted(fromA, fromB, fromOwnChat, fromOthersChat), Ids(await ListAsHumanAsync(host, "?status=Pending")));
        Assert.Equal(Sorted(fromA, fromB, fromOthersChat), Ids(await ListAsHumanAsync(host, $"?status=Pending&templateId={release}")));

        var all = (await ListAsHumanAsync(host)).ToDictionary(Id);
        Assert.Equal(5, all.Count);

        var a = all[fromA];
        Assert.Equal(runOfA.ToString(), a.GetProperty("createdByLoopRunId").GetString());
        Assert.Equal(stepOfA.ToString(), a.GetProperty("createdByRunNodeId").GetString());
        Assert.True(IsNullOrAbsent(a, "createdByChatSessionId"));
        Assert.True(IsNullOrAbsent(a, "chatReplySequence"));
        Assert.Equal("Release Loop", a.GetProperty("templateName").GetString());
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
    public async Task Deleting_the_source_leaves_its_proposals_pending_and_decidable(SourceDeletion deletion)
    {
        await using var host = await StartHostAsync();
        var release = await CreateTemplateAsync(host, "Release Loop");
        var docs = await CreateTemplateAsync(host, "Docs Loop");
        var itemId = await CreateHumanItemAsync(host);
        var runId = await SeedRunAsync(host.Factory, itemId);
        var chatId = await SeedChatSessionAsync(host.Factory, owner: host.Factory.AdminUsername);
        var agent = deletion == SourceDeletion.AUserDeletesTheChat ? host.Agent(chatSessionId: chatId) : host.Agent(runId);
        var toSave = await ProposeBuildCommandAsync(agent, release, "make -j4");
        var toReject = await ProposeBuildCommandAsync(agent, docs, "make docs");
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
        foreach (var id in new[] { toSave, toReject })
        {
            var left = await ReadProposalAsync(host, id);
            Assert.Equal("Pending", Status(left));
            Assert.Equal("deleted", left.GetProperty("source").GetProperty("kind").GetString());
        }
        Assert.Equal(1, await PendingCountAsync(host, release));

        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(host.Human, toSave)).StatusCode);
        Assert.Equal(new[] { 1, 2 }, await VersionNumbersAsync(host, release));
        Assert.Equal(HttpStatusCode.OK, (await RejectAsync(host.Human, toReject, "No.")).StatusCode);
        Assert.Equal("Rejected", Status(await ReadProposalAsync(host, toReject)));
    }

    // ── The agent cannot decide ───────────────────────────────────────────

    [Fact]
    public async Task The_agent_token_cannot_reach_the_human_routes_or_save_a_template_and_has_no_route_that_decides()
    {
        await using var host = await StartHostAsync();
        var templateId = await CreateTemplateAsync(host, "Release Loop");
        var agent = host.Agent(await SeedRunAsync(host.Factory));
        var id = await ProposeBuildCommandAsync(agent, templateId, "make -j4");
        var document = (await ReadDocumentAsync(agent, templateId.ToString())).Document;

        var humanRoutes = new[]
        {
            await agent.GetAsync(ProposalsUrl, Ct),
            await agent.GetAsync($"{ProposalsUrl}/pending-counts", Ct),
            await agent.GetAsync($"{ProposalsUrl}/{id}", Ct),
            await SaveAsync(agent, id),
            await RejectAsync(agent, id, "Self-rejected."),
            await agent.PutAsJsonAsync($"{TemplatesUrl}/{templateId}", new JsonObject
            {
                ["name"] = "Release Loop",
                ["description"] = "",
                ["recoveryPolicy"] = "AutoResume",
                ["nodes"] = document["nodes"]!.DeepClone(),
                ["edges"] = document["edges"]!.DeepClone(),
                ["fromProposalId"] = id,
            }, Ct),
        };
        foreach (var resp in humanRoutes)
            Assert.True(resp.StatusCode == HttpStatusCode.Forbidden, $"{resp.RequestMessage!.Method} {resp.RequestMessage.RequestUri} answered {(int)resp.StatusCode}");

        var agentAttempts = new[]
        {
            await agent.PostAsync($"{AgentProposalsUrl}/{id}/save", null, Ct),
            await agent.PostAsync($"{AgentProposalsUrl}/{id}/apply", null, Ct),
            await agent.PostAsJsonAsync($"{AgentProposalsUrl}/{id}/reject", new { reason = "Self-rejected." }, Ct),
            await agent.DeleteAsync($"{AgentProposalsUrl}/{id}", Ct),
            await agent.PutAsJsonAsync($"{AgentProposalsUrl}/{id}", new { status = "Applied" }, Ct),
        };
        foreach (var resp in agentAttempts)
            Assert.False(resp.IsSuccessStatusCode, $"{resp.RequestMessage!.Method} {resp.RequestMessage.RequestUri} answered {(int)resp.StatusCode}");

        Assert.Equal("Pending", Status(await ReadProposalAsync(host, id)));
        Assert.Equal(new[] { 1 }, await VersionNumbersAsync(host, templateId));
    }
}
