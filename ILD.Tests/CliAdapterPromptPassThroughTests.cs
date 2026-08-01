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

    /// <summary>
    /// Run one adapter against a fake CLI that records the prompt where that CLI
    /// really receives it, and assert on the recording. <c>ResolvedPrompt</c> is
    /// checked too, but only as a secondary: each adapter reports it from the
    /// same field it launches with, so on its own it could not catch a re-render.
    /// </summary>
    private static async Task AssertPromptReachesTheProcessAsync(
        IAgentAdapter adapter,
        string providerType,
        PromptCapturingCli cli)
    {
        var result = await adapter.ExecuteAsync(Context(providerType, cli.BinaryPath, cli.WorkDir));

        Assert.Equal(Prompt, cli.CapturedPrompt);
        Assert.True(result.Success);
        Assert.Equal(Prompt, result.ResolvedPrompt);
    }

    [Fact]
    public async Task ClaudeCode_hands_the_prompt_to_the_agent_process_byte_for_byte()
    {
        // The strongest form of the invariant: what the agent process was
        // launched with is exactly what the caller handed the adapter.
        using var cli = new PromptCapturingCli();

        await AssertPromptReachesTheProcessAsync(new ClaudeCodeAdapter(), "claude-code", cli);
    }

    [Fact]
    public async Task OpenCode_hands_the_prompt_to_the_agent_process_byte_for_byte()
    {
        using var cli = new PromptCapturingCli(turn: PromptCapturingCli.SilentSuccess);

        await AssertPromptReachesTheProcessAsync(new OpenCodeAdapter(), "opencode", cli);
    }

    [Fact]
    public async Task Copilot_hands_the_prompt_to_the_agent_process_byte_for_byte()
    {
        using var cli = new PromptCapturingCli(turn: PromptCapturingCli.SilentSuccess);

        await AssertPromptReachesTheProcessAsync(new CopilotAdapter(), "copilot", cli);
    }

    [Fact]
    public async Task Pi_writes_the_prompt_to_the_agent_process_stdin_byte_for_byte()
    {
        // Pi is the one CLI that takes its turn on stdin rather than argv, so
        // that is where the prompt has to be observed.
        using var cli = new PromptCapturingCli(PromptCaptureMode.StandardInput, PromptCapturingCli.PiTurn);

        await AssertPromptReachesTheProcessAsync(new PiAdapter(), "pi", cli);
    }
}
