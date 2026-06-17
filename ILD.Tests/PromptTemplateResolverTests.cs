using System.Collections.Generic;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;

namespace ILD.Tests;

public class PromptTemplateResolverTests
{
    private static readonly PromptTemplateResolver Resolver = new();

    [Fact]
    public void Render_substitutes_a_loop_variable()
    {
        var ctx = new PromptContext(RunVariables: new Dictionary<string, string>
        {
            ["handoff"] = "ship it",
        });

        var result = Resolver.Render("Note: {{Var.handoff}}", ctx);

        Assert.Equal("Note: ship it", result);
    }

    [Fact]
    public void Render_matches_loop_variable_names_case_insensitively()
    {
        // The renderer builds the variable map with an ordinal-ignore-case
        // comparer, so a template author needn't match the exact casing the
        // agent used when it wrote the variable.
        var ctx = new PromptContext(RunVariables: new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase) { ["Summary"] = "all good" });

        var result = Resolver.Render("{{Var.summary}}", ctx);

        Assert.Equal("all good", result);
    }

    [Fact]
    public void Render_emits_empty_for_a_loop_variable_not_yet_set()
    {
        // The producing node may run after this template is rendered, so an
        // unset loop variable resolves to empty rather than leaking the token.
        var ctx = new PromptContext(RunVariables: new Dictionary<string, string>());

        var result = Resolver.Render("before[{{Var.missing}}]after", ctx);

        Assert.Equal("before[]after", result);
    }

    [Fact]
    public void Render_emits_empty_for_a_loop_variable_when_no_variables_supplied()
    {
        var result = Resolver.Render("[{{Var.handoff}}]", new PromptContext());

        Assert.Equal("[]", result);
    }
}
