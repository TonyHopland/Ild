using System.Text.Json;

namespace ILD.Tests;

/// <summary>
/// The loops under <c>example-loops/</c> are what a fresh install gets
/// (`TemplateSeeder` embeds and seeds them), so a change to what a PR node does
/// reaches every new user through these files or not at all.
///
/// They had drifted: both still set `prCommentTemplate`, which stopped doing
/// anything, and neither told the agent handling PR feedback that
/// `get_pr_review` exists. The effect on a fresh install was silence — the
/// bundled loops stopped saying anything on their pull requests, with no hint
/// why. Nothing failed, because nothing looked.
/// </summary>
public class ExampleLoopPrToolsTests
{
    /// <summary>Config keys a PR node no longer reads. Set, they promise something that will not happen.</summary>
    private static readonly string[] DeadPrConfig = { "prCommentTemplate" };

    private static readonly string[] LoopFiles =
    {
        "DevTeam.json",
        "Development.json",
        "Plan.json",
        "Q&A.json",
    };

    private static JsonDocument Loop(string name)
        => JsonDocument.Parse(RepositoryFiles.ReadAllText(Path.Combine("example-loops", name)));

    private static string Prompt(JsonElement config)
        => config.TryGetProperty("prompt", out var p) ? p.GetString() ?? string.Empty : string.Empty;

    private static IEnumerable<(string Loop, string Label, JsonElement Config)> Nodes()
    {
        foreach (var file in LoopFiles)
        {
            using var doc = Loop(file);
            if (!doc.RootElement.TryGetProperty("nodes", out var nodes)) continue;
            foreach (var node in nodes.EnumerateArray())
            {
                var label = node.TryGetProperty("label", out var l) ? l.GetString() ?? "?" : "?";
                if (node.TryGetProperty("config", out var config))
                    yield return (file, label, config.Clone());
            }
        }
    }

    [Fact]
    public void No_shipped_loop_sets_pr_config_the_node_no_longer_reads()
    {
        foreach (var (file, label, config) in Nodes())
            foreach (var dead in DeadPrConfig)
                Assert.False(
                    config.TryGetProperty(dead, out _),
                    $"{file}: node '{label}' still sets {dead}, which does nothing now.");
    }

    [Fact]
    public void The_loops_that_answer_pull_request_feedback_say_how_to_read_it()
    {
        // A prompt that says "inspect the PR" and stops leaves the agent with
        // the rendered review, which is exactly the thing that omits findings.
        // The node a PR edge routes back to is the one whose prompt opens by
        // saying the pull request needs something.
        var feedbackPrompts = Nodes()
            .Select(n => (n.Loop, n.Label, Prompt: Prompt(n.Config)))
            .Where(n => n.Prompt.StartsWith("The pull request", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(feedbackPrompts);
        foreach (var (file, label, prompt) in feedbackPrompts)
        {
            Assert.Contains("get_pr_review", prompt, StringComparison.Ordinal);
            Assert.True(
                prompt.Contains("reply_to_pr_review_comment", StringComparison.Ordinal),
                $"{file}: node '{label}' never tells the agent how to answer a finding where it was raised.");
            Assert.True(
                prompt.Contains("comment_on_pr", StringComparison.Ordinal),
                $"{file}: node '{label}' never tells the agent how to say anything general — nothing else does it now.");
        }
    }

    [Fact]
    public void Every_shipped_loop_is_still_readable_json_with_nodes()
    {
        foreach (var file in LoopFiles)
        {
            using var doc = Loop(file);
            Assert.True(doc.RootElement.TryGetProperty("nodes", out var nodes), $"{file} has no nodes");
            Assert.NotEmpty(nodes.EnumerateArray());
        }
    }
}
