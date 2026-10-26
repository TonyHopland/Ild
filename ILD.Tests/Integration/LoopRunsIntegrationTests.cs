using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

public class LoopRunsIntegrationTests
{
    [Fact]
    public async Task GetAll_without_token_returns_401()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/looprins", TestContext.Current.CancellationToken);
        // Path is /api/v1/[controller] -> /api/v1/loopruns
        // Use the correct route below
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.Unauthorized });

        var actual = await client.GetAsync("/api/v1/loopruns", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, actual.StatusCode);
    }

    [Fact]
    public async Task GetAll_with_token_returns_200_and_empty_array()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/loopruns", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<object[]>(TestContext.Current.CancellationToken);
        Assert.NotNull(items);
        Assert.Empty(items!);
    }

    [Fact]
    public async Task GetById_for_unknown_id_returns_404()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/loopruns/" + Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Cleanup is the non-destructive counterpart of Delete (ADR-0008): the run
    /// row and its history survive, but the worktree/branch it named and its AI
    /// session snapshots do not, so the branch is free for the next run.
    /// </summary>
    [Fact]
    public async Task Cleanup_frees_the_branch_deletes_the_session_snapshots_and_keeps_the_run_row()
    {
        var reclaimer = new StubRunReclaimer(succeeds: true);
        await using var factory = NewFactory(reclaimer);
        var client = await factory.CreateAuthenticatedClientAsync();
        var runId = SeedRun(factory, LoopRunStatus.Completed, branch: "feature/reclaim-me");
        await SeedSnapshotAsync(factory, runId);

        Assert.Contains("already used locally", await BranchWarningAsync(client, "feature/reclaim-me"));

        var response = await client.PostAsync($"/api/v1/loopruns/{runId}/cleanup", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal([runId], reclaimer.Reclaimed);
        var run = ReadRun(factory, runId);
        Assert.NotNull(run);
        Assert.Null(run!.WorktreePath);
        Assert.Null(run.BranchName);
        Assert.Equal(0, await SnapshotCountAsync(factory, runId));
        Assert.Null(await BranchWarningAsync(client, "feature/reclaim-me"));
    }

    [Fact]
    public async Task Cleanup_of_a_pinned_run_with_only_session_snapshots_left_deletes_them_and_keeps_the_pinned_run()
    {
        await using var factory = NewFactory(new StubRunReclaimer(succeeds: true));
        var client = await factory.CreateAuthenticatedClientAsync();
        var runId = SeedRun(factory, LoopRunStatus.Completed, branch: null, retain: true);
        await SeedSnapshotAsync(factory, runId);

        var response = await client.PostAsync($"/api/v1/loopruns/{runId}/cleanup", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await SnapshotCountAsync(factory, runId));
        var run = ReadRun(factory, runId);
        Assert.NotNull(run);
        Assert.True(run!.Retain);
    }

    /// <summary>
    /// The run view's "history is kept until" date: when the run ended plus the
    /// retention days in force now, and none for a run that has not ended or is pinned.
    /// </summary>
    [Fact]
    public async Task GetById_says_until_when_an_ended_runs_history_is_kept()
    {
        await using var factory = NewFactory(new StubRunReclaimer(succeeds: true));
        var client = await factory.CreateAuthenticatedClientAsync();
        var ct = TestContext.Current.CancellationToken;
        var ended = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var completed = SeedRun(factory, LoopRunStatus.Completed, branch: null, completedAt: ended);
        var cancelled = SeedRun(factory, LoopRunStatus.Cancelled, branch: null, completedAt: ended);
        var pinned = SeedRun(factory, LoopRunStatus.Completed, branch: null, retain: true, completedAt: ended);
        var running = SeedRun(factory, LoopRunStatus.Running, branch: "feature/live");
        var waiting = SeedRun(factory, LoopRunStatus.WaitingHuman, branch: "feature/waiting");

        async Task<DateTime?> KeptUntil(Guid id)
        {
            var run = await client.GetFromJsonAsync<JsonElement>($"/api/v1/loopruns/{id}", ct);
            var value = run.GetProperty("historyKeptUntil");
            if (value.ValueKind == JsonValueKind.Null) return null;
            var at = value.GetDateTime();
            return DateTime.SpecifyKind(at.Kind == DateTimeKind.Local ? at.ToUniversalTime() : at, DateTimeKind.Utc);
        }

        Assert.Equal(ended.AddDays(14), await KeptUntil(completed));
        Assert.Equal(ended.AddDays(14), await KeptUntil(cancelled));
        Assert.Null(await KeptUntil(pinned));
        Assert.Null(await KeptUntil(running));
        Assert.Null(await KeptUntil(waiting));

        Assert.Equal(HttpStatusCode.OK,
            (await client.PutAsJsonAsync("/api/v1/settings/run.retentionDays", new { value = "30" }, ct)).StatusCode);
        Assert.Equal(ended.AddDays(30), await KeptUntil(completed));
    }

    [Fact]
    public async Task Cleanup_is_rejected_for_a_run_that_has_not_finished()
    {
        var reclaimer = new StubRunReclaimer(succeeds: true);
        await using var factory = NewFactory(reclaimer);
        var client = await factory.CreateAuthenticatedClientAsync();
        var runId = SeedRun(factory, LoopRunStatus.Running, branch: "feature/live");

        var response = await client.PostAsync($"/api/v1/loopruns/{runId}/cleanup", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(reclaimer.Reclaimed);
        Assert.Equal("feature/live", ReadRun(factory, runId)!.BranchName);
    }

    [Fact]
    public async Task Cleanup_returns_409_and_leaves_the_run_untouched_when_reclaim_fails()
    {
        var reclaimer = new StubRunReclaimer(succeeds: false);
        await using var factory = NewFactory(reclaimer);
        var client = await factory.CreateAuthenticatedClientAsync();
        var runId = SeedRun(factory, LoopRunStatus.Failed, branch: "feature/stuck");

        var response = await client.PostAsync($"/api/v1/loopruns/{runId}/cleanup", null, TestContext.Current.CancellationToken);

        // Clearing the pointers on a reclaim that did not happen would hide a
        // worktree and branch that are still on disk from every later sweep.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var run = ReadRun(factory, runId);
        Assert.Equal("feature/stuck", run!.BranchName);
        Assert.Equal("/tmp/ild-test-worktree", run.WorktreePath);
    }

    [Fact]
    public async Task Cleanup_for_unknown_id_returns_404()
    {
        await using var factory = NewFactory(new StubRunReclaimer(succeeds: true));
        var client = await factory.CreateAuthenticatedClientAsync();
        var response = await client.PostAsync($"/api/v1/loopruns/{Guid.NewGuid()}/cleanup", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The run list answers whether there is local git state to reclaim, not
    /// where it is: a worktree path is an absolute server path and the cleanup
    /// affordance never needs it. The per-run GET still carries the paths.
    /// </summary>
    [Fact]
    public async Task GetAll_reports_reclaimability_without_exposing_server_paths()
    {
        await using var factory = NewFactory(new StubRunReclaimer(succeeds: true));
        var client = await factory.CreateAuthenticatedClientAsync();
        var runId = SeedRun(factory, LoopRunStatus.Completed, branch: "feature/listed");

        var listed = await client.GetStringAsync("/api/v1/loopruns", TestContext.Current.CancellationToken);

        Assert.DoesNotContain("worktreePath", listed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/tmp/ild-test-worktree", listed, StringComparison.Ordinal);
        using var list = JsonDocument.Parse(listed);
        var row = list.RootElement.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == runId);
        Assert.True(row.GetProperty("hasLocalGitState").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsync($"/api/v1/loopruns/{runId}/cleanup", null, TestContext.Current.CancellationToken)).StatusCode);

        using var after = JsonDocument.Parse(await client.GetStringAsync("/api/v1/loopruns", TestContext.Current.CancellationToken));
        Assert.False(after.RootElement.EnumerateArray()
            .Single(r => r.GetProperty("id").GetGuid() == runId)
            .GetProperty("hasLocalGitState").GetBoolean());
    }

    public static TheoryData<NodeType, string> NodesUsingThreeGlobals => new()
    {
        { NodeType.AI, "{\"prompt\":\"{{Global.Alpha}} {{Global.Beta}} {{Global.Gamma}}\"}" },
        {
            NodeType.SetVariable,
            "{\"assignments\":[{\"name\":\"a\",\"value\":\"{{Global.Alpha}}\"},{\"name\":\"b\",\"value\":\"{{Global.Beta}} {{Global.Gamma}}\"}]}"
        },
    };

    [Theory]
    [MemberData(nameof(NodesUsingThreeGlobals))]
    public async Task A_run_parked_for_missing_globals_names_those_still_missing_now(NodeType type, string config)
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var ct = TestContext.Current.CancellationToken;
        Guid parkedId, humanHaltedId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var template = new LoopTemplate { Id = Guid.NewGuid(), Name = $"globals-{Guid.NewGuid():N}" };
            var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1, CreatedAt = DateTime.UtcNow };
            var node = new LoopNode
            {
                Id = Guid.NewGuid(),
                LoopTemplateVersionId = version.Id,
                NodeType = type,
                Label = "Writer",
                Config = config,
            };
            db.LoopTemplates.Add(template);
            db.LoopTemplateVersions.Add(version);
            db.LoopNodes.Add(node);
            LoopRun Parked(HaltReason reason) => new()
            {
                Id = Guid.NewGuid(),
                WorkItemId = "wi-" + Guid.NewGuid().ToString("N")[..8],
                LoopTemplateVersionId = version.Id,
                Status = LoopRunStatus.WaitingHuman,
                IsHalted = true,
                HaltReason = reason,
                CurrentNodeId = node.Id,
                StartedAt = DateTime.UtcNow,
            };
            var parked = Parked(HaltReason.GlobalVariableMissing);
            var humanHalted = Parked(HaltReason.Human);
            db.LoopRuns.AddRange(parked, humanHalted);
            db.SaveChanges();
            (parkedId, humanHaltedId) = (parked.Id, humanHalted.Id);
        }
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/api/v1/global-variables", new { name = "beta", value = "set" }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/api/v1/global-variables", new { name = "Gamma", value = "" }, ct)).StatusCode);

        async Task<(string? Reason, string[] Missing)> FromDetail(Guid id)
        {
            var run = await client.GetFromJsonAsync<JsonElement>($"/api/v1/loopruns/{id}", ct);
            return (run.GetProperty("haltReason").GetString(), Names(run));
        }
        async Task<string[]> FromList(Guid id)
        {
            var runs = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/loopruns", ct);
            return Names(runs!.Single(r => r.GetProperty("id").GetGuid() == id));
        }
        static string[] Names(JsonElement run)
            => run.GetProperty("missingGlobalVariables").EnumerateArray()
                .Select(n => n.GetString()!.ToUpperInvariant()).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        var (reason, missing) = await FromDetail(parkedId);
        Assert.Equal("GlobalVariableMissing", reason);
        Assert.Equal(new[] { "ALPHA", "GAMMA" }, missing);
        Assert.Equal(new[] { "ALPHA", "GAMMA" }, await FromList(parkedId));
        Assert.Empty((await FromDetail(humanHaltedId)).Missing);
        Assert.Empty(await FromList(humanHaltedId));

        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/api/v1/global-variables", new { name = "alpha", value = "set" }, ct)).StatusCode);

        Assert.Equal(new[] { "GAMMA" }, (await FromDetail(parkedId)).Missing);
        Assert.Equal(new[] { "GAMMA" }, await FromList(parkedId));
    }

    /// <summary>
    /// A run's detail lists its saved sessions from their metadata alone: however
    /// large a transcript is, and whether or not it has been converted yet, the
    /// read never fetches its content.
    /// </summary>
    [Fact]
    public async Task GetById_says_why_ILD_could_not_merge_an_AutoMerge_pull_request()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var failed = SeedRun(factory, LoopRunStatus.WaitingHuman, branch: "feature/automerge-failed");
        var plain = SeedRun(factory, LoopRunStatus.WaitingHuman, branch: "feature/not-automerge");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.LoopRuns.Where(r => r.Id == failed).ExecuteUpdateAsync(u => u
                .SetProperty(r => r.PrAutoMerge, (PrAutoMerge?)PrAutoMerge.IldFailed)
                .SetProperty(r => r.PrAutoMergeError, "At least 1 approving review is required"), ct);
        }

        using var detail = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/loopruns/{failed}", ct));
        Assert.Equal("IldFailed", detail.RootElement.GetProperty("prAutoMerge").GetString());
        Assert.Equal("At least 1 approving review is required", detail.RootElement.GetProperty("prAutoMergeError").GetString());

        using var none = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/loopruns/{plain}", ct));
        Assert.Equal(JsonValueKind.Null, none.RootElement.GetProperty("prAutoMerge").ValueKind);
    }

    [Fact]
    public async Task GetById_lists_sessions_without_reading_any_transcript_content()
    {
        var ct = TestContext.Current.CancellationToken;
        var sql = new SnapshotSqlRecorder();
        await using var factory = new ApiFactory(configureServices: services =>
        {
            services.RemoveHostedService<ILD.Core.Services.Implementations.SessionSnapshotMigrationService>();
            services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(sql));
        });
        var client = await factory.CreateAuthenticatedClientAsync();
        var runId = SeedRun(factory, LoopRunStatus.Completed, branch: "feature/large-session");
        var random = new Random(386);
        var lines = Enumerable.Range(0, 9000)
            .Select(n => $"{{\"type\":\"assistant\",\"n\":{n},\"text\":\"{new string(Enumerable.Range(0, 1000).Select(_ => (char)('a' + random.Next(26))).ToArray())}\"}}\n")
            .ToList();
        var raw = Encoding.UTF8.GetBytes(string.Concat(lines));
        Guid legacyId;
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ILD.Data.Stores.Interfaces.IAdapterSessionSnapshotStore>();
            await SessionSnapshotTestSupport.SeedAsync(
                store, ILD.Data.Stores.Interfaces.SessionSnapshotKey.ForRun(runId, "ClaudeCode", "large"), "claude-jsonl", raw, ct);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True(await db.AdapterSessionSnapshotChunks.CountAsync(ct) > 1);
            legacyId = await SessionSnapshotTestSupport.SeedLegacyAsync(
                db, runId, null, "ClaudeCode", "legacy",
                SessionSnapshotTestSupport.LegacyClaudeWrapper("legacy", "{\"type\":\"user\"}"), ct);
        }
        sql.Clear();

        var response = await client.GetAsync($"/api/v1/loopruns/{runId}", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(sql.Commands);
        Assert.DoesNotContain(sql.Commands, c => SnapshotSqlRecorder.TouchesChunks(c.Sql));
        Assert.DoesNotContain(sql.Commands, c => SnapshotSqlRecorder.SelectsSessionJson(c.Sql));
        var sessions = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("availableSessions").EnumerateArray().ToList();
        var large = sessions.Single(s => s.GetProperty("sessionId").GetString() == "large");
        Assert.Equal("claude-jsonl", large.GetProperty("format").GetString());
        Assert.Equal(raw.LongLength, large.GetProperty("byteLength").GetInt64());
        Assert.Equal(lines.Count, large.GetProperty("eventCount").GetInt32());
        var legacy = sessions.Single(s => s.GetProperty("sessionId").GetString() == "legacy");
        Assert.Equal(JsonValueKind.Null, legacy.GetProperty("format").ValueKind);
        Assert.Equal(JsonValueKind.Null, legacy.GetProperty("byteLength").ValueKind);
        Assert.Equal(JsonValueKind.Null, legacy.GetProperty("eventCount").ValueKind);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.NotNull(await SessionSnapshotTestSupport.ReadLegacySessionJsonAsync(db, legacyId, ct));
        }
    }

    private static ApiFactory NewFactory(IRunReclaimer reclaimer)
        => new(configureServices: services => services.ReplaceSingleton(reclaimer));

    private static Guid SeedRun(ApiFactory factory, LoopRunStatus status, string? branch, bool retain = false, DateTime? completedAt = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = $"cleanup-{Guid.NewGuid():N}" };
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
            WorkItemId = "wi-" + Guid.NewGuid().ToString("N")[..8],
            LoopTemplateVersionId = version.Id,
            Status = status,
            StartedAt = DateTime.UtcNow.AddHours(-2),
            CompletedAt = completedAt ?? (status is LoopRunStatus.Running or LoopRunStatus.WaitingHuman ? null : DateTime.UtcNow.AddHours(-1)),
            WorktreePath = branch is null ? null : "/tmp/ild-test-worktree",
            BranchName = branch,
            Retain = retain,
        };
        db.LoopRuns.Add(run);
        db.SaveChanges();
        return run.Id;
    }

    private static async Task SeedSnapshotAsync(ApiFactory factory, Guid runId)
    {
        using var scope = factory.Services.CreateScope();
        await SessionSnapshotTestSupport.SeedAsync(scope.ServiceProvider.GetRequiredService<ILD.Data.Stores.Interfaces.IAdapterSessionSnapshotStore>(),
            ILD.Data.Stores.Interfaces.SessionSnapshotKey.ForRun(runId, "ClaudeCode", "sess-1"), "claude-jsonl", "{\"turn\":1}\n",
            TestContext.Current.CancellationToken);
    }

    private static async Task<int> SnapshotCountAsync(ApiFactory factory, Guid runId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AdapterSessionSnapshots.CountAsync(s => s.LoopRunId == runId);
    }

    private static LoopRun? ReadRun(ApiFactory factory, Guid runId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return db.LoopRuns.AsNoTracking().FirstOrDefault(r => r.Id == runId);
    }

    private static async Task<string?> BranchWarningAsync(HttpClient client, string name)
    {
        var response = await client.GetAsync($"/api/v1/workitems/branch-name-check?name={Uri.EscapeDataString(name)}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BranchNameCheck>())!.Warning;
    }

    private sealed record BranchNameCheck(string? Error, string? Warning);

    private sealed class StubRunReclaimer(bool succeeds) : IRunReclaimer
    {
        public List<Guid> Reclaimed { get; } = [];

        public Task<bool> ReclaimLocalStateAsync(LoopRun run)
        {
            Reclaimed.Add(run.Id);
            return Task.FromResult(succeeds);
        }
    }
}
