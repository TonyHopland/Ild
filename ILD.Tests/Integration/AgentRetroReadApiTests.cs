using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

/// <summary>
/// The read-only agent routes a loop retro is built on: per-template analytics
/// that agree with the dashboard, a saved template version as an
/// ild-loop-template/v2 document, the run list filtered by date and template,
/// and one run's path, feedback and review comments as a bounded summary.
/// </summary>
public class AgentRetroReadApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- get_template_analytics -------------------------------------------------

    [Fact]
    public async Task Template_analytics_equal_the_dashboard_overview_and_add_per_node_figures_from_live_runs()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        Guid alpha, beta, gamma, coder1, tests1, coder2;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var t1 = AddTemplate(db, $"retro-alpha-{Guid.NewGuid():N}");
            var v1 = AddVersion(db, t1.Id, 1);
            var v2 = AddVersion(db, t1.Id, 2);
            var c1 = AddNode(db, v1.Id, NodeType.AI, "Coder");
            var ts = AddNode(db, v1.Id, NodeType.Cmd, "Tests");
            var c2 = AddNode(db, v2.Id, NodeType.AI, "Coder");
            var onFailure = AddEdge(db, c1.Id, ts.Id, EdgeType.OnFailure, null);
            AddEdge(db, c1.Id, ts.Id, EdgeType.Custom, "Reject");

            var r1 = AddRun(db, v1.Id, LoopRunStatus.Completed, At(6, 1, 10), At(6, 1, 9));
            AddVisit(db, r1.Id, c1.Id, LoopRunNodeStatus.Succeeded, At(6, 1, 10), At(6, 1, 10).AddMinutes(2), "claude", 100, 40, 0.10m);
            AddVisit(db, r1.Id, ts.Id, LoopRunNodeStatus.Failed, At(6, 1, 10).AddMinutes(2), At(6, 1, 10).AddMinutes(6), incomingEdgeId: onFailure.Id);
            // Still running: counted as an execution, but not in the average.
            AddVisit(db, r1.Id, c1.Id, LoopRunNodeStatus.Running, At(6, 1, 10).AddMinutes(6), null);
            AddEvent(db, r1.Id, EventType.HumanFeedbackRequested, At(6, 1, 11), "Confirm?");
            AddEvent(db, r1.Id, EventType.HumanFeedbackReceived, At(6, 1, 11).AddSeconds(90), "Yes.");

            var r2 = AddRun(db, v2.Id, LoopRunStatus.Failed, At(6, 3, 8), At(6, 3, 8));
            AddVisit(db, r2.Id, c2.Id, LoopRunNodeStatus.Failed, At(6, 3, 8), At(6, 3, 8).AddMinutes(4), "claude", 200, 60, 0.20m);

            // Outside the date range.
            var r3 = AddRun(db, v1.Id, LoopRunStatus.Completed, At(6, 10, 8), At(6, 10, 8));
            AddVisit(db, r3.Id, c1.Id, LoopRunNodeStatus.Succeeded, At(6, 10, 8), At(6, 10, 8).AddSeconds(5), "claude", 1, 1, 0.01m);

            // Never started: its day is CreatedAt's. No AI node, so provider "(none)".
            var r4 = AddRun(db, v1.Id, LoopRunStatus.Running, null, At(6, 2, 12));
            AddVisit(db, r4.Id, ts.Id, LoopRunNodeStatus.Succeeded, At(6, 2, 12), At(6, 2, 12).AddSeconds(1));

            // A deleted run of alpha, archived.
            AddBucket(db, t1.Id, t1.Name, At(6, 2, 0), "claude", runs: 1, completed: 1, failed: 0, cancelled: 0,
                nodeCount: 2, nodeSeconds: 10, feedbackCount: 1, feedbackSeconds: 30, input: 300, output: 50, cost: 0.30m);

            // beta: every run deleted, only its bucket is left.
            var t2 = AddTemplate(db, $"retro-beta-{Guid.NewGuid():N}");
            var t2v1 = AddVersion(db, t2.Id, 1);
            AddNode(db, t2v1.Id, NodeType.AI, "Writer");
            AddBucket(db, t2.Id, t2.Name, At(6, 4, 0), "claude", runs: 2, completed: 0, failed: 1, cancelled: 1,
                nodeCount: 4, nodeSeconds: 40, feedbackCount: 0, feedbackSeconds: 0, input: 20, output: 10, cost: 0.05m);

            // gamma: a live run on another provider.
            var t3 = AddTemplate(db, $"retro-gamma-{Guid.NewGuid():N}");
            var t3v1 = AddVersion(db, t3.Id, 1);
            var g1 = AddNode(db, t3v1.Id, NodeType.AI, "Solo");
            var r5 = AddRun(db, t3v1.Id, LoopRunStatus.Completed, At(6, 2, 9), At(6, 2, 9));
            AddVisit(db, r5.Id, g1.Id, LoopRunNodeStatus.Succeeded, At(6, 2, 9), At(6, 2, 9).AddSeconds(3), "opencode", 5, 5, 0.50m);

            await db.SaveChangesAsync(Ct);
            (alpha, beta, gamma, coder1, tests1, coder2) = (t1.Id, t2.Id, t3.Id, c1.Id, ts.Id, c2.Id);
        }

        // Week of June 1–4 on claude, provider given in another case.
        var week = await GetOkAsync(client, "/api/v1/agent/analytics/templates?from=2026-06-01&to=2026-06-04&provider=CLAUDE");
        var weekOverview = await OverviewAsync(factory, new AnalyticsQuery(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 4), "CLAUDE"));
        Assert.Equal(3, weekOverview.Templates.Single(t => t.TemplateId == alpha).TotalRuns);
        AssertRowsMatchOverview(weekOverview.Templates, week.GetProperty("templates"));
        Assert.Equal(new[] { alpha, beta }.Order(), weekOverview.Templates.Select(t => t.TemplateId).Order());

        Assert.Equal("2026-06-01", week.GetProperty("from").GetString());
        Assert.Equal("2026-06-04", week.GetProperty("to").GetString());
        Assert.Equal("claude", week.GetProperty("provider").GetString(), ignoreCase: true);
        Assert.True(IsNullOrMissing(week, "templateId"));

        var alphaRow = Row(week, alpha);
        Assert.Equal(2, alphaRow.GetProperty("runsWithNodeDetail").GetInt32());
        var nodes = alphaRow.GetProperty("nodes").EnumerateArray().ToDictionary(n => n.GetProperty("nodeId").GetGuid());
        Assert.Equal(new[] { coder1, tests1, coder2 }.Order(), nodes.Keys.Order());
        AssertNode(nodes[coder1], "Coder", "AI", version: 1, executions: 2, failures: 0, avgSeconds: 120);
        AssertNode(nodes[tests1], "Tests", "Cmd", version: 1, executions: 1, failures: 1, avgSeconds: 240);
        AssertNode(nodes[coder2], "Coder", "AI", version: 2, executions: 1, failures: 1, avgSeconds: 240);

        var betaRow = Row(week, beta);
        Assert.Equal(0, betaRow.GetProperty("runsWithNodeDetail").GetInt32());
        Assert.Empty(betaRow.GetProperty("nodes").EnumerateArray());

        // One day, every provider: the never-started run counts by its CreatedAt day.
        var day = await GetOkAsync(client, "/api/v1/agent/analytics/templates?from=2026-06-02&to=2026-06-02");
        var dayOverview = await OverviewAsync(factory, new AnalyticsQuery(new DateOnly(2026, 6, 2), new DateOnly(2026, 6, 2)));
        AssertRowsMatchOverview(dayOverview.Templates, day.GetProperty("templates"));
        Assert.Equal(new[] { alpha, gamma }.Order(), dayOverview.Templates.Select(t => t.TemplateId).Order());
        var alphaDay = Row(day, alpha);
        Assert.Equal(1, alphaDay.GetProperty("runsWithNodeDetail").GetInt32());
        var dayNodes = alphaDay.GetProperty("nodes").EnumerateArray().ToDictionary(n => n.GetProperty("nodeId").GetGuid());
        AssertNode(dayNodes[tests1], "Tests", "Cmd", version: 1, executions: 1, failures: 0, avgSeconds: 1);
        Assert.All(dayNodes.Where(n => n.Key != tests1), n => Assert.Equal(0, n.Value.GetProperty("executions").GetInt32()));

        // templateId narrows to that template, figures as the unfiltered overview has them.
        var onlyBeta = await GetOkAsync(client, $"/api/v1/agent/analytics/templates?templateId={beta}");
        var allOverview = await OverviewAsync(factory, new AnalyticsQuery());
        AssertRowsMatchOverview(allOverview.Templates.Where(t => t.TemplateId == beta).ToList(), onlyBeta.GetProperty("templates"));
        Assert.Equal(beta, onlyBeta.GetProperty("templateId").GetGuid());
        Assert.Equal(2, Row(onlyBeta, beta).GetProperty("totalRuns").GetInt32());

        var unknown = await GetOkAsync(client, $"/api/v1/agent/analytics/templates?templateId={Guid.NewGuid()}");
        Assert.Empty(unknown.GetProperty("templates").EnumerateArray());
    }

    [Fact]
    public async Task A_malformed_filter_is_a_400_and_the_analytics_error_names_the_parameter()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        foreach (var (query, parameter) in new[] { ("from=2026-02-30", "from"), ("to=last-week", "to"), ("to=06/01/2026", "to"), ("templateId=retro", "templateId") })
        {
            var resp = await client.GetAsync($"/api/v1/agent/analytics/templates?{query}", Ct);

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var error = (await ReadJsonAsync(resp)).GetProperty("error").GetString();
            Assert.Matches(new Regex($@"\b{parameter}\b"), error);
        }

        foreach (var query in new[] { "from=yesterday", "from=06/01/2026", "to=2026-13-01", "templateId=not-a-guid" })
        {
            var resp = await client.GetAsync($"/api/v1/agent/loop-runs?{query}", Ct);

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
    }

    // ---- list_loop_runs ---------------------------------------------------------

    [Fact]
    public async Task ListLoopRuns_filters_by_start_day_template_and_work_item_newest_first_with_template_version()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        // Ids differ only in their last digit, so every store orders them the same way.
        static Guid Id(int n) => new($"00000000-0000-0000-0000-00000000000{n}");
        var (a, b, c, d, e, f, g) = (Id(1), Id(2), Id(3), Id(4), Id(5), Id(6), Id(7));
        Guid template;
        string templateName;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var t = AddTemplate(db, $"list-{Guid.NewGuid():N}");
            var tv1 = AddVersion(db, t.Id, 1);
            var tv2 = AddVersion(db, t.Id, 2);
            var u = AddTemplate(db, $"other-{Guid.NewGuid():N}");
            var uv1 = AddVersion(db, u.Id, 1);

            AddRun(db, tv1.Id, LoopRunStatus.Completed, new DateTime(2026, 6, 1, 23, 59, 59, DateTimeKind.Utc), At(6, 1, 23), "W1", a);
            AddRun(db, tv2.Id, LoopRunStatus.Running, null, At(6, 3, 0), "W1", b);
            // Created inside the range but started after it: the start day decides.
            AddRun(db, tv2.Id, LoopRunStatus.Running, At(6, 4, 0), At(6, 3, 23), "W2", c);
            AddRun(db, uv1.Id, LoopRunStatus.Completed, At(6, 2, 12), At(6, 2, 12), "W3", d);
            AddRun(db, tv1.Id, LoopRunStatus.Completed, new DateTime(2026, 5, 31, 23, 59, 59, DateTimeKind.Utc), At(5, 31, 23), "W1", e);
            AddRun(db, tv1.Id, LoopRunStatus.Failed, At(6, 2, 8), At(6, 2, 8), "W2", f);
            AddRun(db, tv2.Id, LoopRunStatus.Completed, At(6, 2, 8), At(6, 2, 8), "W2", g);
            await db.SaveChangesAsync(Ct);
            (template, templateName) = (t.Id, t.Name);
        }

        async Task<Guid[]> Ids(string query)
            => (await GetOkAsync(client, $"/api/v1/agent/loop-runs{query}")).EnumerateArray()
                .Select(r => r.GetProperty("id").GetGuid()).ToArray();

        Assert.Equal(new[] { c, b, d, g, f, a, e }, await Ids(""));
        Assert.Equal(new[] { b, a, e }, await Ids("?workItemId=W1"));
        Assert.Equal(new[] { b, d, g, f, a }, await Ids("?from=2026-06-01&to=2026-06-03"));
        Assert.Equal(new[] { b, g, f, a }, await Ids($"?from=2026-06-01&to=2026-06-03&templateId={template}"));
        Assert.Equal(new[] { b, a }, await Ids($"?from=2026-06-01&to=2026-06-03&templateId={template}&workItemId=W1"));
        Assert.Equal(new[] { d, g }, await Ids("?from=2026-06-01&to=2026-06-03&skip=1&take=2"));
        Assert.Equal(new[] { c, b, g }, await Ids($"?from=2026-06-02&templateId={template}&take=3"));
        Assert.Equal(new[] { a, e }, await Ids("?to=2026-06-01&workItemId=W1"));
        Assert.Empty(await Ids("?from=2026-06-03&to=2026-06-01"));

        var rows = (await GetOkAsync(client, $"/api/v1/agent/loop-runs?templateId={template}"))
            .EnumerateArray().ToDictionary(r => r.GetProperty("id").GetGuid());
        foreach (var field in new[] { "id", "workItemId", "status", "startedAt", "completedAt", "costUsd", "inputTokens", "outputTokens" })
            Assert.True(rows[b].TryGetProperty(field, out _), $"list_loop_runs rows lost '{field}'");
        Assert.Equal("W1", rows[b].GetProperty("workItemId").GetString());
        Assert.Equal(template, rows[b].GetProperty("templateId").GetGuid());
        Assert.Equal(templateName, rows[b].GetProperty("templateName").GetString());
        Assert.Equal(2, rows[b].GetProperty("templateVersion").GetInt32());
        Assert.Equal(1, rows[a].GetProperty("templateVersion").GetInt32());
    }

    // ---- get_loop_template ------------------------------------------------------

    [Fact]
    public async Task GetLoopTemplate_returns_a_saved_version_as_a_v2_document_by_name_or_id()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var name = $"Team/Retro loop {Guid.NewGuid():N}";

        Guid templateId;
        using (var scope = factory.Services.CreateScope())
        {
            var templates = scope.ServiceProvider.GetRequiredService<ILoopTemplateManager>();
            templateId = await templates.CreateLoopTemplateAsync(name, "first", Graph("Review the code."), RecoveryPolicy.NeedsReview);
            Assert.Equal(LoopTemplateVersionSaveOutcome.Saved,
                (await templates.SaveNewVersionAsync(templateId, name, "second", Graph("Review the code twice."), RecoveryPolicy.NeedsReview)).Outcome);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.LoopTemplates.Single(t => t.Id == templateId).Description = null;
            await db.SaveChangesAsync(Ct);
        }

        var latest = await GetOkAsync(client,
            $"/api/v1/agent/loop-templates/document?nameOrId={Uri.EscapeDataString($"  {name.ToUpperInvariant()} ")}");
        Assert.Equal(templateId, latest.GetProperty("templateId").GetGuid());
        Assert.Equal(2, latest.GetProperty("version").GetInt32());
        Assert.Equal(2, latest.GetProperty("latestVersion").GetInt32());
        Assert.True(latest.GetProperty("isLatest").GetBoolean());
        await AssertDocumentIsStoredVersionAsync(factory, templateId, 2, name, latest.GetProperty("document"));

        var first = await GetOkAsync(client, $"/api/v1/agent/loop-templates/document?nameOrId={templateId}&version=1");
        Assert.Equal(1, first.GetProperty("version").GetInt32());
        Assert.Equal(2, first.GetProperty("latestVersion").GetInt32());
        Assert.False(first.GetProperty("isLatest").GetBoolean());
        await AssertDocumentIsStoredVersionAsync(factory, templateId, 1, name, first.GetProperty("document"));

        var missingVersion = await client.GetAsync($"/api/v1/agent/loop-templates/document?nameOrId={templateId}&version=3", Ct);
        Assert.Equal(HttpStatusCode.NotFound, missingVersion.StatusCode);
        var missingBody = await ReadJsonAsync(missingVersion);
        Assert.True(
            (missingBody.TryGetProperty("latestVersion", out var lv) && lv.ValueKind == JsonValueKind.Number && lv.GetInt32() == 2)
            || Regex.IsMatch(missingBody.GetProperty("error").GetString() ?? "", @"\b2\b"),
            $"the 404 does not name the latest version: {missingBody}");

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/v1/agent/loop-templates/document?nameOrId={templateId}&version=0", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/v1/agent/loop-templates/document?nameOrId={Guid.NewGuid()}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/v1/agent/loop-templates/document?nameOrId={Uri.EscapeDataString("no such loop " + Guid.NewGuid())}", Ct)).StatusCode);
    }

    [Fact]
    public async Task GetLoopTemplate_finds_archived_templates_by_name_and_refuses_an_ambiguous_name()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var archivedName = $"Old retro {Guid.NewGuid():N}";
        var twinName = $"Twin {Guid.NewGuid():N}";

        Guid archived, twinA, twinB;
        using (var scope = factory.Services.CreateScope())
        {
            var templates = scope.ServiceProvider.GetRequiredService<ILoopTemplateManager>();
            archived = await templates.CreateLoopTemplateAsync(archivedName, "", Graph("Old."));
            await templates.ArchiveLoopTemplateAsync(archived);
            twinA = await templates.CreateLoopTemplateAsync(twinName, "", Graph("A."));
            twinB = await templates.CreateLoopTemplateAsync(twinName.ToLowerInvariant(), "", Graph("B."));
            await templates.ArchiveLoopTemplateAsync(twinB);
        }

        var found = await GetOkAsync(client, $"/api/v1/agent/loop-templates/document?nameOrId={Uri.EscapeDataString(archivedName)}");
        Assert.Equal(archived, found.GetProperty("templateId").GetGuid());
        Assert.Equal(1, found.GetProperty("version").GetInt32());

        var ambiguous = await client.GetAsync($"/api/v1/agent/loop-templates/document?nameOrId={Uri.EscapeDataString(twinName)}", Ct);
        Assert.Equal(HttpStatusCode.Conflict, ambiguous.StatusCode);
        var candidates = (await ReadJsonAsync(ambiguous)).GetProperty("candidates").EnumerateArray()
            .ToDictionary(c => c.GetProperty("id").GetGuid());
        Assert.Equal(new[] { twinA, twinB }.Order(), candidates.Keys.Order());
        Assert.Equal(twinName, candidates[twinA].GetProperty("name").GetString());
        Assert.False(candidates[twinA].GetProperty("isArchived").GetBoolean());
        Assert.Equal(twinName.ToLowerInvariant(), candidates[twinB].GetProperty("name").GetString());
        Assert.True(candidates[twinB].GetProperty("isArchived").GetBoolean());
    }

    [Fact]
    public async Task GetLoopTemplate_matches_a_name_ignoring_case_beyond_ascii_and_any_surrounding_whitespace()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        Guid umlaut, accented;
        Guid[] twins;
        using (var scope = factory.Services.CreateScope())
        {
            var templates = scope.ServiceProvider.GetRequiredService<ILoopTemplateManager>();
            umlaut = await templates.CreateLoopTemplateAsync("umlaut", "", Graph("U."));
            accented = await templates.CreateLoopTemplateAsync("accented", "", Graph("A."));
            twins = new[]
            {
                await templates.CreateLoopTemplateAsync("twin-a", "", Graph("A.")),
                await templates.CreateLoopTemplateAsync("twin-b", "", Graph("B.")),
                await templates.CreateLoopTemplateAsync("twin-c", "", Graph("C.")),
            };
            await templates.ArchiveLoopTemplateAsync(twins[1]);
            // Stored names as an older save or a hand edit may have left them.
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.LoopTemplates.Single(t => t.Id == umlaut).Name = " \t\u00A0Ärger loop\u2003";
            db.LoopTemplates.Single(t => t.Id == accented).Name = "Élan";
            db.LoopTemplates.Single(t => t.Id == twins[0]).Name = "Twin";
            db.LoopTemplates.Single(t => t.Id == twins[1]).Name = "twin";
            db.LoopTemplates.Single(t => t.Id == twins[2]).Name = " TWIN";
            await db.SaveChangesAsync(Ct);
        }

        async Task<HttpResponseMessage> LookUp(string key)
            => await client.GetAsync($"/api/v1/agent/loop-templates/document?nameOrId={Uri.EscapeDataString(key)}", Ct);

        foreach (var (key, expected) in new[]
        {
            ("\u00A0 ärger LOOP\t", umlaut),
            ("ÄRGER LOOP", umlaut),
            ("élan", accented),
        })
        {
            var response = await LookUp(key);
            var body = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"'{key}' -> {(int)response.StatusCode}: {body}");
            Assert.Equal(expected, JsonDocument.Parse(body).RootElement.GetProperty("templateId").GetGuid());
        }

        // Names that differ only in case are one name: the caller must pick by id.
        var ambiguous = await LookUp("\ttWiN ");
        Assert.Equal(HttpStatusCode.Conflict, ambiguous.StatusCode);
        var conflict = await ReadJsonAsync(ambiguous);
        Assert.Equal("3 loop templates are named 'tWiN'; pass the id of the one you mean.", conflict.GetProperty("error").GetString());
        // Live before archived, then by name in ordinal order.
        Assert.Equal(
            new (Guid, string, bool)[] { (twins[2], " TWIN", false), (twins[0], "Twin", false), (twins[1], "twin", true) },
            conflict.GetProperty("candidates").EnumerateArray()
                .Select(c => (c.GetProperty("id").GetGuid(), c.GetProperty("name").GetString()!, c.GetProperty("isArchived").GetBoolean())));

        var unknown = await LookUp("  Ärger  ");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("Loop template not found: Ärger", (await ReadJsonAsync(unknown)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task GetLoopTemplate_document_is_accepted_unchanged_by_update_current_loop()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        Guid templateId;
        Guid chatSessionId;
        using (var scope = factory.Services.CreateScope())
        {
            templateId = await scope.ServiceProvider.GetRequiredService<ILoopTemplateManager>()
                .CreateLoopTemplateAsync($"roundtrip-{Guid.NewGuid():N}", "Round trip.", Graph("Review the code."));
            chatSessionId = await SeedChatSessionAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        }

        var document = (await GetOkAsync(client, $"/api/v1/agent/loop-templates/document?nameOrId={templateId}"))
            .GetProperty("document").GetRawText();

        var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/agent/current-loop")
        {
            Content = JsonContent.Create(new { document }),
        };
        put.Headers.Add("X-ILD-Chat-Session-Id", chatSessionId.ToString());
        var resp = await client.SendAsync(put, Ct);
        resp.EnsureSuccessStatusCode();

        var ack = await ReadJsonAsync(resp);
        Assert.Empty(ack.GetProperty("validationErrors").EnumerateArray());
        Assert.True(ack.GetProperty("applied").GetBoolean());
    }

    // ---- get_loop_run -----------------------------------------------------------

    [Fact]
    public async Task GetLoopRun_reports_the_path_feedback_counters_and_pr_review_as_a_bounded_summary()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var t0 = At(6, 1, 10);
        var longError = new string('e', 2500);
        var exactText = new string('p', 2000);
        var overText = new string('r', 2001);
        var longComment = new string('c', 2500);
        Guid runId, templateId, plan, review, fix, toReview, reject, toFix, v1, v2, v3, v4;
        string templateName;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var t = AddTemplate(db, $"digest-{Guid.NewGuid():N}");
            AddVersion(db, t.Id, 1);
            var version = AddVersion(db, t.Id, 2);
            var nPlan = AddNode(db, version.Id, NodeType.AI, "Plan");
            var nReview = AddNode(db, version.Id, NodeType.AI, "Review");
            var nFix = AddNode(db, version.Id, NodeType.Cmd, "Fix");
            var eToReview = AddEdge(db, nPlan.Id, nReview.Id, EdgeType.OnSuccess, null);
            var eReject = AddEdge(db, nReview.Id, nPlan.Id, EdgeType.Custom, "Reject");
            var eToFix = AddEdge(db, nReview.Id, nFix.Id, EdgeType.OnFailure, null);

            var run = AddRun(db, version.Id, LoopRunStatus.WaitingHuman, t0, t0.AddMinutes(-1), "WI-DIGEST");
            run.IsPaused = true;
            run.IsHalted = true;
            run.HaltReason = HaltReason.Throttled;
            run.HumanFeedbackReason = "Waiting on the reviewer";
            run.AiTraversalCount = 3;
            run.ThrottleAutoResumeCount = 1;
            run.NodeExecutionCount = 4;
            run.ThrottleResetAt = t0.AddHours(5);
            run.CurrentAiSessionId = "SECRET-SESSION-ID";
            run.PreviousNodeOutput = "SECRET-PREVIOUS-OUTPUT";
            run.ExternalActionResult = "SECRET-EXTERNAL-RESULT";
            run.PrUrl = "https://forge.example.com/acme/app/pulls/7";
            run.PrSnapshot = PrSnapshotJson.Serialize(new RemotePrSnapshot(
                "Title", "Body", "open", false, true, "clean", RemotePrCiStatus.Failed, [], false, true,
                [
                    new RemotePrConversationEntry("review", "rev", "Rename the helper.", t0.AddHours(1), "CHANGES_REQUESTED"),
                    new RemotePrConversationEntry("comment", "bot", longComment, t0.AddHours(2), null),
                ],
                t0.AddHours(3)));
            db.LoopRunVariables.Add(new LoopRunVariable { LoopRunId = run.Id, Name = "secret", Value = "SECRET-VARIABLE-VALUE" });

            // Added out of order: the response must follow execution order.
            var visit3 = AddVisit(db, run.Id, nPlan.Id, LoopRunNodeStatus.Failed, t0.AddSeconds(40), null,
                incomingEdgeId: eReject.Id, created: t0.AddSeconds(40), error: longError);
            var visit1 = AddVisit(db, run.Id, nPlan.Id, LoopRunNodeStatus.Succeeded, t0, t0.AddSeconds(10),
                "claude", 10, 20, 0.10m, created: t0, error: "short");
            var visit4 = AddVisit(db, run.Id, nFix.Id, LoopRunNodeStatus.Succeeded, t0.AddSeconds(50), t0.AddSeconds(55),
                incomingEdgeId: eToFix.Id, created: t0.AddSeconds(50));
            var visit2 = AddVisit(db, run.Id, nReview.Id, LoopRunNodeStatus.Succeeded, t0.AddSeconds(10), t0.AddSeconds(25),
                "claude", 30, 40, 0.30m, incomingEdgeId: eToReview.Id, created: t0.AddSeconds(10));
            visit2.PreviousNodeId = visit1.Id;
            visit3.PreviousNodeId = visit2.Id;
            // Entered from Plan's visit, but over an edge that starts at Review: no edge is credited to Plan.
            visit4.PreviousNodeId = visit3.Id;
            foreach (var v in new[] { visit1, visit2, visit3, visit4 })
            {
                v.Output = "SECRET-NODE-OUTPUT";
                v.EffectiveInput = "SECRET-EFFECTIVE-INPUT";
            }
            await db.SaveChangesAsync(Ct);

            AddEvent(db, run.Id, EventType.HumanFeedbackRequested, t0.AddSeconds(26), "Please confirm the plan.", nReview.Id, visit2.Id);
            AddEvent(db, run.Id, EventType.NodeCompleted, t0.AddSeconds(27), "SECRET-NODE-EVENT", nReview.Id, visit2.Id);
            AddEvent(db, run.Id, EventType.RunParked, t0.AddSeconds(28), exactText, nReview.Id, visit2.Id);
            AddEvent(db, run.Id, EventType.HumanFeedbackReceived, t0.AddSeconds(30), overText, nReview.Id, visit2.Id, edgeName: "Reject");
            await db.SaveChangesAsync(Ct);

            (runId, templateId, templateName) = (run.Id, t.Id, t.Name);
            (plan, review, fix) = (nPlan.Id, nReview.Id, nFix.Id);
            (toReview, reject, toFix) = (eToReview.Id, eReject.Id, eToFix.Id);
            (v1, v2, v3, v4) = (visit1.Id, visit2.Id, visit3.Id, visit4.Id);
        }

        var resp = await client.GetAsync($"/api/v1/agent/loop-runs/{runId}", Ct);
        var raw = await resp.Content.ReadAsStringAsync(Ct);
        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"{(int)resp.StatusCode}: {raw}");
        var body = JsonDocument.Parse(raw).RootElement;

        Assert.Equal(runId, body.GetProperty("id").GetGuid());
        Assert.Equal("WI-DIGEST", body.GetProperty("workItemId").GetString());
        Assert.Equal("WaitingHuman", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("isPaused").GetBoolean());
        Assert.True(body.GetProperty("isHalted").GetBoolean());
        Assert.Equal("Throttled", body.GetProperty("haltReason").GetString());
        Assert.Equal("Waiting on the reviewer", body.GetProperty("humanFeedbackReason").GetString());
        Assert.Equal(templateId, body.GetProperty("templateId").GetGuid());
        Assert.Equal(templateName, body.GetProperty("templateName").GetString());
        Assert.Equal(2, body.GetProperty("templateVersion").GetInt32());
        Assert.Equal(t0.AddMinutes(-1), body.GetProperty("createdAt").GetDateTime());
        Assert.Equal(t0, body.GetProperty("startedAt").GetDateTime());
        Assert.True(IsNullOrMissing(body, "completedAt"));

        Assert.Equal(3, body.GetProperty("aiTraversalCount").GetInt32());
        Assert.Equal(1, body.GetProperty("throttleAutoResumeCount").GetInt32());
        Assert.Equal(4, body.GetProperty("nodeExecutionCount").GetInt32());
        Assert.Equal(t0.AddHours(5), body.GetProperty("throttleResetAt").GetDateTime());

        var nodes = body.GetProperty("nodes").EnumerateArray().ToArray();
        Assert.Equal(new[] { v1, v2, v3, v4 }, nodes.Select(n => n.GetProperty("runNodeId").GetGuid()));

        var first = nodes[0];
        Assert.Equal(plan, first.GetProperty("nodeId").GetGuid());
        Assert.Equal("Plan", first.GetProperty("label").GetString());
        Assert.Equal("AI", NodeTypeOf(first));
        Assert.Equal("Succeeded", first.GetProperty("status").GetString());
        Assert.Equal(t0, first.GetProperty("startedAt").GetDateTime());
        Assert.Equal(t0.AddSeconds(10), first.GetProperty("completedAt").GetDateTime());
        Assert.Equal(10, first.GetProperty("durationSeconds").GetDouble(), 6);
        Assert.Equal("short", first.GetProperty("error").GetString());
        Assert.False(IsTruncated(first, "error"));
        Assert.Equal(10, first.GetProperty("inputTokens").GetInt64());
        Assert.Equal(20, first.GetProperty("outputTokens").GetInt64());
        Assert.Equal(0.10m, first.GetProperty("costUsd").GetDecimal());
        Assert.Equal("claude", first.GetProperty("aiProvider").GetString());
        Assert.True(IsNullOrMissing(first, "incomingEdge"));
        AssertEdgeTaken(first, toReview, "OnSuccess", null, review, "Review");

        var second = nodes[1];
        Assert.Equal("Review", second.GetProperty("label").GetString());
        Assert.Equal(15, second.GetProperty("durationSeconds").GetDouble(), 6);
        AssertIncomingEdge(second, toReview, "OnSuccess", null, plan);
        AssertEdgeTaken(second, reject, "Custom", "Reject", plan, "Plan");

        var third = nodes[2];
        Assert.Equal("Failed", third.GetProperty("status").GetString());
        Assert.True(IsNullOrMissing(third, "durationSeconds"));
        AssertIncomingEdge(third, reject, "Custom", "Reject", review);
        Assert.True(IsNullOrMissing(third, "edgeTaken"));
        AssertCut(third, "error", longError);

        var fourth = nodes[3];
        Assert.Equal(fix, fourth.GetProperty("nodeId").GetGuid());
        AssertIncomingEdge(fourth, toFix, "OnFailure", null, review);
        Assert.True(IsNullOrMissing(fourth, "edgeTaken"));

        var feedback = body.GetProperty("feedback").EnumerateArray().ToArray();
        Assert.Equal(new[] { "requested", "parked", "received" }, feedback.Select(f => f.GetProperty("kind").GetString()));
        Assert.True(feedback.Select(f => f.GetProperty("eventId").GetInt64()).SequenceEqual(
            feedback.Select(f => f.GetProperty("eventId").GetInt64()).Order()));
        Assert.Equal("Please confirm the plan.", feedback[0].GetProperty("text").GetString());
        Assert.Equal(v2, feedback[0].GetProperty("runNodeId").GetGuid());
        Assert.Equal(review, feedback[0].GetProperty("nodeId").GetGuid());
        Assert.Equal(t0.AddSeconds(26), feedback[0].GetProperty("timestamp").GetDateTime());
        Assert.Equal(exactText, feedback[1].GetProperty("text").GetString());
        Assert.False(IsTruncated(feedback[1], "text"));
        Assert.Equal("Reject", feedback[2].GetProperty("edgeName").GetString());
        AssertCut(feedback[2], "text", overText);

        var pr = body.GetProperty("pr");
        Assert.Equal("https://forge.example.com/acme/app/pulls/7", pr.GetProperty("url").GetString());
        Assert.False(pr.GetProperty("merged").GetBoolean());
        Assert.Equal("open", pr.GetProperty("state").GetString());
        Assert.False(pr.GetProperty("approved").GetBoolean());
        Assert.True(pr.GetProperty("changesRequested").GetBoolean());
        Assert.Equal("Failed", pr.GetProperty("ci").GetString(), ignoreCase: true);
        Assert.Equal(t0.AddHours(3), pr.GetProperty("fetchedAt").GetDateTime());
        var comments = pr.GetProperty("comments").EnumerateArray().ToArray();
        Assert.Equal(2, comments.Length);
        Assert.Equal("review", comments[0].GetProperty("kind").GetString());
        Assert.Equal("rev", comments[0].GetProperty("author").GetString());
        Assert.Equal("CHANGES_REQUESTED", comments[0].GetProperty("state").GetString());
        Assert.Equal(t0.AddHours(1), comments[0].GetProperty("createdAt").GetDateTime());
        Assert.Equal("Rename the helper.", comments[0].GetProperty("body").GetString());
        Assert.False(IsTruncated(comments[0], "body"));
        Assert.Equal("bot", comments[1].GetProperty("author").GetString());
        AssertCut(comments[1], "body", longComment);

        Assert.DoesNotContain("SECRET-", raw);
    }

    [Fact]
    public async Task GetLoopRun_reports_a_pr_without_a_snapshot_and_no_pr_without_a_url()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        Guid merged, noPr;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var t = AddTemplate(db, $"pr-{Guid.NewGuid():N}");
            var v = AddVersion(db, t.Id, 1);
            var withPr = AddRun(db, v.Id, LoopRunStatus.Completed, At(6, 1, 10), At(6, 1, 10));
            withPr.PrUrl = "https://forge.example.com/acme/app/pulls/8";
            withPr.IsPrMerged = true;
            var without = AddRun(db, v.Id, LoopRunStatus.Completed, At(6, 1, 11), At(6, 1, 11));
            await db.SaveChangesAsync(Ct);
            (merged, noPr) = (withPr.Id, without.Id);
        }

        var pr = (await GetOkAsync(client, $"/api/v1/agent/loop-runs/{merged}")).GetProperty("pr");
        Assert.Equal("https://forge.example.com/acme/app/pulls/8", pr.GetProperty("url").GetString());
        Assert.True(pr.GetProperty("merged").GetBoolean());
        foreach (var field in new[] { "state", "approved", "changesRequested", "ci", "fetchedAt" })
            Assert.True(IsNullOrMissing(pr, field), $"pr.{field} should be null without a snapshot");
        Assert.Empty(pr.GetProperty("comments").EnumerateArray());

        var none = await GetOkAsync(client, $"/api/v1/agent/loop-runs/{noPr}");
        Assert.True(IsNullOrMissing(none, "pr"));
        Assert.Empty(none.GetProperty("nodes").EnumerateArray());
        Assert.Empty(none.GetProperty("feedback").EnumerateArray());
    }

    [Fact]
    public async Task GetLoopRun_credits_the_last_visit_with_the_edge_the_run_followed_toward_its_current_node()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var t0 = At(6, 2, 10);
        Guid between, wrongSource, wrongTarget, retriedPending, retriedEntered, review, fix, toReview, toFix;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var t = AddTemplate(db, $"between-{Guid.NewGuid():N}");
            var version = AddVersion(db, t.Id, 1);
            var nPlan = AddNode(db, version.Id, NodeType.AI, "Plan");
            var nReview = AddNode(db, version.Id, NodeType.AI, "Review");
            var nFix = AddNode(db, version.Id, NodeType.Cmd, "Fix");
            var eToReview = AddEdge(db, nPlan.Id, nReview.Id, EdgeType.OnSuccess, null);
            var eToFix = AddEdge(db, nReview.Id, nFix.Id, EdgeType.OnFailure, null);

            // Each run did Plan then Review, and stopped after following an edge but before entering its target.
            LoopRun StoppedBetweenNodes(Guid storedEdge, Guid currentNode)
            {
                var run = AddRun(db, version.Id, LoopRunStatus.Running, t0, t0);
                run.IncomingEdgeId = storedEdge;
                run.CurrentNodeId = currentNode;
                var first = AddVisit(db, run.Id, nPlan.Id, LoopRunNodeStatus.Succeeded, t0, t0.AddSeconds(10), created: t0);
                var last = AddVisit(db, run.Id, nReview.Id, LoopRunNodeStatus.Failed, t0.AddSeconds(10), t0.AddSeconds(20),
                    incomingEdgeId: eToReview.Id, created: t0.AddSeconds(10));
                last.PreviousNodeId = first.Id;
                return run;
            }

            between = StoppedBetweenNodes(eToFix.Id, nFix.Id).Id;
            wrongSource = StoppedBetweenNodes(eToReview.Id, nReview.Id).Id;
            wrongTarget = StoppedBetweenNodes(eToFix.Id, nPlan.Id).Id;

            // Review was retried while it waited (a newer visit), then the original visit got its answer,
            // completed last and followed Review -> Fix: the edge left the original, not the retry.
            LoopRun RetriedWhileWaiting(bool fixEntered)
            {
                var run = AddRun(db, version.Id, LoopRunStatus.Running, t0, t0);
                run.CurrentNodeId = nFix.Id;
                var plan = AddVisit(db, run.Id, nPlan.Id, LoopRunNodeStatus.Succeeded, t0, t0.AddSeconds(10), created: t0);
                var answered = AddVisit(db, run.Id, nReview.Id, LoopRunNodeStatus.Succeeded, t0.AddSeconds(10), t0.AddSeconds(40),
                    incomingEdgeId: eToReview.Id, created: t0.AddSeconds(10));
                var retry = AddVisit(db, run.Id, nReview.Id, LoopRunNodeStatus.WaitingHuman, t0.AddSeconds(30), t0.AddSeconds(31),
                    created: t0.AddSeconds(30));
                answered.PreviousNodeId = plan.Id;
                retry.PreviousNodeId = answered.Id;
                if (fixEntered)
                {
                    var fixVisit = AddVisit(db, run.Id, nFix.Id, LoopRunNodeStatus.Running, t0.AddSeconds(45), null,
                        incomingEdgeId: eToFix.Id, created: t0.AddSeconds(45));
                    fixVisit.PreviousNodeId = answered.Id;
                }
                else
                {
                    run.IncomingEdgeId = eToFix.Id;
                }
                return run;
            }

            retriedPending = RetriedWhileWaiting(fixEntered: false).Id;
            retriedEntered = RetriedWhileWaiting(fixEntered: true).Id;
            await db.SaveChangesAsync(Ct);
            (review, fix, toReview, toFix) = (nReview.Id, nFix.Id, eToReview.Id, eToFix.Id);
        }

        var nodes = (await GetOkAsync(client, $"/api/v1/agent/loop-runs/{between}")).GetProperty("nodes").EnumerateArray().ToList();
        AssertEdgeTaken(nodes[0], toReview, "OnSuccess", null, review, "Review");
        AssertEdgeTaken(nodes[1], toFix, "OnFailure", null, fix, "Fix");

        foreach (var runId in new[] { wrongSource, wrongTarget })
        {
            nodes = (await GetOkAsync(client, $"/api/v1/agent/loop-runs/{runId}")).GetProperty("nodes").EnumerateArray().ToList();
            AssertEdgeTaken(nodes[0], toReview, "OnSuccess", null, review, "Review");
            Assert.True(IsNullOrMissing(nodes[1], "edgeTaken"), $"run {runId}: a stored edge not leading from the last visit to the current node is not credited");
        }

        nodes = (await GetOkAsync(client, $"/api/v1/agent/loop-runs/{retriedPending}")).GetProperty("nodes").EnumerateArray().ToList();
        Assert.Equal(3, nodes.Count);
        Assert.True(IsNullOrMissing(nodes[1], "edgeTaken"), "the answered visit's edge is not entered yet and it is not the last visit");
        Assert.True(IsNullOrMissing(nodes[2], "edgeTaken"), "the retry is the last visit but did not complete last, so the stored edge is not its");

        nodes = (await GetOkAsync(client, $"/api/v1/agent/loop-runs/{retriedEntered}")).GetProperty("nodes").EnumerateArray().ToList();
        Assert.Equal(4, nodes.Count);
        AssertEdgeTaken(nodes[1], toFix, "OnFailure", null, fix, "Fix");
        Assert.True(IsNullOrMissing(nodes[2], "edgeTaken"));
        Assert.True(IsNullOrMissing(nodes[3], "edgeTaken"));
    }

    [Fact]
    public async Task GetLoopRun_rejects_a_malformed_id_and_reports_an_unknown_run()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/agent/loop-runs/not-a-guid", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/agent/loop-runs/{Guid.NewGuid()}", Ct)).StatusCode);
    }

    // ---- helpers ----------------------------------------------------------------

    private static DateTime At(int month, int day, int hour) => new(2026, month, day, hour, 0, 0, DateTimeKind.Utc);

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp)
        => JsonDocument.Parse(await resp.Content.ReadAsStringAsync(Ct)).RootElement;

    private static async Task<JsonElement> GetOkAsync(HttpClient client, string url)
    {
        var resp = await client.GetAsync(url, Ct);
        var body = await resp.Content.ReadAsStringAsync(Ct);
        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"GET {url} -> {(int)resp.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task<RunAnalyticsOverview> OverviewAsync(ApiFactory factory, AnalyticsQuery query)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IRunAnalyticsService>().GetOverviewAsync(query, Ct);
    }

    private static JsonElement Row(JsonElement response, Guid templateId)
        => Assert.Single(response.GetProperty("templates").EnumerateArray(), t => t.GetProperty("templateId").GetGuid() == templateId);

    private static void AssertRowsMatchOverview(IReadOnlyList<TemplateAnalytics> expected, JsonElement rows)
    {
        var actual = rows.EnumerateArray().ToArray();
        Assert.Equal(expected.Select(t => t.TemplateId), actual.Select(r => r.GetProperty("templateId").GetGuid()));
        foreach (var (e, r) in expected.Zip(actual))
        {
            Assert.Equal(e.TemplateName, r.GetProperty("templateName").GetString());
            Assert.Equal(e.TotalRuns, r.GetProperty("totalRuns").GetInt32());
            Assert.Equal(e.CompletedRuns, r.GetProperty("completedRuns").GetInt32());
            Assert.Equal(e.FailedRuns, r.GetProperty("failedRuns").GetInt32());
            Assert.Equal(e.CancelledRuns, r.GetProperty("cancelledRuns").GetInt32());
            Assert.Equal(e.SuccessRate, r.GetProperty("successRate").GetDouble(), 9);
            AssertNullableDouble(e.AvgNodeSeconds, r, "avgNodeSeconds");
            Assert.Equal(e.OnFailureRoutings, r.GetProperty("onFailureRoutings").GetInt32());
            Assert.Equal(e.RejectRoutings, r.GetProperty("rejectRoutings").GetInt32());
            AssertNullableDouble(e.AvgHumanFeedbackSeconds, r, "avgFeedbackSeconds");
            Assert.Equal(e.TotalInputTokens, r.GetProperty("totalInputTokens").GetInt64());
            Assert.Equal(e.TotalOutputTokens, r.GetProperty("totalOutputTokens").GetInt64());
            Assert.Equal(e.TotalCostUsd, r.GetProperty("totalCostUsd").GetDecimal());
        }
    }

    private static void AssertNullableDouble(double? expected, JsonElement row, string name)
    {
        if (expected is null) Assert.True(IsNullOrMissing(row, name), $"{name} should be null");
        else Assert.Equal(expected.Value, row.GetProperty(name).GetDouble(), 9);
    }

    private static void AssertNode(JsonElement node, string label, string type, int version, int executions, int failures, double? avgSeconds)
    {
        Assert.Equal(label, node.GetProperty("label").GetString());
        Assert.Equal(type, NodeTypeOf(node));
        Assert.Equal(version, node.GetProperty("templateVersion").GetInt32());
        Assert.Equal(executions, node.GetProperty("executions").GetInt32());
        Assert.Equal(failures, node.GetProperty("failures").GetInt32());
        AssertNullableDouble(avgSeconds, node, "avgSeconds");
    }

    private static string? NodeTypeOf(JsonElement node)
        => (node.TryGetProperty("nodeType", out var t) ? t : node.GetProperty("type")).GetString();

    private static void AssertIncomingEdge(JsonElement visit, Guid id, string type, string? name, Guid sourceNodeId)
    {
        var edge = visit.GetProperty("incomingEdge");
        Assert.Equal(id, edge.GetProperty("id").GetGuid());
        Assert.Equal(type, edge.GetProperty("type").GetString());
        Assert.Equal(name, edge.GetProperty("name").GetString());
        Assert.Equal(sourceNodeId, edge.GetProperty("sourceNodeId").GetGuid());
    }

    private static void AssertEdgeTaken(JsonElement visit, Guid id, string type, string? name, Guid targetNodeId, string targetLabel)
    {
        var edge = visit.GetProperty("edgeTaken");
        Assert.Equal(id, edge.GetProperty("id").GetGuid());
        Assert.Equal(type, edge.GetProperty("type").GetString());
        Assert.Equal(name, edge.GetProperty("name").GetString());
        Assert.Equal(targetNodeId, edge.GetProperty("targetNodeId").GetGuid());
        Assert.Equal(targetLabel, edge.GetProperty("targetLabel").GetString());
    }

    /// <summary>A text over the bound: at most 2,000 characters of the original, flagged as cut.</summary>
    private static void AssertCut(JsonElement owner, string field, string original)
    {
        var text = owner.GetProperty(field).GetString()!;
        Assert.InRange(text.Length, 1, 2000);
        Assert.StartsWith(text.TrimEnd('…', '.'), original);
        Assert.True(IsTruncated(owner, field), $"{field} was cut but not flagged");
    }

    private static bool IsTruncated(JsonElement owner, string field)
    {
        if (!owner.TryGetProperty($"{field}Truncated", out var flag) && !owner.TryGetProperty("truncated", out flag))
            return false;
        return flag.ValueKind == JsonValueKind.True;
    }

    private static bool IsNullOrMissing(JsonElement owner, string name)
        => !owner.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null;

    private static async Task AssertDocumentIsStoredVersionAsync(ApiFactory factory, Guid templateId, int versionNumber, string name, JsonElement document)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var template = db.LoopTemplates.Single(t => t.Id == templateId);
        var versionId = db.LoopTemplateVersions.Single(v => v.LoopTemplateId == templateId && v.VersionNumber == versionNumber).Id;
        var storedNodes = db.LoopNodes.Where(n => n.LoopTemplateVersionId == versionId).ToList();
        var nodeIds = storedNodes.Select(n => n.Id).ToHashSet();
        var storedEdges = db.LoopNodeEdges.Where(e => nodeIds.Contains(e.SourceNodeId)).ToList();

        Assert.Equal("ild-loop-template/v2", document.GetProperty("$schema").GetString());
        Assert.Equal(name, document.GetProperty("name").GetString());
        Assert.Equal(template.Description ?? "", document.GetProperty("description").GetString());
        Assert.Equal(template.RecoveryPolicy.ToString(), document.GetProperty("recoveryPolicy").GetString());

        var nodes = document.GetProperty("nodes").EnumerateArray().ToDictionary(n => Guid.Parse(n.GetProperty("id").GetString()!));
        Assert.Equal(nodeIds.Order(), nodes.Keys.Order());
        foreach (var stored in storedNodes)
        {
            var node = nodes[stored.Id];
            Assert.Equal(stored.NodeType.ToString(), node.GetProperty("type").GetString());
            Assert.Equal(stored.Label, node.GetProperty("label").GetString());
            var config = JsonNode.Parse(node.GetProperty("config").GetRawText());
            Assert.True(
                JsonNode.DeepEquals(JsonNode.Parse(string.IsNullOrEmpty(stored.Config) ? "{}" : stored.Config), config),
                $"node {stored.Label}: config differs from the stored one: {config?.ToJsonString()}");
        }
        Assert.Contains(storedNodes, n => n.Config?.Contains("\"outputs\"") == true);

        var edges = document.GetProperty("edges").EnumerateArray().ToDictionary(e => Guid.Parse(e.GetProperty("id").GetString()!));
        Assert.Equal(storedEdges.Select(e => e.Id).Order(), edges.Keys.Order());
        foreach (var stored in storedEdges)
        {
            var edge = edges[stored.Id];
            Assert.Equal(stored.SourceNodeId, Guid.Parse(edge.GetProperty("sourceNodeId").GetString()!));
            Assert.Equal(stored.TargetNodeId, Guid.Parse(edge.GetProperty("targetNodeId").GetString()!));
            Assert.Equal(stored.EdgeType.ToString(), edge.GetProperty("edgeType").GetString());
            Assert.Equal(stored.Name, edge.GetProperty("name").GetString());
        }
    }

    /// <summary>Start → AI → Cleanup, with the AI node's named Reject output wired to Cleanup too.</summary>
    private static LoopTemplateGraph Graph(string prompt) => new(
        Guid.Empty,
        [
            new LoopNodeDto { Id = "start", NodeType = "Start", Label = "Start" },
            new LoopNodeDto
            {
                Id = "ai", NodeType = "AI", Label = "Reviewer",
                Config = new()
                {
                    ["prompt"] = prompt,
                    ["aiProviderId"] = "prov",
                    ["outputs"] = new JsonArray(new JsonObject { ["name"] = "Reject" }),
                },
            },
            new LoopNodeDto { Id = "cleanup", NodeType = "Cleanup", Label = "Cleanup" },
        ],
        [
            new LoopNodeEdgeDto { Id = "e1", SourceNodeId = "start", TargetNodeId = "ai", EdgeType = "OnSuccess" },
            new LoopNodeEdgeDto { Id = "e2", SourceNodeId = "ai", TargetNodeId = "cleanup", EdgeType = "OnSuccess" },
            new LoopNodeEdgeDto { Id = "e3", SourceNodeId = "ai", TargetNodeId = "cleanup", EdgeType = "Custom", Name = "Reject" },
        ]);

    private static async Task<Guid> SeedChatSessionAsync(AppDbContext db)
    {
        var session = new ChatSession
        {
            Id = Guid.NewGuid(),
            UserId = "retro-tester",
            AiProviderId = Guid.NewGuid(),
            ProviderType = "claude-code",
            ToolAllowlistCsv = "ild",
            ScratchPath = "/tmp/ild-test-chat-session",
            CreatedAt = DateTime.UtcNow,
        };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync(Ct);
        return session.Id;
    }

    private static LoopTemplate AddTemplate(AppDbContext db, string name)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = name, RecoveryPolicy = RecoveryPolicy.AutoResume, CreatedAt = At(1, 1, 0) };
        db.LoopTemplates.Add(template);
        return template;
    }

    private static LoopTemplateVersion AddVersion(AppDbContext db, Guid templateId, int number)
    {
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = templateId, VersionNumber = number, CreatedAt = At(1, 1, number) };
        db.LoopTemplateVersions.Add(version);
        return version;
    }

    private static LoopNode AddNode(AppDbContext db, Guid versionId, NodeType type, string label)
    {
        var node = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = versionId, NodeType = type, Label = label, CreatedAt = At(1, 1, 0) };
        db.LoopNodes.Add(node);
        return node;
    }

    private static LoopNodeEdge AddEdge(AppDbContext db, Guid source, Guid target, EdgeType type, string? name)
    {
        var edge = new LoopNodeEdge { Id = Guid.NewGuid(), SourceNodeId = source, TargetNodeId = target, EdgeType = type, Name = name, CreatedAt = At(1, 1, 0) };
        db.LoopNodeEdges.Add(edge);
        return edge;
    }

    private static LoopRun AddRun(
        AppDbContext db, Guid versionId, LoopRunStatus status, DateTime? startedAt, DateTime createdAt,
        string? workItemId = null, Guid? id = null)
    {
        var run = new LoopRun
        {
            Id = id ?? Guid.NewGuid(),
            WorkItemId = workItemId ?? $"WI-{Guid.NewGuid():N}",
            LoopTemplateVersionId = versionId,
            Status = status,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            StartedAt = startedAt,
            CreatedAt = createdAt,
        };
        db.LoopRuns.Add(run);
        return run;
    }

    private static LoopRunNode AddVisit(
        AppDbContext db, Guid runId, Guid nodeId, LoopRunNodeStatus status, DateTime? started, DateTime? completed,
        string? aiProvider = null, long? input = null, long? output = null, decimal? cost = null,
        Guid? incomingEdgeId = null, DateTime? created = null, string? error = null)
    {
        var visit = new LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = runId,
            LoopNodeId = nodeId,
            NodeLabel = db.LoopNodes.Local.Single(n => n.Id == nodeId).Label,
            Status = status,
            StartedAt = started,
            CompletedAt = completed,
            IncomingEdgeId = incomingEdgeId,
            InputTokens = input,
            OutputTokens = output,
            CostUsd = cost,
            AiProvider = aiProvider,
            Error = error,
            CreatedAt = created ?? started ?? At(1, 1, 0),
        };
        db.LoopRunNodes.Add(visit);
        return visit;
    }

    private static void AddEvent(
        AppDbContext db, Guid runId, EventType type, DateTime timestamp, string data,
        Guid? nodeId = null, Guid? runNodeId = null, string? edgeName = null)
        => db.EventLogs.Add(new EventLog
        {
            LoopRunId = runId,
            EventType = type,
            Timestamp = timestamp,
            Data = data,
            NodeId = nodeId,
            RunNodeId = runNodeId,
            EdgeName = edgeName,
        });

    private static void AddBucket(
        AppDbContext db, Guid templateId, string templateName, DateTime day, string provider,
        int runs, int completed, int failed, int cancelled, int nodeCount, double nodeSeconds,
        int feedbackCount, double feedbackSeconds, long input, long output, decimal cost)
        => db.LoopRunAnalyticsBuckets.Add(new LoopRunAnalyticsBucket
        {
            Id = Guid.NewGuid(),
            BucketDate = day,
            LoopTemplateId = templateId,
            TemplateName = templateName,
            AiProvider = provider,
            RunCount = runs,
            CompletedRuns = completed,
            FailedRuns = failed,
            CancelledRuns = cancelled,
            NodeCount = nodeCount,
            TotalNodeSeconds = nodeSeconds,
            FeedbackCount = feedbackCount,
            TotalFeedbackSeconds = feedbackSeconds,
            TotalInputTokens = input,
            TotalOutputTokens = output,
            TotalCostUsd = cost,
        });
}
