using ILD.Data;

namespace ILD.Tests;

/// <summary>
/// What the loop authoring guide (get_loop_authoring_guide) tells an agent
/// about the Set Variable node, line by line, so a guide edit cannot quietly
/// drop it or bring back the claim that only AI nodes write variables.
/// </summary>
public class LoopAuthoringGuideTests
{
    private static string Line(string startsWith)
        => Assert.Single(
            LoopAuthoringGuide.Text.Split('\n'),
            l => l.TrimStart().StartsWith(startsWith, StringComparison.Ordinal));

    [Fact]
    public void The_node_type_list_documents_the_Set_Variable_node()
    {
        var line = Line("- SetVariable:");

        Assert.Contains("config.assignments", line);
        Assert.Contains("before", line);
        Assert.Contains("8192", line);
        Assert.Contains("input", line);
        Assert.Contains("OnSuccess", line);
    }

    [Fact]
    public void Loop_variables_are_written_by_a_Set_Variable_node_or_an_AI_node()
    {
        var line = Line("Loop variables:");

        Assert.Contains("Set Variable node", line);
        Assert.Contains("set_loop_variable", line);
        Assert.DoesNotContain("an AI node writes", line);
    }

    [Fact]
    public void Per_iteration_sessions_recommend_a_Set_Variable_node_to_set_the_variable_first()
        => Assert.Contains("Set Variable node", Line("Per-iteration sessions:"));

    [Fact]
    public void Values_are_stored_as_rendered_so_command_output_is_trimmed_in_the_command()
    {
        Assert.Contains("exactly as rendered", LoopAuthoringGuide.Text);
        Assert.Contains("tr -d", LoopAuthoringGuide.Text);
    }

    [Fact]
    public void The_outputs_rule_lists_Set_Variable_among_the_nodes_with_only_success_and_failure()
        => Assert.Contains("SetVariable", Line("- config.outputs must be an array"));
}
