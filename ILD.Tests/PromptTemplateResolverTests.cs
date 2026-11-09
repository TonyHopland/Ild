using ILD.Tests.Fakes;
using ILD.Core.Services.RunHosts.Local;
using System.Collections.Generic;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;

namespace ILD.Tests;

public class PromptTemplateResolverTests
{
    private static readonly PromptTemplateResolver Resolver = new(host: LocalRunHost.Shared);

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

    [Theory]
    [InlineData(1, true)]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void IsValidVariableName_bounds_length_to_the_name_column(int length, bool expected)
    {
        // Names are capped at 128 chars to match the LoopRunVariable.Name
        // column, so a pattern-shaped but over-long name can never overflow it.
        var name = new string('a', length);

        Assert.Equal(expected, PromptPlaceholderRegistry.IsValidVariableName(name));
    }
}

public class PromptTemplateResolverWorkTreeFileTests
{
    private const string Worktree = "/fake-worktrees/wt";

    private static FakeRunHost Host() => new FakeRunHost()
        .WithFile("/etc/passwd", "root:x:0:0")
        .WithFile("/fake-worktrees/x", "outside")
        .WithFile(Worktree + "/docs/a.md", "inside");

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../x")]
    [InlineData("a/../../x")]
    [InlineData("a\\..\\..\\x")]
    [InlineData("docs/../docs/a.md")]
    public async Task A_file_named_by_a_rooted_path_or_a_parent_segment_is_never_read(string path)
    {
        var resolver = new PromptTemplateResolver(Host());

        var files = await resolver.ReadWorkTreeFilesAsync($"{{{{WorkTree.File:{path}}}}}", Worktree, TestContext.Current.CancellationToken);

        Assert.Empty(files);
    }

    [Fact]
    public async Task A_file_in_the_worktree_is_read()
    {
        var resolver = new PromptTemplateResolver(Host());

        var files = await resolver.ReadWorkTreeFilesAsync("{{WorkTree.File:docs/a.md}}", Worktree, TestContext.Current.CancellationToken);

        Assert.Equal("inside", files["docs/a.md"]);
    }
}
