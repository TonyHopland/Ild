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

    private static LoopTemplateGraph TaggedAiGraph(string aiProviderTag)
    {
        var ai = Node("ai-review", "AI", "Review {{WorkItem.Title}}");
        ai.Config["aiProviderTag"] = aiProviderTag;
        return new LoopTemplateGraph(Guid.NewGuid(),
            new() { Node("s", "Start"), ai, Node("c", "Cleanup") },
            new() { Edge("s", "ai-review"), Edge("ai-review", "c") });
    }

    public static TheoryData<string> InvalidAiProviderTags => new()
    {
        new string('x', 65),
        "QA,Fast",
    };

    [Theory]
    [MemberData(nameof(InvalidAiProviderTags))]
    public void Ai_provider_tag_too_long_or_with_a_comma_is_invalid_and_names_the_node(string tag)
    {
        var errs = LoopTemplateValidator.Validate(TaggedAiGraph(tag));

        Assert.Contains(errs, e => e.Contains("ai-review"));
    }

    public static TheoryData<string> ValidAiProviderTags => new()
    {
        new string('x', 64),
        "Nobody holds this one",
        "",
    };

    [Theory]
    [MemberData(nameof(ValidAiProviderTags))]
    public void Any_other_ai_provider_tag_saves(string tag)
    {
        Assert.Empty(LoopTemplateValidator.Validate(TaggedAiGraph(tag)));
    }

    /// <summary>Minimal Start → AI → Cleanup graph whose AI node uses a session.</summary>
    private static LoopTemplateGraph SessionGraph(string sessionPlaceholder, string? forkFromPlaceholder = null)
    {
        var ai = Node("a", "AI", "{{PreviousNode.Output}}");
        ai.Config["useSession"] = true;
        ai.Config["sessionPlaceholder"] = sessionPlaceholder;
        if (forkFromPlaceholder is not null) ai.Config["forkFromPlaceholder"] = forkFromPlaceholder;

        return new LoopTemplateGraph(Guid.NewGuid(),
            new() { Node("s", "Start"), ai, Node("c", "Cleanup") },
            new() { Edge("s", "a"), Edge("a", "c") });
    }

    [Fact]
    public void Literal_session_placeholder_is_valid()
    {
        Assert.Empty(LoopTemplateValidator.Validate(SessionGraph("research")));
    }

    [Fact]
    public void Session_placeholder_may_interpolate_a_loop_variable()
    {
        Assert.Empty(LoopTemplateValidator.Validate(SessionGraph("ticket_{{Var.current_ticket}}")));
    }

    [Fact]
    public void Fork_from_placeholder_may_interpolate_a_loop_variable()
    {
        Assert.Empty(LoopTemplateValidator.Validate(
            SessionGraph("fork_{{Var.n}}", "base_{{Var.n}}")));
    }

    [Theory]
    [InlineData("{{PreviousNode.Output}}")]      // known, but unbounded and different every turn
    [InlineData("t_{{WorkItem.Title}}")]         // known, but not a loop variable
    [InlineData("t_{{Node.Input}}")]
    [InlineData("t_{{Bogus.Thing}}")]            // not known at all
    [InlineData("t_{{Var.9bad}}")]               // Var. prefix, illegal variable name
    public void Session_placeholder_rejects_placeholders_other_than_loop_variables(string placeholder)
    {
        var errs = LoopTemplateValidator.Validate(SessionGraph(placeholder));
        Assert.Contains(errs, e => e.Contains("sessionPlaceholder") && e.Contains("Var."));
    }

    [Fact]
    public void Fork_from_placeholder_rejects_placeholders_other_than_loop_variables()
    {
        var errs = LoopTemplateValidator.Validate(SessionGraph("dest", "{{PreviousNode.Output}}"));
        Assert.Contains(errs, e => e.Contains("forkFromPlaceholder") && e.Contains("Var."));
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

    /// <summary>Declares <paramref name="names"/> as the node's outputs, one { "name" } object each.</summary>
    private static LoopNodeDto Declare(LoopNodeDto node, params string[] names)
    {
        node.Config["outputs"] = names.Select(n => new Dictionary<string, object> { ["name"] = n }).ToList();
        return node;
    }

    /// <summary>Sets a config key to raw JSON, the way a request body or a loop document delivers it.</summary>
    private static LoopNodeDto WithRawConfig(LoopNodeDto node, string key, string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        node.Config[key] = doc.RootElement.Clone();
        return node;
    }

    [Fact]
    public void Custom_edge_on_non_human_ai_pr_node_is_invalid()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Node("s", "Start"), Node("build", "Cmd"), Node("c", "Cleanup") },
            new() { Edge("s", "build"), Edge("build", "c"), Edge("build", "c", "Custom", "Retry") });
        var errs = LoopTemplateValidator.Validate(g);
        Assert.Contains(errs, e => e.Contains("build") && e.Contains("Retry"));
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
            new() { Node("s", "Start"), Declare(Node("h", "Human", "Review"), "Respond"), Node("a", "AI"), Node("c", "Cleanup") },
            new() {
                Edge("s", "h"),
                Edge("h", "a", "Custom", "Respond"),
                Edge("h", "c", "Custom", "Respond"),
                Edge("a", "c")
            });
        var errs = LoopTemplateValidator.Validate(g);
        Assert.Contains(errs, e => e.Contains("duplicate") && e.Contains("'Respond'"));
    }

    [Fact]
    public void Multiple_distinct_custom_edges_on_human_node_are_valid()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() {
                Node("s", "Start"),
                Declare(Node("h", "Human", "Review this"), "Respond", "Escalate"),
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
    public void Custom_edge_from_an_output_the_node_does_not_declare_is_invalid()
    {
        // An edge row only connects a declared output; it no longer defines one.
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Node("s", "Start"), Node("reviewer", "AI"), Node("c", "Cleanup") },
            new()
            {
                Edge("s", "reviewer"),
                Edge("reviewer", "c"),
                Edge("reviewer", "c", "Custom", "Reject"),
            });
        var errs = LoopTemplateValidator.Validate(g);
        Assert.Contains(errs, e => e.Contains("reviewer") && e.Contains("'Reject'"));
    }

    [Theory]
    [InlineData("OnSuccess")]
    [InlineData("OnFailure")]
    public void Custom_edge_named_after_a_fixed_output_is_invalid(string name)
    {
        // Success and failure are wired as their own edge types, never as a named custom edge.
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Node("s", "Start"), Node("review", "Human", "ok?"), Node("c", "Cleanup") },
            new() { Edge("s", "review"), Edge("review", "c", "Custom", name) });
        var errs = LoopTemplateValidator.Validate(g);
        Assert.Contains(errs, e => e.Contains("review") && e.Contains(name));
    }

    [Fact]
    public void Ai_match_rule_referencing_an_undeclared_output_is_invalid_and_says_to_declare_it()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { AiNodeWithRules("reviewer", ("Reject", "reject")), Node("s", "Start"), Node("c", "Cleanup") },
            new() { Edge("s", "reviewer"), Edge("reviewer", "c") });
        var errs = LoopTemplateValidator.Validate(g);
        Assert.Contains(errs, e => e.Contains("reviewer") && e.Contains("'reject'") && e.Contains("outputs"));
    }

    [Fact]
    public void Ai_match_rule_referencing_a_declared_output_that_has_no_edge_is_valid()
    {
        // A rule may point at an output nobody has wired yet: the author wires it next.
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Declare(AiNodeWithRules("a", ("Reject", "Reject")), "Reject"), Node("s", "Start"), Node("c", "Cleanup") },
            new() { Edge("s", "a"), Edge("a", "c") });
        Assert.Empty(LoopTemplateValidator.Validate(g));
    }

    [Fact]
    public void Declared_output_that_no_rule_references_and_no_edge_connects_is_valid()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Declare(Node("a", "AI"), "Escalate"), Declare(Node("h", "Human", "ok?"), "Later"), Node("s", "Start"), Node("c", "Cleanup") },
            new() { Edge("s", "a"), Edge("a", "h"), Edge("h", "c") });
        Assert.Empty(LoopTemplateValidator.Validate(g));
    }

    [Fact]
    public void Declared_output_wired_but_referenced_by_no_rule_is_valid()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Declare(Node("a", "AI"), "Escalate"), Node("s", "Start"), Node("c", "Cleanup") },
            new() { Edge("s", "a"), Edge("a", "c"), Edge("a", "c", "Custom", "Escalate") });
        Assert.Empty(LoopTemplateValidator.Validate(g));
    }

    [Fact]
    public void Ai_match_rule_with_matching_custom_edge_is_valid()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Declare(AiNodeWithRules("a", ("Reject", "Reject")), "Reject"), Node("s", "Start"), Node("c", "Cleanup") },
            new()
            {
                Edge("s", "a"),
                Edge("a", "c"),
                Edge("a", "c", "Custom", "Reject"),
            });
        var errs = LoopTemplateValidator.Validate(g);
        Assert.Empty(errs);
    }

    [Theory]
    [InlineData("OnSuccess")]
    [InlineData("OnFailure")]
    public void Ai_match_rule_may_not_route_to_a_fixed_output(string name)
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { AiNodeWithRules("reviewer", ("Reject", name)), Node("s", "Start"), Node("c", "Cleanup") },
            new() { Edge("s", "reviewer"), Edge("reviewer", "c") });
        var errs = LoopTemplateValidator.Validate(g);
        Assert.Contains(errs, e => e.Contains("reviewer") && e.Contains(name));
    }

    /// <summary>Wires an AI node whose single rule routes to a real custom edge,
    /// so the only thing left for the validator to complain about is the pattern.</summary>
    private static LoopTemplateGraph GraphWithAiPattern(string pattern)
        => new(Guid.NewGuid(),
            new() { Declare(AiNodeWithRules("a", (pattern, "Reject")), "Reject"), Node("s", "Start"), Node("c", "Cleanup") },
            new()
            {
                Edge("s", "a"),
                Edge("a", "c"),
                Edge("a", "c", "Custom", "Reject"),
            });

    [Fact]
    public void Ai_match_rule_with_an_uncompilable_pattern_is_invalid()
    {
        // The executor skips a pattern it cannot compile, so the rule would
        // silently never fire. Say so at save time, while it can still be fixed.
        var errs = LoopTemplateValidator.Validate(GraphWithAiPattern("[unclosed"));
        Assert.Contains(errs, e => e.Contains("is not a valid regex"));
    }

    [Theory]
    [InlineData("x*")]          // zero or more
    [InlineData("a?")]          // optional
    [InlineData("(?:)")]        // empty group
    [InlineData(@"\b")]         // zero-width assertion
    [InlineData(@"\s*")]        // zero or more whitespace
    [InlineData("(?=approve)")] // lookahead consumes nothing
    [InlineData("approve|")]    // empty alternative
    public void Ai_match_rule_that_can_match_nothing_is_invalid(string pattern)
    {
        // Under last-match-wins a zero-width pattern matches at the end of every
        // output, so it would outrank every genuine verdict.
        var errs = LoopTemplateValidator.Validate(GraphWithAiPattern(pattern));
        Assert.Contains(errs, e => e.Contains("can match an empty string"));
    }

    [Fact]
    public void The_multiline_anchored_verdict_pattern_the_authoring_guide_recommends_saves_cleanly()
    {
        // The Chat Context loop authoring guide (ChatService.LoopAuthoringGuide)
        // tells agents to anchor a verdict rule as (?m)^TO_REVIEW$. Advice that
        // cannot be saved is worse than none, so pin it here: the inline (?m)
        // must not read as zero-width to the probe.
        var errs = LoopTemplateValidator.Validate(GraphWithAiPattern("(?m)^TO_REVIEW$"));
        Assert.Empty(errs);
    }

    [Fact]
    public void Ai_match_rule_with_a_catastrophically_backtracking_pattern_is_invalid()
    {
        // "(.|.)+#" blows up on the validator's own short probe string: before
        // the probe run was given a timeout, this hung the save request itself.
        // It must come back as an ordinary validation error instead.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var errs = LoopTemplateValidator.Validate(GraphWithAiPattern("(.|.)+#"));
        sw.Stop();

        Assert.Contains(errs, e => e.Contains("too slow to evaluate"));
        // The timeout has to actually bound it — not merely be declared.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"validation took {sw.Elapsed}");
    }

    [Theory]
    [InlineData("reject")]
    [InlineData(@"^\s*reject")]
    [InlineData("reject(?! this)")]
    [InlineData("(reject|deny)")]
    [InlineData("nits*")]        // optional tail, but 'nit' is still required
    [InlineData(@"\d+")]
    [InlineData("approve with nits")]
    public void Ai_match_rule_with_an_ordinary_pattern_is_valid(string pattern)
    {
        // Anchors, lookarounds, alternation and optional tails are all
        // legitimate verdict patterns and must not be flagged.
        Assert.Empty(LoopTemplateValidator.Validate(GraphWithAiPattern(pattern)));
    }

    [Fact]
    public void Ai_match_rule_must_name_a_declared_output_with_the_same_casing()
    {
        // The engine resolves outputs by ordinal name, so 'reject' is not 'Reject'.
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Declare(AiNodeWithRules("reviewer", ("Reject", "reject")), "Reject"), Node("s", "Start"), Node("c", "Cleanup") },
            new()
            {
                Edge("s", "reviewer"),
                Edge("reviewer", "c"),
                Edge("reviewer", "c", "Custom", "Reject"),
            });
        var errs = LoopTemplateValidator.Validate(g);
        Assert.Contains(errs, e => e.Contains("reviewer") && e.Contains("'reject'"));
    }

    [Fact]
    public void Custom_edge_on_pr_node_is_valid()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() {
                Node("s", "Start"),
                Declare(Node("p", "PR"), "Respond"),
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

    [Fact]
    public void Reserved_pr_outputs_count_as_declared_even_when_absent_from_config()
    {
        // A PR node always has its reserved outputs, so wiring one needs no declaration.
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Node("s", "Start"), Node("p", "PR"), Node("a", "AI"), Node("c", "Cleanup") },
            new() {
                Edge("s", "p"),
                Edge("p", "a", "Custom", "on_comment"),
                Edge("p", "c", "Custom", "on_merged"),
                Edge("p", "c", "Custom", "on_abandoned"),
                Edge("a", "c")
            });
        Assert.Empty(LoopTemplateValidator.Validate(g));
    }

    [Fact]
    public void Reserved_pr_output_is_not_available_on_other_node_types()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Node("s", "Start"), Node("review", "Human", "ok?"), Node("c", "Cleanup") },
            new() { Edge("s", "review"), Edge("review", "c", "Custom", "on_merged") });
        var errs = LoopTemplateValidator.Validate(g);
        Assert.Contains(errs, e => e.Contains("review") && e.Contains("on_merged"));
    }

    [Fact]
    public void Output_objects_may_carry_fields_the_validator_does_not_know()
    {
        var pr = WithRawConfig(Node("p", "PR"), "outputs",
            "[{\"name\":\"on_merged\",\"reserved\":true,\"visible\":false},{\"name\":\"deploy\",\"color\":\"x\",\"confirm\":{\"text\":\"sure?\"}}]");
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Node("s", "Start"), pr, Node("c", "Cleanup") },
            new() { Edge("s", "p"), Edge("p", "c", "Custom", "on_merged"), Edge("p", "c", "Custom", "deploy") });
        Assert.Empty(LoopTemplateValidator.Validate(g));
    }

    // ---- outputs: shape and uniqueness ---------------------------------------

    public static TheoryData<string> MalformedOutputs => new()
    {
        "\"approve\"",                                   // not an array
        "{\"name\":\"approve\"}",                        // an object, not an array of them
        "[\"approve\"]",                                 // entries must be objects
        "[{}]",                                          // missing name
        "[{\"name\":\"\"}]",                             // empty name
        "[{\"name\":\"   \"}]",                          // blank name
        "[{\"name\":5}]",                                // non-string name
        "[{\"name\":null}]",
        "[{\"name\":\"approve\"},{\"name\":\"approve\"}]", // declared twice
    };

    [Theory]
    [MemberData(nameof(MalformedOutputs))]
    public void Malformed_outputs_are_rejected_and_name_the_node(string outputsJson)
    {
        var human = WithRawConfig(Node("review", "Human", "ok?"), "outputs", outputsJson);
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Node("s", "Start"), human, Node("c", "Cleanup") },
            new() { Edge("s", "review"), Edge("review", "c") });
        var errs = LoopTemplateValidator.Validate(g);
        Assert.Contains(errs, e => e.Contains("review"));
    }

    [Fact]
    public void Duplicate_output_error_names_the_output()
    {
        var human = WithRawConfig(Node("review", "Human", "ok?"), "outputs",
            "[{\"name\":\"approve\"},{\"name\":\"approve\",\"visible\":false}]");
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Node("s", "Start"), human, Node("c", "Cleanup") },
            new() { Edge("s", "review"), Edge("review", "c") });
        var errs = LoopTemplateValidator.Validate(g);
        Assert.Contains(errs, e => e.Contains("review") && e.Contains("'approve'"));
    }

    [Fact]
    public void Output_names_that_differ_only_in_case_are_distinct()
    {
        var human = Declare(Node("review", "Human", "ok?"), "approve", "Approve");
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Node("s", "Start"), human, Node("c", "Cleanup") },
            new() { Edge("s", "review"), Edge("review", "c") });
        Assert.Empty(LoopTemplateValidator.Validate(g));
    }

    // ---- per-type output rules -----------------------------------------------

    private static LoopTemplateGraph SingleStepGraph(string type, params string[] outputs)
    {
        var node = Declare(Node("step", type, type == "Prompt" ? "go" : null), outputs);
        return type == "Start"
            ? new LoopTemplateGraph(Guid.NewGuid(), new() { node, Node("c", "Cleanup") }, new() { Edge("step", "c") })
            : new LoopTemplateGraph(Guid.NewGuid(),
                new() { Node("s", "Start"), node, Node("c", "Cleanup") },
                new() { Edge("s", "step"), Edge("step", "c") });
    }

    [Theory]
    [InlineData("Start")]
    [InlineData("Cmd")]
    [InlineData("Prompt")]
    public void Nodes_without_named_outputs_may_declare_success_and_failure(string type)
    {
        Assert.Empty(LoopTemplateValidator.Validate(SingleStepGraph(type, "OnSuccess", "OnFailure")));
    }

    [Theory]
    [InlineData("Start")]
    [InlineData("Cmd")]
    [InlineData("Prompt")]
    public void Nodes_without_named_outputs_may_not_declare_one(string type)
    {
        var errs = LoopTemplateValidator.Validate(SingleStepGraph(type, "OnSuccess", "OnFailure", "Retry"));
        Assert.Contains(errs, e => e.Contains("step") && e.Contains("Retry"));
    }

    [Fact]
    public void Cleanup_declares_no_outputs()
    {
        var cleanup = Declare(Node("done", "Cleanup"), "OnSuccess");
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Node("s", "Start"), cleanup },
            new() { Edge("s", "done") });
        var errs = LoopTemplateValidator.Validate(g);
        Assert.Contains(errs, e => e.Contains("done"));
    }

    [Fact]
    public void Cleanup_with_an_empty_outputs_list_is_valid()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Node("s", "Start"), Declare(Node("c", "Cleanup")) },
            new() { Edge("s", "c") });
        Assert.Empty(LoopTemplateValidator.Validate(g));
    }

    [Fact]
    public void Cleanup_has_no_outgoing_edges()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Node("s", "Start"), Node("done", "Cleanup"), Node("a", "Cmd") },
            new() { Edge("s", "done"), Edge("done", "a") });
        var errs = LoopTemplateValidator.Validate(g);
        Assert.Contains(errs, e => e.Contains("done"));
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

    /// <summary>A switch Condition declaring every name its cases and default refer to.</summary>
    private static LoopNodeDto SwitchConditionNode(string id, object[] cases, string defaultEdge, string? output = null)
    {
        var referenced = cases.OfType<Dictionary<string, object>>()
            .Select(c => ((string)c["edgeName"]).Trim())
            .Append(defaultEdge.Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return Declare(UndeclaredSwitchConditionNode(id, cases, defaultEdge, output), referenced);
    }

    private static LoopNodeDto UndeclaredSwitchConditionNode(string id, object[] cases, string defaultEdge, string? output = null)
    {
        var dto = new LoopNodeDto { Id = id, NodeType = "Condition", Label = id };
        dto.Config["cases"] = cases;
        dto.Config["defaultEdge"] = defaultEdge;
        if (output != null) dto.Config["output"] = output;
        return dto;
    }

    // A switch Condition wired to one custom edge per given name.
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
        var cond = SwitchConditionNode("gate",
            new object[] { SwitchCase("PrExists", "has-pr") }, "otherwise");
        var g = SwitchGraph(cond, "has-pr", "otherwise");
        g.Edges.Add(Edge("gate", "c", "OnSuccess"));
        var errs = LoopTemplateValidator.Validate(g);
        Assert.Contains(errs, e => e.Contains("gate") && e.Contains("OnSuccess"));
    }

    [Fact]
    public void Condition_switch_declaring_an_on_success_output_is_rejected()
    {
        var cond = Declare(
            UndeclaredSwitchConditionNode("gate", new object[] { SwitchCase("PrExists", "has-pr") }, "otherwise"),
            "has-pr", "otherwise", "OnSuccess");
        var errs = LoopTemplateValidator.Validate(SwitchGraph(cond, "has-pr", "otherwise"));
        Assert.Contains(errs, e => e.Contains("gate") && e.Contains("OnSuccess"));
    }

    [Fact]
    public void Condition_switch_may_wire_its_failure_edge()
    {
        var cond = SwitchConditionNode("q",
            new object[] { SwitchCase("PrExists", "has-pr") }, "otherwise");
        var g = SwitchGraph(cond, "has-pr", "otherwise");
        g.Edges.Add(Edge("q", "c", "OnFailure"));
        Assert.Empty(LoopTemplateValidator.Validate(g));
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
    public void Condition_switch_with_a_declared_but_unwired_output_is_valid()
    {
        // The "otherwise" default is declared but not wired yet.
        var cond = SwitchConditionNode("q",
            new object[] { SwitchCase("PrExists", "has-pr") }, "otherwise");
        Assert.Empty(LoopTemplateValidator.Validate(SwitchGraph(cond, "has-pr")));
    }

    [Fact]
    public void Condition_switch_case_referencing_an_undeclared_output_is_rejected()
    {
        var cond = Declare(
            UndeclaredSwitchConditionNode("gate", new object[] { SwitchCase("PrExists", "has-pr") }, "otherwise"),
            "otherwise");
        var errs = LoopTemplateValidator.Validate(SwitchGraph(cond, "otherwise"));
        Assert.Contains(errs, e => e.Contains("gate") && e.Contains("'has-pr'") && e.Contains("outputs"));
    }

    [Fact]
    public void Condition_switch_default_referencing_an_undeclared_output_is_rejected()
    {
        var cond = Declare(
            UndeclaredSwitchConditionNode("gate", new object[] { SwitchCase("PrExists", "has-pr") }, "otherwise"),
            "has-pr");
        var errs = LoopTemplateValidator.Validate(SwitchGraph(cond, "has-pr"));
        Assert.Contains(errs, e => e.Contains("gate") && e.Contains("'otherwise'") && e.Contains("outputs"));
    }

    [Fact]
    public void Condition_switch_references_are_trimmed_before_they_are_looked_up()
    {
        // The executor trims case and default names, so padded references still resolve.
        var cond = Declare(
            UndeclaredSwitchConditionNode("q", new object[] { SwitchCase("PrExists", "  has-pr ") }, " otherwise "),
            "has-pr", "otherwise");
        Assert.Empty(LoopTemplateValidator.Validate(SwitchGraph(cond, "has-pr", "otherwise")));
    }

    [Theory]
    [InlineData("OnFailure", "otherwise")]
    [InlineData("has-pr", "OnFailure")]
    [InlineData("OnSuccess", "otherwise")]
    public void Condition_switch_may_not_route_a_case_or_default_to_a_fixed_output(string caseEdge, string defaultEdge)
    {
        var cond = Declare(
            UndeclaredSwitchConditionNode("gate", new object[] { SwitchCase("PrExists", caseEdge) }, defaultEdge),
            "has-pr", "otherwise");
        var fixedName = caseEdge.StartsWith("On") ? caseEdge : defaultEdge;
        var errs = LoopTemplateValidator.Validate(SwitchGraph(cond, "has-pr", "otherwise"));
        Assert.Contains(errs, e => e.Contains("gate") && e.Contains(fixedName));
    }

    [Fact]
    public void Condition_switch_with_a_wired_edge_from_an_undeclared_output_is_rejected()
    {
        var cond = SwitchConditionNode("gate",
            new object[] { SwitchCase("PrExists", "has-pr") }, "otherwise");
        var errs = LoopTemplateValidator.Validate(SwitchGraph(cond, "has-pr", "otherwise", "orphan"));
        Assert.Contains(errs, e => e.Contains("gate") && e.Contains("'orphan'"));
    }

    [Fact]
    public void Condition_switch_with_a_declared_output_no_case_references_is_valid()
    {
        var cond = Declare(
            UndeclaredSwitchConditionNode("q", new object[] { SwitchCase("PrExists", "has-pr") }, "otherwise"),
            "has-pr", "otherwise", "spare");
        Assert.Empty(LoopTemplateValidator.Validate(SwitchGraph(cond, "has-pr", "otherwise", "spare")));
    }
}
