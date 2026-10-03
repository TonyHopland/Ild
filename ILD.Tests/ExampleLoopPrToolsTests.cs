using System.Text.Json;

namespace ILD.Tests;

/// <summary>
/// The loops under <c>example-loops/</c> are what a fresh install gets
/// (`TemplateSeeder` embeds and seeds them), so a change to what a PR node does
/// reaches every new user through these files or not at all.
/// </summary>
public class ExampleLoopPrToolsTests
{
    private static readonly string[] LoopFiles =
    {
        "Advanced.json",
        "Simple.json",
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
