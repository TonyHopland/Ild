using ILD.Tests.Fakes;
using System.Net;
using System.Text;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.RemoteProviders;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using Microsoft.Extensions.Logging;

namespace ILD.Tests;

/// <summary>
/// Finding the pull request already open for a branch, which a run continuing an
/// existing branch needs so it reuses it instead of asking the forge for a duplicate.
/// A wrong answer is silent — the lookup is best-effort and falls through to creating one.
/// </summary>
public class FindOpenPullRequestTests
{
    private sealed class ListLogger : ILogger<RemoteProviderService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("connection refused");
    }

    private static RemoteProviderService CreateService(TestDb db, HttpMessageHandler handler, string type, string url, ILogger<RemoteProviderService>? log = null)
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
            new HttpClient(handler),
            log);
    }

    private static string Pull(string headRef, int number, string baseRef = "main")
        => "{\"url\":\"https://api.test/repos/o/r/pulls/" + number + "\",\"html_url\":\"https://test/o/r/pulls/" + number
            + "\",\"head\":{\"ref\":\"" + headRef + "\"},\"base\":{\"ref\":\"" + baseRef + "\"}}";

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
    public async Task The_branch_the_found_pull_request_targets_is_reported(string type, string providerUrl, string repoUrl)
    {
        using var db = new TestDb();
        var handler = Pulls("[" + Pull("Planning", 7, "release/1.0") + "]");

        var found = await CreateService(db, handler, type, providerUrl).FindOpenPullRequestAsync(repoUrl, "Planning");

        Assert.Equal("release/1.0", found!.TargetBranch);
    }

    [Theory]
    [MemberData(nameof(Forges))]
    public async Task A_pull_request_beyond_the_first_page_is_found(string type, string providerUrl, string repoUrl)
    {
        // A forge that ignores the head filter hands back everything open, a page at a time.
        using var db = new TestDb();
        var firstPage = "[" + string.Join(",", Enumerable.Range(1, 50).Select(n => Pull("other-" + n, n))) + "]";
        var handler = new RoutingHttpHandler()
            .Map(u => u.Contains("page=1&", StringComparison.Ordinal), () => firstPage)
            .Map(u => u.Contains("page=2&", StringComparison.Ordinal), () => "[" + Pull("Planning", 77) + "]");

        var found = await CreateService(db, handler, type, providerUrl).FindOpenPullRequestAsync(repoUrl, "Planning");

        Assert.Equal("https://test/o/r/pulls/77", found!.HtmlUrl);
        Assert.Equal(2, handler.Urls.Count());
    }

    [Theory]
    [MemberData(nameof(Forges))]
    public async Task A_failed_lookup_is_null_and_says_so_in_the_log(string type, string providerUrl, string repoUrl)
    {
        using var db = new TestDb();
        var log = new ListLogger();

        var found = await CreateService(db, new FailingHandler(), type, providerUrl, log).FindOpenPullRequestAsync(repoUrl, "Planning");

        Assert.Null(found);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("Planning", entry.Message);
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
        var handler = AzurePulls("{\"value\":[{\"pullRequestId\":42,\"targetRefName\":\"refs/heads/release/1.0\"}]}");

        var found = await CreateService(db, handler, "AzureDevOps", "https://dev.azure.com/org")
            .FindOpenPullRequestAsync(AzureRepo, "Planning");

        Assert.NotNull(found);
        Assert.Equal("release/1.0", found.TargetBranch);
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
