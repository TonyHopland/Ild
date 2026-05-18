using System.Security.Cryptography;
using System.Text;
using ILD.Api.Controllers;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.RemoteProviders;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests;

public class WebhookLoopRoutingTests
{
    private static readonly IRemoteGitProviderAdapter[] WebhookAdapters =
    {
        new ForgejoRemoteGitProviderAdapter(),
        new GitHubRemoteGitProviderAdapter(),
    };

    [Fact]
    public async Task GitHub_merged_webhook_routes_waiting_pr_node_to_on_success()
    {
        const string prUrl = "https://github.com/team/repo/pull/7";

        using var h = new EngineHarness();
        h.BuildSimpleGraph(
            ("s", NodeType.Start),
            ("pr", NodeType.PR),
            ("c", NodeType.Cleanup));
        h.AddEdge("e1", "s", "pr");
        h.AddEdge("e2", "pr", "c", EdgeType.OnSuccess);

        ConfigureGitHubProvider(h, "github-secret");
        h.Fakes[NodeType.PR].Behavior = _ => NodeExecutionResult.Ok(prUrl);
        h.Save();

        await h.Engine.RunAsync(h.RunId);
        LinkRunToPrUrl(h, prUrl);

        var parkedRun = h.ReloadRun();
        Assert.Equal(LoopRunStatus.WaitingHuman, parkedRun.Status);

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

        var result = await SendGitHubWebhookAsync(h, body, "pull_request", "github-secret");

        Assert.IsType<OkResult>(result);
        Assert.Equal(LoopRunStatus.Completed, h.ReloadRun().Status);
        Assert.Contains(h.ReloadRunNodes(), n => n.LoopNodeId == h.NodesById["c"].Id && n.Status == LoopRunNodeStatus.Succeeded);
    }

    [Fact]
    public async Task GitHub_changes_requested_review_routes_waiting_pr_node_to_on_failure()
    {
        const string prUrl = "https://github.com/team/repo/pull/7";

        using var h = new EngineHarness();
        h.BuildSimpleGraph(
            ("s", NodeType.Start),
            ("pr", NodeType.PR),
            ("fix", NodeType.Cmd),
            ("c", NodeType.Cleanup));
        h.AddEdge("e1", "s", "pr");
        h.AddEdge("e2", "pr", "c", EdgeType.OnSuccess);
        h.AddEdge("e3", "pr", "fix", EdgeType.OnFailure);
        h.AddEdge("e4", "fix", "c");

        ConfigureGitHubProvider(h, "github-secret");
        h.Fakes[NodeType.PR].Behavior = _ => NodeExecutionResult.Ok(prUrl);
        h.Save();

        await h.Engine.RunAsync(h.RunId);
        LinkRunToPrUrl(h, prUrl);

        var parkedRun = h.ReloadRun();
        Assert.Equal(LoopRunStatus.WaitingHuman, parkedRun.Status);

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

        var result = await SendGitHubWebhookAsync(h, body, "pull_request_review", "github-secret");

        Assert.IsType<OkResult>(result);
        Assert.Equal(LoopRunStatus.Completed, h.ReloadRun().Status);
        Assert.Contains(h.ReloadRunNodes(), n => n.LoopNodeId == h.NodesById["fix"].Id && n.Status == LoopRunNodeStatus.Succeeded);
    }

    private static void ConfigureGitHubProvider(EngineHarness harness, string secret)
    {
        var provider = harness.Db.Context.RemoteProviders.Single();
        provider.Type = "GitHub";
        provider.Url = "https://github.com";
        provider.WebhookSecret = secret;
    }

    private static void LinkRunToPrUrl(EngineHarness harness, string prUrl)
    {
        var run = harness.Db.Context.LoopRuns.Single(r => r.Id == harness.RunId);
        run.PrUrl = prUrl;
        harness.Db.Context.SaveChanges();
    }

    private static async Task<IActionResult> SendGitHubWebhookAsync(EngineHarness harness, string body, string eventName, string secret)
    {
        var workItems = harness.ServiceProvider.GetRequiredService<IWorkItemManager>();
        var prSync = new PrSyncService(harness.Db.LoopRuns, harness.Db.EventLogs, workItems, harness.Engine);
        var controller = new WebhooksController(prSync, harness.Db.Context, WebhookAdapters)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = BuildHttpContext(body, new Dictionary<string, string?>
                {
                    ["X-GitHub-Event"] = eventName,
                    ["X-Hub-Signature-256"] = Hmac(secret, body, includePrefix: true),
                }),
            },
        };

        return await controller.GitHub();
    }

    private static DefaultHttpContext BuildHttpContext(string body, IDictionary<string, string?> headers)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        ctx.Request.ContentType = "application/json";
        foreach (var header in headers)
        {
            if (header.Value != null)
                ctx.Request.Headers[header.Key] = header.Value;
        }

        return ctx;
    }

    private static string Hmac(string secret, string body, bool includePrefix = false)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var sig = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        return includePrefix ? $"sha256={sig}" : sig;
    }
}