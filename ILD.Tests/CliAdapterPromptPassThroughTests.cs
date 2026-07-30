using System.Diagnostics;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Tests;

/// <summary>
/// The adapter layer is transport, not templating: it hands the prompt it was
/// given to the agent CLI unchanged.
///
/// Rendering belongs to the node executors, which own the run context a
/// template needs and render each field exactly once. An adapter re-render is
/// wrong twice over — it is a second pass over already-substituted content, and
/// it runs against a strictly weaker context (no loop variables, no
/// conversation), so tokens it recognises resolve to nothing. It is also
/// provider-independent: every CLI adapter shares the same base, so the
/// invariant is asserted for each of them here.
/// </summary>
public class CliAdapterPromptPassThroughTests
{
    /// <summary>
    /// A prompt quoting every placeholder family, paired with a run context
    /// whose values are all non-empty — so any render pass in the adapter shows
    /// up as a visible rewrite rather than a coincidental no-op.
    /// </summary>
    private const string Prompt =
        "Title={{WorkItem.Title}} Desc={{WorkItem.Description}} Prev={{PreviousNode.Output}} "
        + "Input={{Node.Input}} Log={{EventLog.LastN}} Var={{Var.handoff}} Unknown={{Totally.Unknown}} Angle=<Foo.Bar>";

    private static LoopRunContext RunContext(string worktreePath) => new(
        LoopRunId: Guid.NewGuid(),
        WorkItemId: "WI-1",
        WorkItemTitle: "Fix the widget",
        WorkItemDescription: "A description that must not be substituted in.",
        WorktreePath: worktreePath,
        BranchName: "main",
        EventLogSummary: new List<string> { "NodeStarted: something" },
        PreviousNodeOutput: "the previous node's output");

    private static AgentExecutionContext Context(string providerType, string binaryPath, string worktreePath)
        => new(
            Provider: new AiProvider
            {
                Name = $"{providerType}-test",
                Type = providerType,
                BaseUrl = string.Empty,
                Model = "m",
                Config = $"{{\"binaryPath\":\"{binaryPath}\"}}",
            },
            Prompt: Prompt,
            RunContext: RunContext(worktreePath),
            ExecutionCount: 1,
            Cancel: CancellationToken.None);

    private static string CreateWorktree()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ild-passthrough-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task ClaudeCode_hands_the_prompt_to_the_agent_process_byte_for_byte()
    {
        using var cli = new PromptCapturingCli();

        var result = await new ClaudeCodeAdapter().ExecuteAsync(
            Context("claude-code", cli.BinaryPath, cli.WorkDir));

        // The strongest form of the invariant: what the agent process was
        // launched with is exactly what the caller handed the adapter.
        Assert.Equal(Prompt, cli.CapturedPrompt);
        Assert.True(result.Success);
        Assert.Equal(Prompt, result.ResolvedPrompt);
    }

    [Fact]
    public async Task OpenCode_records_the_prompt_it_was_given_as_the_resolved_prompt()
    {
        var worktree = CreateWorktree();
        try
        {
            var result = await new OpenCodeAdapter().ExecuteAsync(
                Context("opencode", "/bin/true", worktree));

            Assert.True(result.Success);
            Assert.Equal(Prompt, result.ResolvedPrompt);
        }
        finally
        {
            Directory.Delete(worktree, true);
        }
    }

    [Fact]
    public async Task Copilot_records_the_prompt_it_was_given_as_the_resolved_prompt()
    {
        var worktree = CreateWorktree();
        try
        {
            var result = await new CopilotAdapter().ExecuteAsync(
                Context("copilot", "/bin/true", worktree));

            Assert.True(result.Success);
            Assert.Equal(Prompt, result.ResolvedPrompt);
        }
        finally
        {
            Directory.Delete(worktree, true);
        }
    }

    [Fact]
    public async Task Pi_records_the_prompt_it_was_given_as_the_resolved_prompt()
    {
        var worktree = CreateWorktree();
        var scriptPath = Path.Combine(worktree, "fake-pi.sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n"
            + "echo '{\"type\":\"session\",\"version\":3,\"id\":\"pi-1\",\"cwd\":\"$PWD\"}'\n"
            + "echo '{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":[{\"text\":\"ok\"}]}}'\n");
        Process.Start("chmod", "+x " + scriptPath)!.WaitForExit();

        try
        {
            var result = await new PiAdapter().ExecuteAsync(
                Context("pi", scriptPath, worktree));

            Assert.True(result.Success);
            Assert.Equal(Prompt, result.ResolvedPrompt);
        }
        finally
        {
            Directory.Delete(worktree, true);
        }
    }
}
