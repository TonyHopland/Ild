using System.Reflection;
using ILD.Api.Controllers;
using Microsoft.AspNetCore.Mvc.Routing;

namespace ILD.Tests;

/// <summary>
/// What an agent may do to a pull request's review: read it, answer a thread,
/// resolve that thread, say something general about the round, and close an item
/// it read and chose not to answer. Approving, merging, closing the PULL REQUEST
/// and dismissing a review are decisions this surface must not be able to take,
/// so the whole surface — MCP tool names and agent routes — is pinned exactly
/// rather than the additions being checked on their own.
///
/// "Close" is the one verb that now means two things, which is why it is not
/// simply banned: an agent may close a review ITEM, and must not be able to
/// close the pull request. Every occurrence of it is therefore named.
/// </summary>
public class PrReviewAgentSurfaceTests
{
    /// <summary>Actions this surface must not offer under any name.</summary>
    private static readonly string[] ForbiddenVerbs = { "approve", "merge", "dismiss" };

    /// <summary>The whole PR-review tool surface. Exactly these, no more.</summary>
    private static readonly string[] ExpectedTools =
    {
        "get_pr_review",
        "reply_to_pr_review_comment",
        "resolve_pr_review_thread",
        "comment_on_pr",
        "close_pr_review_item",
    };

    private static readonly (string Method, string Template)[] ExpectedRoutes =
    {
        ("GET", "workitems/{id}/pr-review"),
        ("POST", "workitems/{id}/pr-review/reply"),
        ("POST", "workitems/{id}/pr-review/resolve"),
        ("POST", "workitems/{id}/pr-review/comment"),
        ("POST", "workitems/{id}/pr-review/close"),
    };

    private static IReadOnlyList<(string Method, string Template)> AgentRoutes()
        => typeof(AgentController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(m => m.GetCustomAttributes<HttpMethodAttribute>()
                .Select(a => (Method: a.HttpMethods.First(), Template: a.Template ?? string.Empty)))
            .ToArray();

    private static IReadOnlyList<string> PrReviewTools()
        => typeof(ILD.McpServer.Tools.PrReviewTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(m => m.CustomAttributes)
            .Where(a => a.AttributeType.Name == "McpServerToolAttribute")
            .Select(a => (string)a.NamedArguments.Single(n => n.MemberName == "Name").TypedValue.Value!)
            .ToArray();

    [Fact]
    public void The_mcp_server_offers_exactly_these_five_things_to_do_with_a_review()
    {
        Assert.Equal(ExpectedTools.OrderBy(n => n, StringComparer.Ordinal), PrReviewTools().OrderBy(n => n, StringComparer.Ordinal));
        // …and each of them is really registered on the server, not just on the class.
        Assert.All(ExpectedTools, name => Assert.Contains(name, McpServerToolReflection.Names()));
    }

    [Fact]
    public void No_mcp_tool_can_approve_merge_or_dismiss()
    {
        var names = McpServerToolReflection.Names();

        foreach (var verb in ForbiddenVerbs)
            Assert.DoesNotContain(names, n => n.Contains(verb, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_only_thing_an_agent_can_close_is_one_item_of_a_review()
    {
        // Not the pull request. The verb is allowed exactly once, on exactly the
        // tool that takes a comment id — a tool named for closing anything else
        // fails here rather than being read as this one.
        var closing = McpServerToolReflection.Names()
            .Where(n => n.Contains("clos", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Equal(new[] { "close_pr_review_item" }, closing);
    }

    [Fact]
    public void The_agent_api_exposes_exactly_these_five_routes_for_a_review()
    {
        var routes = AgentRoutes();

        foreach (var expected in ExpectedRoutes)
            Assert.Contains(expected, routes);

        var reviewRoutes = routes.Where(r => r.Template.Contains("pr-review", StringComparison.Ordinal)).ToArray();
        Assert.Equal(ExpectedRoutes.Length, reviewRoutes.Length);
        foreach (var verb in ForbiddenVerbs)
            Assert.DoesNotContain(reviewRoutes, r => r.Template.Contains(verb, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void No_agent_route_at_all_approves_merges_or_dismisses()
    {
        foreach (var verb in ForbiddenVerbs)
            Assert.DoesNotContain(AgentRoutes(), r => r.Template.Contains(verb, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_only_route_that_closes_anything_closes_one_review_item()
    {
        var closing = AgentRoutes()
            .Where(r => r.Template.Contains("clos", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Equal(new[] { ("POST", "workitems/{id}/pr-review/close") }, closing);
    }
}
