using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ILD.Core.Services.Remote;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

/// <summary>
/// The review ledger as an agent actually reaches it: over the agent API, with
/// the caller's run taken from the same <c>X-ILD-Run-Id</c> header the rest of
/// the surface uses — that header is what decides whether a read consumes the
/// items it returned or merely shows them.
/// </summary>
public class AgentPrReviewApiTests
{
    private sealed class StubPrReviewService : IPrReviewService
    {
        public string? WorkItemId { get; private set; }
        public string? SinceCommit { get; private set; }
        public Guid? CallerRunId { get; private set; }
        public string? CommentId { get; private set; }
        public string? ThreadId { get; private set; }
        public string? Body { get; private set; }
        public int Reads { get; private set; }

        public Task<RemotePrReviewLedger> ReadAsync(string workItemId, string? sinceCommit, Guid? callerRunId)
        {
            WorkItemId = workItemId;
            SinceCommit = sinceCommit;
            CallerRunId = callerRunId;
            Reads++;
            return Task.FromResult(new RemotePrReviewLedger(
                new[]
                {
                    new RemotePrReviewSummary("5250768235", "COMMENTED", "review body", "c19dc2d1",
                        new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc), "Copilot", false),
                },
                new[]
                {
                    new RemotePrReviewItem("review", "4049159495", "PRRT_thread_1", "5250768235",
                        "src/A.cs", 10, "this allocation is wrong", "Copilot", "c19dc2d1",
                        new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc), false, false),
                },
                "c19dc2d1", null));
        }

        public Task<RemotePrWriteResult> ReplyAsync(string workItemId, string commentId, string body, Guid? callerRunId)
        {
            WorkItemId = workItemId;
            CommentId = commentId;
            Body = body;
            CallerRunId = callerRunId;
            return Task.FromResult(new RemotePrWriteResult(true, "4053396920", null));
        }

        public Task<RemotePrWriteResult> ResolveAsync(string workItemId, string threadId, Guid? callerRunId)
        {
            WorkItemId = workItemId;
            ThreadId = threadId;
            CallerRunId = callerRunId;
            return Task.FromResult(new RemotePrWriteResult(false, null, "Resolving review threads is not supported by this provider."));
        }

        public Task<RemotePrWriteResult> CommentAsync(string workItemId, string body, Guid? callerRunId)
        {
            WorkItemId = workItemId;
            Body = body;
            CallerRunId = callerRunId;
            return Task.FromResult(new RemotePrWriteResult(true, null, "Queued."));
        }

        public bool Resolved { get; private set; }

        public Task<RemotePrWriteResult> CloseAsync(string workItemId, string commentId, bool resolve, Guid? callerRunId)
        {
            WorkItemId = workItemId;
            CommentId = commentId;
            Resolved = resolve;
            CallerRunId = callerRunId;
            return Task.FromResult(new RemotePrWriteResult(true, null, "Closed."));
        }
    }

    private static ApiFactory FactoryWith(StubPrReviewService stub)
        => new(configureServices: services => services.AddScoped<IPrReviewService>(_ => stub));

    private static async Task<string> SeedWorkItemAsync(ApiFactory factory, HttpClient client)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!db.Repositories.Any())
            {
                var provider = new RemoteProvider
                {
                    Id = Guid.NewGuid(), Name = "p", Type = "forgejo", Url = "https://example.invalid", CreatedAt = DateTime.UtcNow,
                };
                db.RemoteProviders.Add(provider);
                db.Repositories.Add(new Repository
                {
                    Id = Guid.NewGuid(), Name = "repo", CloneUrl = "https://example.invalid/repo.git",
                    RemoteProviderId = provider.Id, DefaultIntakeStatus = WorkItemStatus.Backlog, CreatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }
        }

        Guid repoId;
        using (var scope = factory.Services.CreateScope())
            repoId = scope.ServiceProvider.GetRequiredService<AppDbContext>().Repositories.First().Id;

        var created = await client.PostAsJsonAsync("/api/v1/agent/workitems", new
        {
            title = "reviewed item",
            description = "",
            repositoryId = repoId.ToString(),
        });
        created.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Reading_a_review_carries_the_callers_run_and_the_commit_it_asked_about()
    {
        var stub = new StubPrReviewService();
        await using var factory = FactoryWith(stub);
        var client = await factory.CreateAuthenticatedClientAsync();
        var workItemId = await SeedWorkItemAsync(factory, client);
        var runId = Guid.NewGuid();

        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/agent/workitems/{workItemId}/pr-review?sinceCommit=7e932b3d");
        request.Headers.Add("X-ILD-Run-Id", runId.ToString());
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(workItemId, stub.WorkItemId);
        Assert.Equal("7e932b3d", stub.SinceCommit);
        Assert.Equal(runId, stub.CallerRunId);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("4049159495", body, StringComparison.Ordinal);
        Assert.Contains("PRRT_thread_1", body, StringComparison.Ordinal);
        Assert.Contains("src/A.cs", body, StringComparison.Ordinal);
        Assert.Contains("this allocation is wrong", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_with_no_run_header_is_answered_without_a_caller_run()
    {
        var stub = new StubPrReviewService();
        await using var factory = FactoryWith(stub);
        var client = await factory.CreateAuthenticatedClientAsync();
        var workItemId = await SeedWorkItemAsync(factory, client);

        var response = await client.GetAsync($"/api/v1/agent/workitems/{workItemId}/pr-review", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(stub.CallerRunId);
        Assert.Null(stub.SinceCommit);
    }

    [Fact]
    public async Task An_unknown_work_item_is_not_found_and_never_reaches_the_forge()
    {
        var stub = new StubPrReviewService();
        await using var factory = FactoryWith(stub);
        var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync($"/api/v1/agent/workitems/{Guid.NewGuid()}/pr-review", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, stub.Reads);
    }

    [Fact]
    public async Task Replying_hands_the_thread_and_the_text_to_the_server_side()
    {
        var stub = new StubPrReviewService();
        await using var factory = FactoryWith(stub);
        var client = await factory.CreateAuthenticatedClientAsync();
        var workItemId = await SeedWorkItemAsync(factory, client);
        var runId = Guid.NewGuid();

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/agent/workitems/{workItemId}/pr-review/reply")
        {
            Content = JsonContent.Create(new { commentId = "4049159495", body = "That compiles — C# allows a long array length." }),
        };
        request.Headers.Add("X-ILD-Run-Id", runId.ToString());
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("4049159495", stub.CommentId);
        Assert.Equal("That compiles — C# allows a long array length.", stub.Body);
        Assert.Equal(runId, stub.CallerRunId);
    }

    [Fact]
    public async Task A_reply_with_nothing_to_reply_to_is_a_bad_request()
    {
        var stub = new StubPrReviewService();
        await using var factory = FactoryWith(stub);
        var client = await factory.CreateAuthenticatedClientAsync();
        var workItemId = await SeedWorkItemAsync(factory, client);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/agent/workitems/{workItemId}/pr-review/reply", new { commentId = "", body = "" }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(stub.CommentId);
    }

    [Fact]
    public async Task A_refusal_is_an_answer_with_a_message_not_an_error_status()
    {
        // Same shape as get_ci_log: the agent gets 200 and reads why.
        var stub = new StubPrReviewService();
        await using var factory = FactoryWith(stub);
        var client = await factory.CreateAuthenticatedClientAsync();
        var workItemId = await SeedWorkItemAsync(factory, client);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/agent/workitems/{workItemId}/pr-review/resolve", new { threadId = "PRRT_thread_1" }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("PRRT_thread_1", stub.ThreadId);
        Assert.Contains("not supported", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task There_is_no_route_here_that_approves_merges_or_dismisses()
    {
        var stub = new StubPrReviewService();
        await using var factory = FactoryWith(stub);
        var client = await factory.CreateAuthenticatedClientAsync();
        var workItemId = await SeedWorkItemAsync(factory, client);

        // "close" is deliberately absent: an agent may close one ITEM of a
        // review, which is a different act from closing the pull request, and
        // the route it goes through is asserted below rather than banned here.
        foreach (var verb in new[] { "approve", "merge", "dismiss", "abandon" })
        {
            var response = await client.PostAsJsonAsync(
                $"/api/v1/agent/workitems/{workItemId}/pr-review/{verb}", new { threadId = "PRRT_thread_1" }, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task Saying_something_general_about_the_round_goes_through_this_surface_too()
    {
        // The PR node no longer writes the round's own account, so this is the
        // only way anything general reaches the pull request.
        var stub = new StubPrReviewService();
        await using var factory = FactoryWith(stub);
        var client = await factory.CreateAuthenticatedClientAsync();
        var workItemId = await SeedWorkItemAsync(factory, client);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/agent/workitems/{workItemId}/pr-review/comment",
            new { body = "Rebased onto main and re-ran the gate." }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Rebased onto main and re-ran the gate.", stub.Body);
    }

    [Fact]
    public async Task A_comment_with_nothing_to_say_is_refused_before_it_reaches_the_service()
    {
        var stub = new StubPrReviewService();
        await using var factory = FactoryWith(stub);
        var client = await factory.CreateAuthenticatedClientAsync();
        var workItemId = await SeedWorkItemAsync(factory, client);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/agent/workitems/{workItemId}/pr-review/comment", new { body = "" }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(stub.Body);
    }

    [Fact]
    public async Task Closing_an_item_carries_the_id_and_whether_to_resolve_its_thread()
    {
        var stub = new StubPrReviewService();
        await using var factory = FactoryWith(stub);
        var client = await factory.CreateAuthenticatedClientAsync();
        var workItemId = await SeedWorkItemAsync(factory, client);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/agent/workitems/{workItemId}/pr-review/close",
            new { commentId = "4049159495", resolve = true }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("4049159495", stub.CommentId);
        Assert.True(stub.Resolved);
    }

    [Fact]
    public async Task Closing_nothing_in_particular_is_refused()
    {
        var stub = new StubPrReviewService();
        await using var factory = FactoryWith(stub);
        var client = await factory.CreateAuthenticatedClientAsync();
        var workItemId = await SeedWorkItemAsync(factory, client);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/agent/workitems/{workItemId}/pr-review/close", new { resolve = true }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(stub.CommentId);
    }
}
