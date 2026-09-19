using System.Reflection;
using ILD.Api.Controllers;
using Microsoft.AspNetCore.Mvc.Routing;

namespace ILD.Tests;

/// <summary>
/// What an agent may do to a pull request's review: read it, answer a thread,
/// close that thread. Approving, merging, closing the PR and dismissing a review
/// are decisions this surface must not be able to take, so the whole surface —
/// MCP tool names and agent routes — is checked rather than the three additions
/// on their own.
/// </summary>
public class PrReviewAgentSurfaceTests
{
    private static readonly string[] ForbiddenVerbs = { "approve", "merge", "dismiss", "close" };

    private static IReadOnlyList<(string Method, string Template)> AgentRoutes()
        => typeof(AgentController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(m => m.GetCustomAttributes<HttpMethodAttribute>()
                .Select(a => (Method: a.HttpMethods.First(), Template: a.Template ?? string.Empty)))
            .ToArray();

    [Fact]
    public void The_mcp_server_offers_a_way_to_read_a_review_reply_to_it_and_resolve_a_thread()
    {
        var names = McpServerToolReflection.Names();

        Assert.Contains("get_pr_review", names);
        Assert.Contains(names, n => n.Contains("reply", StringComparison.Ordinal) && n.Contains("pr", StringComparison.Ordinal));
        Assert.Contains(names, n => n.Contains("resolve", StringComparison.Ordinal) && n.Contains("pr", StringComparison.Ordinal));
    }

    [Fact]
    public void No_mcp_tool_can_approve_merge_close_or_dismiss()
    {
        var names = McpServerToolReflection.Names();

        foreach (var verb in ForbiddenVerbs)
            Assert.DoesNotContain(names, n => n.Contains(verb, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_agent_api_exposes_exactly_read_reply_and_resolve_for_a_review()
    {
        var routes = AgentRoutes();

        Assert.Contains(("GET", "workitems/{id}/pr-review"), routes);
        Assert.Contains(("POST", "workitems/{id}/pr-review/reply"), routes);
        Assert.Contains(("POST", "workitems/{id}/pr-review/resolve"), routes);

        var reviewRoutes = routes.Where(r => r.Template.Contains("pr-review", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, reviewRoutes.Length);
        foreach (var verb in ForbiddenVerbs)
            Assert.DoesNotContain(reviewRoutes, r => r.Template.Contains(verb, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void No_agent_route_at_all_approves_merges_closes_or_dismisses()
    {
        foreach (var verb in ForbiddenVerbs)
            Assert.DoesNotContain(AgentRoutes(), r => r.Template.Contains(verb, StringComparison.OrdinalIgnoreCase));
    }
}
