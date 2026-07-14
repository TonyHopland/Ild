using ILD.Data.DTOs;
using ILD.Core.Services.Implementations;

namespace ILD.Tests;

public class LoopTemplateValidatorTests
{
    private static LoopNodeDto Node(string id, string type, string? prompt = null)
    {
        var dto = new LoopNodeDto { Id = id, NodeType = type, Label = id };
        if (prompt != null) dto.Config["prompt"] = prompt;
        return dto;
    }

    private static LoopNodeEdgeDto Edge(string from, string to, string type = "OnSuccess", string? name = null)
        => new() { Id = $"{from}->{to}:{type}:{name}", SourceNodeId = from, TargetNodeId = to, EdgeType = type, Name = name };

    [Fact]
    public void Graph_without_start_node_is_invalid()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Node("a", "Cmd"), Node("c", "Cleanup") },
            new() { Edge("a", "c") });
        var errs = LoopTemplateValidator.Validate(g);
        Assert.Contains(errs, e => e.Contains("Start"));
    }

    [Fact]
    public void Graph_without_cleanup_node_is_invalid()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Node("s", "Start"), Node("a", "Cmd") },
            new() { Edge("s", "a") });
        var errs = LoopTemplateValidator.Validate(g);
        Assert.Contains(errs, e => e.Contains("Cleanup"));
    }

    [Fact]
    public void Unreachable_node_is_invalid()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Node("s", "Start"), Node("a", "Cmd"), Node("orphan", "Cmd"), Node("c", "Cleanup") },
            new() { Edge("s", "a"), Edge("a", "c") });
        var errs = LoopTemplateValidator.Validate(g);
        Assert.Contains(errs, e => e.ToLower().Contains("unreachable"));
    }

    [Fact]
    public void No_path_to_cleanup_is_invalid()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Node("s", "Start"), Node("a", "Cmd"), Node("c", "Cleanup") },
            new() { Edge("s", "a"), Edge("a", "s") }); // cleanup unreachable
        var errs = LoopTemplateValidator.Validate(g);
        Assert.Contains(errs, e => e.ToLower().Contains("cleanup"));
    }

    [Fact]
    public void Two_outgoing_edges_of_same_type_invalid()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Node("s", "Start"), Node("a", "Cmd"), Node("b", "Cmd"), Node("c", "Cleanup") },
            new() { Edge("s", "a"), Edge("s", "b"), Edge("a", "c"), Edge("b", "c") });
        var errs = LoopTemplateValidator.Validate(g);
        Assert.Contains(errs, e => e.ToLower().Contains("duplicate"));
    }

    [Fact]
    public void Unknown_placeholder_in_prompt_template_invalid()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() {
                Node("s", "Start"),
                Node("a", "AI", "Title: {{WorkItem.Title}} {{Bogus.Thing}}"),
                Node("c", "Cleanup")
            },
            new() { Edge("s", "a"), Edge("a", "c") });
        var errs = LoopTemplateValidator.Validate(g);
        Assert.Contains(errs, e => e.Contains("Bogus"));
    }

    [Fact]
    public void Unknown_placeholder_in_prompt_node_invalid()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() {
                Node("s", "Start"),
                Node("a", "Prompt", "Retry: {{Bogus.Session}}"),
                Node("c", "Cleanup")
            },
            new() { Edge("s", "a"), Edge("a", "c") });

        var errs = LoopTemplateValidator.Validate(g);
        Assert.Contains(errs, e => e.Contains("Bogus.Session"));
    }

    [Fact]
    public void Ai_node_with_use_session_and_no_placeholder_is_invalid()
    {
        var ai = Node("a", "AI", "Title: {{WorkItem.Title}}");
        ai.Config["useSession"] = true;

        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() {
                Node("s", "Start"),
                ai,
                Node("c", "Cleanup")
            },
            new() { Edge("s", "a"), Edge("a", "c") });

        var errs = LoopTemplateValidator.Validate(g);
        Assert.Contains(errs, e => e.Contains("sessionPlaceholder"));
    }

    [Fact]
    public void Valid_minimal_graph_passes()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Node("s", "Start"), Node("a", "Cmd"), Node("c", "Cleanup") },
            new() { Edge("s", "a"), Edge("a", "c") });
        var errs = LoopTemplateValidator.Validate(g);
        Assert.Empty(errs);
    }

    [Fact]
    public void Custom_edge_on_non_human_ai_pr_node_is_invalid()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Node("s", "Start"), Node("a", "Cmd"), Node("c", "Cleanup") },
            new() { Edge("s", "a"), Edge("a", "c"), Edge("a", "c", "Custom", "Retry") });
        var errs = LoopTemplateValidator.Validate(g);
        Assert.Contains(errs, e => e.Contains("custom edges"));
    }

    [Fact]
    public void Custom_edge_without_name_is_invalid()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Node("s", "Start"), Node("h", "Human", "Review"), Node("c", "Cleanup") },
            new() { Edge("s", "h"), Edge("h", "c", "OnSuccess"), Edge("h", "c", "Custom") });
        var errs = LoopTemplateValidator.Validate(g);
        Assert.Contains(errs, e => e.Contains("must have a Name"));
    }

    [Fact]
    public void Duplicate_custom_edge_names_on_one_node_is_invalid()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Node("s", "Start"), Node("h", "Human", "Review"), Node("a", "AI"), Node("c", "Cleanup") },
            new() {
                Edge("s", "h"),
                Edge("h", "a", "Custom", "Respond"),
                Edge("h", "c", "Custom", "Respond"),
                Edge("a", "c")
            });
        var errs = LoopTemplateValidator.Validate(g);
        Assert.Contains(errs, e => e.Contains("duplicate custom edge 'Respond'"));
    }

    [Fact]
    public void Multiple_distinct_custom_edges_on_human_node_are_valid()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() {
                Node("s", "Start"),
                Node("h", "Human", "Review this"),
                Node("a", "AI"),
                Node("c", "Cleanup")
            },
            new() {
                Edge("s", "h"),
                Edge("h", "a", "Custom", "Respond"),
                Edge("h", "a", "Custom", "Escalate"),
                Edge("h", "a", "OnSuccess"),
                Edge("h", "c", "OnFailure"),
                Edge("a", "c")
            });
        var errs = LoopTemplateValidator.Validate(g);
        Assert.Empty(errs);
    }

    private static LoopNodeDto AiNodeWithRules(string id, params (string pattern, string edgeName)[] rules)
    {
        var dto = new LoopNodeDto { Id = id, NodeType = "AI", Label = id };
        dto.Config["matchRules"] = rules
            .Select(r => new Dictionary<string, object> { ["pattern"] = r.pattern, ["edgeName"] = r.edgeName })
            .ToList();
        return dto;
    }

    [Fact]
    public void Ai_custom_edge_without_matching_rule_is_invalid()
    {
        // Removed the rule but left the custom edge: the edge is unreachable.
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Node("s", "Start"), Node("a", "AI"), Node("c", "Cleanup") },
            new()
            {
                Edge("s", "a"),
                Edge("a", "c"),
                Edge("a", "c", "Custom", "Reject"),
            });
        var errs = LoopTemplateValidator.Validate(g);
        Assert.Contains(errs, e => e.Contains("custom edge 'Reject' that no match rule routes to"));
    }

    [Fact]
    public void Ai_match_rule_without_custom_edge_is_invalid()
    {
        // Rule references an edge that does not exist: routing would fail at run time.
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { AiNodeWithRules("a", ("Reject", "Reject")), Node("s", "Start"), Node("c", "Cleanup") },
            new() { Edge("s", "a"), Edge("a", "c") });
        var errs = LoopTemplateValidator.Validate(g);
        Assert.Contains(errs, e => e.Contains("match rule routing to 'Reject' but no custom edge"));
    }

    [Fact]
    public void Ai_match_rule_with_matching_custom_edge_is_valid()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { AiNodeWithRules("a", ("Reject", "Reject")), Node("s", "Start"), Node("c", "Cleanup") },
            new()
            {
                Edge("s", "a"),
                Edge("a", "c"),
                Edge("a", "c", "Custom", "Reject"),
            });
        var errs = LoopTemplateValidator.Validate(g);
        Assert.Empty(errs);
    }

    [Fact]
    public void Ai_custom_edge_name_casing_must_match_rule_exactly()
    {
        // The engine resolves edges by ordinal name, so a casing mismatch leaves
        // both the edge orphaned and the rule dangling.
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { AiNodeWithRules("a", ("Reject", "reject")), Node("s", "Start"), Node("c", "Cleanup") },
            new()
            {
                Edge("s", "a"),
                Edge("a", "c"),
                Edge("a", "c", "Custom", "Reject"),
            });
        var errs = LoopTemplateValidator.Validate(g);
        Assert.Contains(errs, e => e.Contains("custom edge 'Reject' that no match rule routes to"));
        Assert.Contains(errs, e => e.Contains("match rule routing to 'reject' but no custom edge"));
    }

    [Fact]
    public void Custom_edge_on_pr_node_is_valid()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() {
                Node("s", "Start"),
                Node("p", "PR"),
                Node("a", "AI"),
                Node("c", "Cleanup")
            },
            new() {
                Edge("s", "p"),
                Edge("p", "a", "Custom", "Respond"),
                Edge("p", "c", "OnSuccess"),
                Edge("p", "c", "OnFailure"),
                Edge("a", "c")
            });
        var errs = LoopTemplateValidator.Validate(g);
        Assert.Empty(errs);
    }

    // ---- Condition switch (multiple cases + default edge) ------------------

    private static Dictionary<string, object> SwitchCase(
        string variant, string edgeName, string? pattern = null, string? tag = null, string? subject = null)
    {
        var c = new Dictionary<string, object> { ["variant"] = variant, ["edgeName"] = edgeName };
        if (pattern != null) c["pattern"] = pattern;
        if (tag != null) c["tag"] = tag;
        if (subject != null) c["subject"] = subject;
        return c;
    }

    private static LoopNodeDto SwitchConditionNode(string id, object[] cases, string defaultEdge, string? output = null)
    {
        var dto = new LoopNodeDto { Id = id, NodeType = "Condition", Label = id };
        dto.Config["cases"] = cases;
        dto.Config["defaultEdge"] = defaultEdge;
        if (output != null) dto.Config["output"] = output;
        return dto;
    }

    // A switch Condition wired to one custom edge per referenced name.
    private static LoopTemplateGraph SwitchGraph(LoopNodeDto cond, params string[] edgeNames)
    {
        var edges = new List<LoopNodeEdgeDto> { Edge("s", cond.Id) };
        foreach (var name in edgeNames)
            edges.Add(Edge(cond.Id, "c", "Custom", name));
        return new LoopTemplateGraph(Guid.NewGuid(),
            new() { Node("s", "Start"), cond, Node("c", "Cleanup") },
            edges);
    }

    [Fact]
    public void Condition_switch_with_cases_and_default_is_valid()
    {
        var cond = SwitchConditionNode("q",
            new object[]
            {
                SwitchCase("TextMatches", "approved", pattern: "approve"),
                SwitchCase("HasTag", "urgent", tag: "urgent"),
                SwitchCase("PrExists", "has-pr"),
            },
            "otherwise");
        var errs = LoopTemplateValidator.Validate(SwitchGraph(cond, "approved", "urgent", "has-pr", "otherwise"));
        Assert.Empty(errs);
    }

    [Fact]
    public void Condition_switch_text_matches_with_empty_pattern_is_rejected()
    {
        var cond = SwitchConditionNode("q",
            new object[] { SwitchCase("TextMatches", "approved", pattern: "") }, "otherwise");
        var errs = LoopTemplateValidator.Validate(SwitchGraph(cond, "approved", "otherwise"));
        Assert.Contains(errs, e => e.Contains("must set a non-empty pattern"));
    }

    [Fact]
    public void Condition_switch_has_tag_without_tag_is_rejected()
    {
        var cond = SwitchConditionNode("q",
            new object[] { SwitchCase("HasTag", "tagged") }, "otherwise");
        var errs = LoopTemplateValidator.Validate(SwitchGraph(cond, "tagged", "otherwise"));
        Assert.Contains(errs, e => e.Contains("must set a non-empty tag"));
    }

    [Fact]
    public void Condition_switch_with_no_cases_is_rejected()
    {
        var cond = SwitchConditionNode("q", Array.Empty<object>(), "otherwise");
        var errs = LoopTemplateValidator.Validate(SwitchGraph(cond, "otherwise"));
        Assert.Contains(errs, e => e.Contains("must have at least one case"));
    }

    [Fact]
    public void Condition_switch_with_unknown_variant_is_rejected()
    {
        var cond = SwitchConditionNode("q",
            new object[] { SwitchCase("Bogus", "x") }, "otherwise");
        var errs = LoopTemplateValidator.Validate(SwitchGraph(cond, "x", "otherwise"));
        Assert.Contains(errs, e => e.Contains("unknown variant 'Bogus'"));
    }

    [Fact]
    public void Condition_switch_with_on_success_edge_is_rejected()
    {
        var cond = SwitchConditionNode("q",
            new object[] { SwitchCase("PrExists", "has-pr") }, "otherwise");
        var g = SwitchGraph(cond, "has-pr", "otherwise");
        g.Edges.Add(Edge("q", "c", "OnSuccess"));
        var errs = LoopTemplateValidator.Validate(g);
        Assert.Contains(errs, e => e.Contains("must not have an OnSuccess edge"));
    }

    [Fact]
    public void Condition_switch_with_unknown_output_placeholder_is_rejected()
    {
        var cond = SwitchConditionNode("q",
            new object[] { SwitchCase("PrExists", "has-pr") }, "otherwise", output: "{{Bogus.Thing}}");
        var errs = LoopTemplateValidator.Validate(SwitchGraph(cond, "has-pr", "otherwise"));
        Assert.Contains(errs, e => e.Contains("Bogus"));
    }

    [Fact]
    public void Condition_switch_without_default_edge_is_rejected()
    {
        var cond = SwitchConditionNode("q",
            new object[] { SwitchCase("PrExists", "has-pr") }, "");
        var errs = LoopTemplateValidator.Validate(SwitchGraph(cond, "has-pr"));
        Assert.Contains(errs, e => e.Contains("must set a default edge"));
    }

    [Fact]
    public void Condition_switch_case_without_edge_name_is_rejected()
    {
        var cond = SwitchConditionNode("q",
            new object[] { SwitchCase("PrExists", "") }, "otherwise");
        var errs = LoopTemplateValidator.Validate(SwitchGraph(cond, "otherwise"));
        Assert.Contains(errs, e => e.Contains("case 1 must set an edge name"));
    }

    [Fact]
    public void Condition_switch_case_with_invalid_regex_is_rejected()
    {
        var cond = SwitchConditionNode("q",
            new object[] { SwitchCase("TextMatches", "approved", pattern: "**bad**") }, "otherwise");
        var errs = LoopTemplateValidator.Validate(SwitchGraph(cond, "approved", "otherwise"));
        Assert.Contains(errs, e => e.Contains("case 1 has an invalid regex pattern"));
    }

    [Fact]
    public void Condition_switch_with_unwired_referenced_edge_is_rejected()
    {
        // The "otherwise" default edge is referenced but never wired out.
        var cond = SwitchConditionNode("q",
            new object[] { SwitchCase("PrExists", "has-pr") }, "otherwise");
        var errs = LoopTemplateValidator.Validate(SwitchGraph(cond, "has-pr"));
        Assert.Contains(errs, e => e.Contains("routes to 'otherwise' but no custom edge with that name exists"));
    }

    [Fact]
    public void Condition_switch_with_orphan_wired_edge_is_rejected()
    {
        // A wired custom edge no case or default routes to.
        var cond = SwitchConditionNode("q",
            new object[] { SwitchCase("PrExists", "has-pr") }, "otherwise");
        var errs = LoopTemplateValidator.Validate(SwitchGraph(cond, "has-pr", "otherwise", "orphan"));
        Assert.Contains(errs, e => e.Contains("custom edge 'orphan' that no case or default routes to"));
    }
}
