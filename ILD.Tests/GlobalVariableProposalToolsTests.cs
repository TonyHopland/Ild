using System.Text.Json;
using ILD.Tests.Fakes;

namespace ILD.Tests;

/// <summary>
/// The MCP half of Global Variable Proposals, addressed by tool name and
/// parameter names — the schema the agent actually sees. Agents read the
/// variables, suggest a whole new value and read what became of their
/// suggestions; no tool decides or deletes one.
/// </summary>
public class GlobalVariableProposalToolsTests
{
    [Fact]
    public async Task get_global_variables_reads_them_from_the_agent_surface()
    {
        var (result, request, _) = await McpToolInvoker.InvokeAsync("get_global_variables", new Dictionary<string, object?>(),
            """[{"name":"review_rules","value":"Keep it short.","updatedAt":"2026-10-01T10:00:00Z"}]""");

        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"{McpToolInvoker.BaseAddress}api/v1/agent/global-variables", request.RequestUri!.ToString());
        Assert.Contains("Keep it short.", result);
    }

    [Fact]
    public async Task propose_global_variable_change_posts_the_name_the_whole_value_and_the_rationale()
    {
        var (result, request, body) = await McpToolInvoker.InvokeAsync("propose_global_variable_change", new Dictionary<string, object?>
        {
            ["name"] = "review_rules",
            ["value"] = "Keep it short.\nName the file.",
            ["rationale"] = "Reviews keep missing the file.",
        }, """{"id":"gp-1","name":"review_rules","status":"Pending"}""");

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"{McpToolInvoker.BaseAddress}api/v1/agent/global-variables/proposals", request.RequestUri!.ToString());
        var json = JsonDocument.Parse(body!).RootElement;
        Assert.Equal("review_rules", json.GetProperty("name").GetString());
        Assert.Equal("Keep it short.\nName the file.", json.GetProperty("value").GetString());
        Assert.Equal("Reviews keep missing the file.", json.GetProperty("rationale").GetString());
        Assert.Contains("gp-1", result);
    }

    [Fact]
    public async Task list_global_variable_proposals_reads_the_callers_own_from_the_agent_surface()
    {
        var (result, request, _) = await McpToolInvoker.InvokeAsync("list_global_variable_proposals", new Dictionary<string, object?>(),
            """[{"id":"gp-1","status":"Rejected","rejectionReason":"Too vague."}]""");

        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"{McpToolInvoker.BaseAddress}api/v1/agent/global-variables/proposals", request.RequestUri!.ToString());
        Assert.Contains("Too vague.", result);
    }

    [Fact]
    public void No_tool_decides_or_deletes_a_suggestion()
    {
        Assert.Equal(
            new[] { "get_global_variables", "list_global_variable_proposals", "propose_global_variable_change" },
            McpToolInvoker.Names()
                .Where(n => n.Contains("global_variable", StringComparison.Ordinal))
                .OrderBy(n => n, StringComparer.Ordinal));
    }
}
