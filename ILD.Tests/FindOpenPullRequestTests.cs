using ILD.Tests.Fakes;
using System.Net;
using System.Text;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.RemoteProviders;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Tests;

/// <summary>
/// Finding the pull request already open for a branch, which a run continuing an
/// existing branch needs so it reuses it instead of asking the forge for a duplicate.
/// A wrong answer is silent — the lookup is best-effort and falls through to creating one.
/// </summary>
public class FindOpenPullRequestTests
{
    private static RemoteProviderService CreateService(TestDb db, HttpMessageHandler handler, string type, string url)
    {
        db.Context.RemoteProviders.Add(new RemoteProvider
        {
            Id = Guid.NewGuid(), Name = type, Type = type, Url = url, ApiKey = "k",
        });
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

    private static string Pull(string headRef, int number)
        => "{\"url\":\"https://api.test/repos/o/r/pulls/" + number + "\",\"html_url\":\"https://test/o/r/pulls/" + number
            + "\",\"head\":{\"ref\":\"" + headRef + "\"}}";

    private static RoutingHttpHandler Pulls(string json)
        => new RoutingHttpHandler().Map(u => u.Contains("/pulls?", StringComparison.Ordinal), () => json);

    public static TheoryData<string, string, string> Forges => new()
    {
        { "GitHub", "https://github.com", "https://github.com/o/r.git" },
        { "Forgejo", "https://forge.test", "https://forge.test/o/r.git" },
    };

    [Theory]
    [MemberData(nameof(Forges))]
    public async Task An_open_pull_request_for_the_branch_is_found_and_asked_for_by_head(string type, string providerUrl, string repoUrl)
    {
        using var db = new TestDb();
        var handler = Pulls("[" + Pull("Planning", 7) + "]");

        var found = await CreateService(db, handler, type, providerUrl).FindOpenPullRequestAsync(repoUrl, "Planning");

        Assert.NotNull(found);
        Assert.Equal("https://api.test/repos/o/r/pulls/7", found.Url);
        Assert.Equal("https://test/o/r/pulls/7", found.HtmlUrl);
        Assert.Null(found.Error);
        var url = Assert.Single(handler.Urls);
        Assert.Contains("state=open", url);
        Assert.Contains("head=o%3APlanning", url);
    }

    [Theory]
    [MemberData(nameof(Forges))]
    public async Task A_forge_that_ignores_the_head_filter_is_not_taken_at_its_word(string type, string providerUrl, string repoUrl)
    {
        using var db = new TestDb();
        var handler = Pulls("[" + Pull("other", 3) + "," + Pull("Planning", 8) + "]");

        var found = await CreateService(db, handler, type, providerUrl).FindOpenPullRequestAsync(repoUrl, "Planning");

        Assert.Equal("https://test/o/r/pulls/8", found!.HtmlUrl);
    }

    [Theory]
    [MemberData(nameof(Forges))]
    public async Task No_pull_request_for_the_branch_is_null(string type, string providerUrl, string repoUrl)
    {
        using var db = new TestDb();

        Assert.Null(await CreateService(db, Pulls("[]"), type, providerUrl).FindOpenPullRequestAsync(repoUrl, "Planning"));
        Assert.Null(await CreateService(db, Pulls("[" + Pull("other", 3) + "]"), type, providerUrl).FindOpenPullRequestAsync(repoUrl, "Planning"));
    }

    [Theory]
    [MemberData(nameof(Forges))]
    public async Task A_refused_lookup_is_null_not_an_error(string type, string providerUrl, string repoUrl)
    {
        using var db = new TestDb();
        var handler = new RoutingHttpHandler().MapUrl(_ => true, _ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        Assert.Null(await CreateService(db, handler, type, providerUrl).FindOpenPullRequestAsync(repoUrl, "Planning"));
    }

    private const string AzureRepo = "https://dev.azure.com/org/project/_git/repo";

    private static RoutingHttpHandler AzurePulls(string json)
        => new RoutingHttpHandler().Map(u => u.Contains("/pullrequests?", StringComparison.Ordinal), () => json);

    [Fact]
    public async Task Azure_DevOps_finds_the_active_pull_request_by_its_source_ref()
    {
        using var db = new TestDb();
        var handler = AzurePulls("{\"value\":[{\"pullRequestId\":42}]}");

        var found = await CreateService(db, handler, "AzureDevOps", "https://dev.azure.com/org")
            .FindOpenPullRequestAsync(AzureRepo, "Planning");

        Assert.NotNull(found);
        Assert.EndsWith("/pullrequests/42", found.Url);
        Assert.EndsWith("/pullrequest/42", found.HtmlUrl);
        var url = Assert.Single(handler.Urls);
        Assert.Contains("searchCriteria.status=active", url);
        Assert.Contains("searchCriteria.sourceRefName=refs%2Fheads%2FPlanning", url);
    }

    [Theory]
    [InlineData("{\"value\":[]}")]
    [InlineData("{\"count\":0}")]
    public async Task Azure_DevOps_with_no_active_pull_request_is_null(string json)
    {
        using var db = new TestDb();

        Assert.Null(await CreateService(db, AzurePulls(json), "AzureDevOps", "https://dev.azure.com/org")
            .FindOpenPullRequestAsync(AzureRepo, "Planning"));
    }

    [Fact]
    public async Task Azure_DevOps_refusing_the_lookup_is_null()
    {
        using var db = new TestDb();
        var handler = new RoutingHttpHandler().MapUrl(_ => true, _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        Assert.Null(await CreateService(db, handler, "AzureDevOps", "https://dev.azure.com/org")
            .FindOpenPullRequestAsync(AzureRepo, "Planning"));
    }
}
