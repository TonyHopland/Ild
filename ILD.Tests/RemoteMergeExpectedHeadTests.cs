using System.Net;
using System.Text;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.RemoteProviders;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Tests.Fakes;

namespace ILD.Tests;

public class RemoteMergeExpectedHeadTests
{
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static RoutingHttpHandler Answering(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new RoutingHttpHandler().MapUrl(_ => true, _ => Json(body, status));

    private static RemoteProviderService Service(TestDb db, string type, string url, HttpMessageHandler handler)
    {
        db.Context.RemoteProviders.Add(new RemoteProvider { Id = Guid.NewGuid(), Name = type, Type = type, Url = url, ApiKey = "k" });
        db.Context.SaveChanges();
        return new RemoteProviderService(
            db.Providers,
            new IRemoteGitProviderAdapter[]
            {
                new ForgejoRemoteGitProviderAdapter(),
                new GitHubRemoteGitProviderAdapter(),
                new AzureDevOpsRemoteGitProviderAdapter(),
            },
            new HttpClient(handler));
    }

    private const string GitHubRepo = "https://github.com/team/repo";
    private const string ForgejoRepo = "https://forge.example/team/repo";
    private const string AzureRepo = "https://dev.azure.com/contoso/widgets/_git/app";

    [Fact]
    public async Task GitHub_sends_the_expected_commit_as_sha_and_nothing_when_none_is_given()
    {
        using var db = new TestDb();
        var handler = Answering("{}");
        var service = Service(db, "GitHub", "https://github.com", handler);

        Assert.True(await service.MergePullRequestAsync(GitHubRepo, "7", "abc"));
        Assert.True(await service.MergePullRequestAsync(GitHubRepo, "7"));

        Assert.Equal("""{"sha":"abc"}""", handler.Calls[0].Body);
        Assert.Equal("{}", handler.Calls[1].Body);
    }

    [Fact]
    public async Task GitHub_reports_a_409_on_a_named_commit_as_the_head_having_moved()
    {
        using var db = new TestDb();
        var handler = Answering("""{"message":"Head branch was modified."}""", HttpStatusCode.Conflict);
        var service = Service(db, "GitHub", "https://github.com", handler);

        var moved = await Assert.ThrowsAsync<RemoteHeadMovedException>(() => service.MergePullRequestAsync(GitHubRepo, "7", "abc"));
        Assert.Equal("abc", moved.Expected);
    }

    [Fact]
    public async Task GitHub_treats_any_other_refusal_as_a_plain_failure_while_the_head_is_unchanged()
    {
        using var db = new TestDb();
        var handler = new RoutingHttpHandler()
            .MapUrl(_ => true, _ => Json("""{"message":"Pull Request is not mergeable"}""", HttpStatusCode.MethodNotAllowed), HttpMethod.Put)
            .MapUrl(_ => true, _ => Json("""{"head":{"sha":"abc"}}"""));
        var service = Service(db, "GitHub", "https://github.com", handler);

        Assert.False(await service.MergePullRequestAsync(GitHubRepo, "7", "abc"));
    }

    [Fact]
    public async Task Forgejo_sends_the_expected_commit_as_head_commit_id_and_nothing_when_none_is_given()
    {
        using var db = new TestDb();
        var handler = Answering("{}");
        var service = Service(db, "Forgejo", "https://forge.example", handler);

        Assert.True(await service.MergePullRequestAsync(ForgejoRepo, "7", "abc"));
        Assert.True(await service.MergePullRequestAsync(ForgejoRepo, "7"));

        Assert.Equal("""{"do":"merge","head_commit_id":"abc"}""", handler.Calls[0].Body);
        Assert.Equal("""{"do":"merge"}""", handler.Calls[1].Body);
    }

    [Fact]
    public async Task Forgejo_tells_a_moved_head_from_a_merge_conflict_by_reading_the_head()
    {
        using var db = new TestDb();
        var headNow = "def";
        var handler = new RoutingHttpHandler()
            .MapUrl(_ => true, _ => Json("""{"message":"refused"}""", HttpStatusCode.Conflict), HttpMethod.Post)
            .MapUrl(_ => true, _ => Json("""{"head":{"sha":"%s"}}""".Replace("%s", headNow)));
        var service = Service(db, "Forgejo", "https://forge.example", handler);

        var moved = await Assert.ThrowsAsync<RemoteHeadMovedException>(() => service.MergePullRequestAsync(ForgejoRepo, "7", "abc"));
        Assert.Equal("def", moved.Current);

        headNow = "abc";
        Assert.False(await service.MergePullRequestAsync(ForgejoRepo, "7", "abc"));
    }

    [Fact]
    public async Task AzureDevOps_completes_against_the_named_commit_without_reading_the_head_first()
    {
        using var db = new TestDb();
        var handler = Answering("""{"lastMergeSourceCommit":{"commitId":"current"}}""");
        var service = Service(db, "AzureDevOps", "https://dev.azure.com/contoso", handler);

        Assert.True(await service.MergePullRequestAsync(AzureRepo, "7", "checked"));

        var call = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Patch, call.Method);
        Assert.Contains("\"commitId\":\"checked\"", call.Body);
    }

    [Fact]
    public async Task AzureDevOps_without_a_named_commit_keeps_reading_the_current_one()
    {
        using var db = new TestDb();
        var handler = Answering("""{"lastMergeSourceCommit":{"commitId":"current"}}""");
        var service = Service(db, "AzureDevOps", "https://dev.azure.com/contoso", handler);

        Assert.True(await service.MergePullRequestAsync(AzureRepo, "7"));

        Assert.Equal(2, handler.Calls.Count);
        Assert.Equal(HttpMethod.Patch, handler.Calls[1].Method);
        Assert.Contains("\"status\":\"completed\"", handler.Calls[1].Body);
        Assert.Contains("\"commitId\":\"current\"", handler.Calls[1].Body);
    }

    [Fact]
    public async Task AzureDevOps_reports_a_refusal_as_the_head_having_moved_only_when_it_has()
    {
        using var db = new TestDb();
        var headNow = "newer";
        var handler = new RoutingHttpHandler()
            .MapUrl(_ => true, _ => Json("""{"message":"refused"}""", HttpStatusCode.Conflict), HttpMethod.Patch)
            .MapUrl(_ => true, _ => Json("""{"lastMergeSourceCommit":{"commitId":"%s"}}""".Replace("%s", headNow)));
        var service = Service(db, "AzureDevOps", "https://dev.azure.com/contoso", handler);

        await Assert.ThrowsAsync<RemoteHeadMovedException>(() => service.MergePullRequestAsync(AzureRepo, "7", "checked"));

        headNow = "checked";
        Assert.False(await service.MergePullRequestAsync(AzureRepo, "7", "checked"));
    }
}
