using System.Text;
using System.Text.Json;
using ILD.Api.Controllers;
using ILD.Core.Services.Implementations.RemoteProviders;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ILD.Tests;

public class WebhooksControllerTests : IDisposable
{
    private static readonly IRemoteGitProviderAdapter[] WebhookAdapters =
    {
        new ForgejoRemoteGitProviderAdapter(),
        new GitHubRemoteGitProviderAdapter(),
        new AzureDevOpsRemoteGitProviderAdapter(),
    };

    private readonly SqliteConnection _conn = new("Filename=:memory:");
    private readonly AppDbContext _db;

    public WebhooksControllerTests()
    {
        _conn.Open();
        var opts = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options;
        _db = new AppDbContext(opts);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string Hmac(string secret, string body, bool includePrefix = false)
    {
        using var h = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var sig = Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        return includePrefix ? $"sha256={sig}" : sig;
    }

    private static (WebhooksController controller, Mock<IPrSyncService> prSync, string body) BuildRequest(
        AppDbContext db, IDictionary<string, string?> headers, string body)
    {
        var prSync = new Mock<IPrSyncService>();
        var controller = new WebhooksController(prSync.Object, db, WebhookAdapters);
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        ctx.Request.ContentType = "application/json";
        foreach (var header in headers)
        {
            if (header.Value != null)
                ctx.Request.Headers[header.Key] = header.Value;
        }
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        return (controller, prSync, body);
    }

    [Fact]
    public async Task Forgejo_returns_unauthorized_when_signature_missing()
    {
        _db.RemoteProviders.Add(new RemoteProvider { Id = Guid.NewGuid(), Name = "p", Type = "Forgejo", Url = "x", WebhookSecret = "topsecret" });
        await _db.SaveChangesAsync();

        var payload = new WebhookPayload("pull_request.merged", "r1", "1", "https://x/pr/1", null, "merged");
        var (c, prSync, _) = BuildRequest(_db, new Dictionary<string, string?>(), JsonSerializer.Serialize(payload));
        var result = await c.Forgejo();

        Assert.IsType<UnauthorizedResult>(result);
        prSync.Verify(s => s.HandleWebhookAsync(It.IsAny<WebhookPayload>()), Times.Never);
    }

    [Fact]
    public async Task Forgejo_returns_unauthorized_when_signature_wrong()
    {
        _db.RemoteProviders.Add(new RemoteProvider { Id = Guid.NewGuid(), Name = "p", Type = "Forgejo", Url = "x", WebhookSecret = "topsecret" });
        await _db.SaveChangesAsync();

        var payload = new WebhookPayload("pull_request.merged", "r1", "1", "https://x/pr/1", null, "merged");
        var (c, prSync, _) = BuildRequest(_db, new Dictionary<string, string?>
        {
            ["X-Forgejo-Signature"] = "deadbeef",
        }, JsonSerializer.Serialize(payload));
        var result = await c.Forgejo();

        Assert.IsType<UnauthorizedResult>(result);
        prSync.Verify(s => s.HandleWebhookAsync(It.IsAny<WebhookPayload>()), Times.Never);
    }

    [Fact]
    public async Task Forgejo_accepts_request_when_signature_matches_provider_secret()
    {
        const string secret = "topsecret";
        _db.RemoteProviders.Add(new RemoteProvider { Id = Guid.NewGuid(), Name = "p", Type = "Forgejo", Url = "x", WebhookSecret = secret });
        await _db.SaveChangesAsync();

        var payload = new WebhookPayload("pull_request.merged", "r1", "1", "https://x/pr/1", null, "merged");
        var bodyJson = JsonSerializer.Serialize(payload);
        var sig = Hmac(secret, bodyJson);

                var (c, prSync, _) = BuildRequest(_db, new Dictionary<string, string?>
                {
                        ["X-Forgejo-Signature"] = sig,
                }, bodyJson);
        var result = await c.Forgejo();

        Assert.IsType<OkResult>(result);
        prSync.Verify(s => s.HandleWebhookAsync(It.IsAny<WebhookPayload>()), Times.Once);
    }

        [Fact]
        public async Task GitHub_accepts_merged_webhook_and_normalizes_payload()
        {
                const string secret = "github-secret";
                _db.RemoteProviders.Add(new RemoteProvider { Id = Guid.NewGuid(), Name = "gh", Type = "GitHub", Url = "https://github.com", WebhookSecret = secret });
                await _db.SaveChangesAsync();

                var body = """
                {
                    "action": "closed",
                    "repository": { "id": 42, "full_name": "team/repo" },
                    "pull_request": {
                        "number": 7,
                        "html_url": "https://github.com/team/repo/pull/7",
                        "merged": true
                    }
                }
                """;

                var (c, prSync, _) = BuildRequest(_db, new Dictionary<string, string?>
                {
                        ["X-GitHub-Event"] = "pull_request",
                        ["X-Hub-Signature-256"] = Hmac(secret, body, includePrefix: true),
                }, body);

                var result = await c.GitHub();

                Assert.IsType<OkResult>(result);
                prSync.Verify(s => s.HandleWebhookAsync(It.Is<WebhookPayload>(p =>
                        p.EventType == "pull_request.merged"
                        && p.PullRequestId == "7"
                        && p.PullRequestUrl == "https://github.com/team/repo/pull/7"
                        && p.MergeStatus == "merged")), Times.Once);
        }

        [Fact]
        public async Task GitHub_accepts_changes_requested_review_as_rejection()
        {
                const string secret = "github-secret";
                _db.RemoteProviders.Add(new RemoteProvider { Id = Guid.NewGuid(), Name = "gh", Type = "GitHub", Url = "https://github.com", WebhookSecret = secret });
                await _db.SaveChangesAsync();

                var body = """
                {
                    "action": "submitted",
                    "repository": { "id": 42, "full_name": "team/repo" },
                    "pull_request": {
                        "number": 7,
                        "html_url": "https://github.com/team/repo/pull/7"
                    },
                    "review": {
                        "state": "changes_requested",
                        "body": "needs work"
                    }
                }
                """;

                var (c, prSync, _) = BuildRequest(_db, new Dictionary<string, string?>
                {
                        ["X-GitHub-Event"] = "pull_request_review",
                        ["X-Hub-Signature-256"] = Hmac(secret, body, includePrefix: true),
                }, body);

                var result = await c.GitHub();

                Assert.IsType<OkResult>(result);
                prSync.Verify(s => s.HandleWebhookAsync(It.Is<WebhookPayload>(p =>
                        p.EventType == "pull_request.rejected"
                        && p.PullRequestUrl == "https://github.com/team/repo/pull/7"
                        && p.Comment == "needs work"
                        && p.MergeStatus == "changes_requested")), Times.Once);
        }

    private const string AzureMergedBody = """
        {
            "eventType": "git.pullrequest.merged",
            "resource": {
                "pullRequestId": 7,
                "status": "completed",
                "repository": { "id": "repo-guid", "webUrl": "https://dev.azure.com/contoso/widgets/_git/app" }
            }
        }
        """;

    private static string BasicCredential(string password, string username = "ild")
        => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));

    private void AddAzureProvider(string secret)
        => _db.RemoteProviders.Add(new RemoteProvider
        {
            Id = Guid.NewGuid(),
            Name = "azure",
            Type = "AzureDevOps",
            Url = "https://dev.azure.com/contoso",
            WebhookSecret = secret,
        });

    [Fact]
    public async Task AzureDevOps_accepts_merged_webhook_authenticated_by_its_basic_credential()
    {
        const string secret = "azure-secret";
        AddAzureProvider(secret);
        await _db.SaveChangesAsync();

        var (c, prSync, _) = BuildRequest(_db, new Dictionary<string, string?>
        {
            ["Authorization"] = BasicCredential(secret),
        }, AzureMergedBody);

        var result = await c.AzureDevOps();

        Assert.IsType<OkResult>(result);
        prSync.Verify(s => s.HandleWebhookAsync(It.Is<WebhookPayload>(p =>
            p.EventType == "pull_request.merged"
            && p.PullRequestId == "7"
            && p.PullRequestUrl == "https://dev.azure.com/contoso/widgets/_git/app/pullrequest/7"
            && p.MergeStatus == "merged")), Times.Once);
    }

    [Theory]
    // Azure DevOps signs nothing, so the Basic password is the whole secret: a
    // wrong one, a missing header and a non-Basic header all fail closed.
    [InlineData(null)]
    [InlineData("Basic aWxkOndyb25n")]
    [InlineData("Bearer azure-secret")]
    [InlineData("Basic not-valid-base64!!")]
    [InlineData("Basic aXNvbGF0ZWQ=")]
    public async Task AzureDevOps_returns_unauthorized_without_the_registered_basic_credential(string? authorization)
    {
        AddAzureProvider("azure-secret");
        await _db.SaveChangesAsync();

        var headers = new Dictionary<string, string?>();
        if (authorization != null)
            headers["Authorization"] = authorization;

        var (c, prSync, _) = BuildRequest(_db, headers, AzureMergedBody);
        var result = await c.AzureDevOps();

        Assert.IsType<UnauthorizedResult>(result);
        prSync.Verify(s => s.HandleWebhookAsync(It.IsAny<WebhookPayload>()), Times.Never);
    }

    [Fact]
    public async Task AzureDevOps_ignores_a_basic_credential_belonging_to_another_provider()
    {
        // The controller tries every stored secret; only an Azure DevOps
        // provider's own may admit an Azure DevOps hook.
        _db.RemoteProviders.Add(new RemoteProvider
        {
            Id = Guid.NewGuid(), Name = "gh", Type = "GitHub", Url = "https://github.com", WebhookSecret = "github-secret",
        });
        AddAzureProvider("azure-secret");
        await _db.SaveChangesAsync();

        var (c, prSync, _) = BuildRequest(_db, new Dictionary<string, string?>
        {
            ["Authorization"] = BasicCredential("github-secret"),
        }, AzureMergedBody);

        Assert.IsType<UnauthorizedResult>(await c.AzureDevOps());
        prSync.Verify(s => s.HandleWebhookAsync(It.IsAny<WebhookPayload>()), Times.Never);
    }

    [Fact]
    public async Task AzureDevOps_maps_an_abandoned_pull_request_to_a_closed_payload()
    {
        const string secret = "azure-secret";
        AddAzureProvider(secret);
        await _db.SaveChangesAsync();

        var body = """
        {
            "eventType": "git.pullrequest.updated",
            "resource": {
                "pullRequestId": 7,
                "status": "abandoned",
                "repository": { "id": "repo-guid", "webUrl": "https://dev.azure.com/contoso/widgets/_git/app" }
            }
        }
        """;

        var (c, prSync, _) = BuildRequest(_db, new Dictionary<string, string?>
        {
            ["Authorization"] = BasicCredential(secret),
        }, body);

        Assert.IsType<OkResult>(await c.AzureDevOps());
        prSync.Verify(s => s.HandleWebhookAsync(It.Is<WebhookPayload>(p =>
            p.EventType == "pull_request.rejected" && p.MergeStatus == "closed")), Times.Once);
    }

    [Fact]
    public async Task AzureDevOps_maps_a_pull_request_comment_event_to_a_comment_payload()
    {
        const string secret = "azure-secret";
        AddAzureProvider(secret);
        await _db.SaveChangesAsync();

        var body = """
        {
            "eventType": "ms.vss-code.git-pullrequest-comment-event",
            "resource": {
                "comment": { "content": "please rebase" },
                "pullRequest": {
                    "pullRequestId": 7,
                    "status": "active",
                    "repository": { "id": "repo-guid", "webUrl": "https://dev.azure.com/contoso/widgets/_git/app" }
                }
            }
        }
        """;

        var (c, prSync, _) = BuildRequest(_db, new Dictionary<string, string?>
        {
            ["Authorization"] = BasicCredential(secret),
        }, body);

        Assert.IsType<OkResult>(await c.AzureDevOps());
        prSync.Verify(s => s.HandleWebhookAsync(It.Is<WebhookPayload>(p =>
            p.EventType == "pull_request.comment" && p.Comment == "please rebase")), Times.Once);
    }

    [Fact]
    public async Task AzureDevOps_maps_a_negative_review_vote_to_a_rejection()
    {
        const string secret = "azure-secret";
        AddAzureProvider(secret);
        await _db.SaveChangesAsync();

        var body = """
        {
            "eventType": "git.pullrequest.updated",
            "resource": {
                "pullRequestId": 7,
                "status": "active",
                "reviewers": [ { "displayName": "alice", "vote": -10 } ],
                "repository": { "id": "repo-guid", "webUrl": "https://dev.azure.com/contoso/widgets/_git/app" }
            }
        }
        """;

        var (c, prSync, _) = BuildRequest(_db, new Dictionary<string, string?>
        {
            ["Authorization"] = BasicCredential(secret),
        }, body);

        Assert.IsType<OkResult>(await c.AzureDevOps());
        prSync.Verify(s => s.HandleWebhookAsync(It.Is<WebhookPayload>(p =>
            p.EventType == "pull_request.rejected" && p.MergeStatus == "changes_requested")), Times.Once);
    }
}
