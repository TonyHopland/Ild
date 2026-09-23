using ILD.Data;

namespace ILD.Tests;

/// <summary>
/// A reserved edge nobody can find is a reserved edge nobody wires. The edge
/// vocabulary is written down in four places that have to move together, and the
/// one precedence rule an author cannot guess — that a changes-requested review
/// keeps outranking a comment — has to be said out loud.
/// </summary>
public class PrReviewDocumentationTests
{
    private const string Edge = "on_comment";

    private static string ContextEntry(string name)
    {
        var text = RepositoryFiles.ReadAllText("CONTEXT.md");
        var start = text.IndexOf($"**{name}**:", StringComparison.Ordinal);
        Assert.True(start >= 0, $"CONTEXT.md has no '{name}' entry");
        var avoid = text.IndexOf("_Avoid_:", start, StringComparison.Ordinal);
        var end = avoid < 0 ? text.Length : text.IndexOf('\n', avoid);
        return text[start..(end < 0 ? text.Length : end)];
    }

    private static void AssertMentionsOneOf(string text, string subject, params string[] alternatives)
        => Assert.True(
            alternatives.Any(a => text.Contains(a, StringComparison.OrdinalIgnoreCase)),
            $"nothing in {subject} mentions any of: {string.Join(", ", alternatives)}");

    [Fact]
    public void The_authoring_guide_lists_the_new_edge_with_the_other_reserved_ones()
    {
        var guide = LoopAuthoringGuide.Text;
        var prLine = guide.Split('\n').Single(l => l.Contains("Reserved PR edges:", StringComparison.Ordinal));

        foreach (var edge in new[]
        {
            "on_rejected", "on_merge_conflict", "on_ci_failed", Edge,
            "on_approved", "on_ci_passed", "on_merged", "on_abandoned",
        })
            Assert.Contains(edge, prLine, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pr_heartbeat_entry_documents_the_new_edge()
    {
        var entry = ContextEntry("PR Heartbeat");

        Assert.Contains(Edge, entry, StringComparison.Ordinal);
        Assert.DoesNotContain("The seven reserved edges", entry, StringComparison.Ordinal);
    }

    [Fact]
    public void Context_documents_the_ledger_tool_the_marker_and_the_throttles()
    {
        var context = RepositoryFiles.ReadAllText("CONTEXT.md");

        Assert.Contains("get_pr_review", context, StringComparison.Ordinal);
        AssertMentionsOneOf(context, "CONTEXT.md", "marker");
        AssertMentionsOneOf(context, "CONTEXT.md", "batch", "one firing", "single firing");
        AssertMentionsOneOf(context, "CONTEXT.md", "truncated", "cut short", "incomplete");
    }

    [Fact]
    public void Context_says_which_of_the_two_review_edges_wins_while_changes_are_requested()
    {
        // An author who wires on_comment and never sees it fire under a
        // changes-requested review has no way to work out why.
        var context = RepositoryFiles.ReadAllText("CONTEXT.md");
        var sentences = context.Split(new[] { ". ", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s.Contains("on_rejected", StringComparison.Ordinal) && s.Contains(Edge, StringComparison.Ordinal))
            .ToArray();

        Assert.True(sentences.Length > 0, "CONTEXT.md never mentions on_rejected and on_comment together");
        AssertMentionsOneOf(string.Join("\n", sentences), "the on_rejected/on_comment note",
            "outranks", "wins", "takes precedence", "higher priority", "beats");
    }

    [Fact]
    public void The_pr_node_adr_names_the_new_edge_among_the_ones_the_heartbeat_fires()
    {
        var adr = RepositoryFiles.ReadAllText(Path.Combine("docs", "adr", "0004-pr-lifecycle-as-graph-node.md"));
        var consequences = adr[adr.IndexOf("## Consequences", StringComparison.Ordinal)..];

        Assert.Contains(Edge, consequences, StringComparison.Ordinal);
    }

    [Fact]
    public void The_loop_editors_pr_help_text_lists_the_new_edge()
    {
        var modal = RepositoryFiles.ReadAllText(
            Path.Combine("frontend", "src", "pages", "LoopEditor", "components", "NodeSettingsModal.tsx"));

        Assert.Contains(Edge, modal, StringComparison.Ordinal);
    }

    [Fact]
    public void The_changelog_says_what_is_different_now()
    {
        var changelog = RepositoryFiles.ReadAllText("CHANGELOG.md");
        var afterHeading = changelog[(changelog.IndexOf("## [Unreleased]", StringComparison.Ordinal) + 1)..];
        var nextRelease = afterHeading.IndexOf("\n## [", StringComparison.Ordinal);
        var unreleased = nextRelease < 0 ? afterHeading : afterHeading[..nextRelease];

        AssertMentionsOneOf(unreleased, "the unreleased changelog section", "review", "comment");
    }
}
