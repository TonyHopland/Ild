using System.Text;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Data.DTOs;

namespace ILD.Tests;

/// <summary>
/// The Claude stream reader against real launches that ended a turn with work still running (<c>Fixtures/claude-streams</c>, whose
/// README records how they were captured): a launch is one or more turns, and the CLI itself waits for the work the agent registered.
/// </summary>
public class ClaudeBackgroundWorkStreamTests
{
    private static StreamReader Fixture(string name)
        => new(new MemoryStream(Encoding.UTF8.GetBytes(RepositoryFiles.ReadAllText(Path.Combine("ILD.Tests", "Fixtures", "claude-streams", name + ".jsonl")))));

    private static async Task<(StreamResult Result, List<IReadOnlyList<RunBackgroundTask>> Tasks, List<string> Progress)> ReadAsync(string name)
    {
        var tasks = new List<IReadOnlyList<RunBackgroundTask>>();
        var progress = new List<string>();
        var result = await ClaudeCodeAdapter.ReadStreamJsonAsync(
            Fixture(name), line => { progress.Add(line); return Task.CompletedTask; }, onSessionId: null, TestContext.Current.CancellationToken,
            live => { tasks.Add(live); return Task.CompletedTask; });
        return (result, tasks, progress);
    }

    [Theory]
    [InlineData("registered-background-command")]
    [InlineData("registered-background-command-stdin-open")]
    public async Task A_registered_command_that_outlives_the_turn_gives_a_launch_that_answers_with_the_follow_up_turn(string fixture)
    {
        var (result, _, progress) = await ReadAsync(fixture);

        Assert.Equal("FINISHED", result.Text);
        Assert.Null(result.ProviderError);
        Assert.NotNull(result.SessionId);
        // Both turns were shown live; only the last one is the answer.
        Assert.Contains("STARTED", progress);
        Assert.Contains("FINISHED", progress);
    }

    [Fact]
    public async Task The_live_task_list_appears_while_the_command_runs_and_clears_when_it_ends()
    {
        var (_, tasks, _) = await ReadAsync("registered-background-command");

        Assert.Equal(2, tasks.Count);
        var live = Assert.Single(tasks[0]);
        Assert.Equal("local_bash", live.Kind);
        Assert.Equal("Sleep for 20 seconds in background", live.Description);
        Assert.Null(live.SubagentType);
        Assert.Empty(tasks[1]);
    }

    [Fact]
    public async Task A_background_subagent_is_listed_with_its_type_and_its_own_foreground_command_is_not()
    {
        var (result, tasks, _) = await ReadAsync("background-subagent");

        Assert.Equal("FINISHED", result.Text);
        Assert.Equal(2, tasks.Count);
        var agent = Assert.Single(tasks[0]);
        Assert.Equal("local_agent", agent.Kind);
        Assert.Equal("general-purpose", agent.SubagentType);
        Assert.Empty(tasks[1]);
    }

    [Fact]
    public async Task An_unregistered_leftover_process_is_one_turn_with_no_task_to_show()
    {
        var (result, tasks, _) = await ReadAsync("unregistered-leftover-process");

        Assert.Equal("STARTED", result.Text);
        Assert.Null(result.ProviderError);
        Assert.Empty(tasks);
    }

    [Fact]
    public async Task Of_a_registered_command_and_a_leftover_in_one_turn_only_the_registered_one_is_listed()
    {
        var (result, tasks, _) = await ReadAsync("registered-command-and-unregistered-leftover");

        Assert.Equal("FINISHED", result.Text);
        Assert.Equal(2, tasks.Count);
        Assert.Single(tasks[0]);
        Assert.Empty(tasks[1]);
    }

    [Fact]
    public async Task A_launch_without_task_events_lists_nothing_and_needs_no_listener()
    {
        var withListener = await ReadAsync("unregistered-leftover-process");
        var without = await ClaudeCodeAdapter.ReadStreamJsonAsync(
            Fixture("unregistered-leftover-process"), null, null, TestContext.Current.CancellationToken);

        Assert.Empty(withListener.Tasks);
        Assert.Equal("STARTED", without.Text);
    }

    [Fact]
    public async Task The_last_turn_decides_whether_the_launch_failed()
    {
        const string Init = "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s\"}";
        static string Result(bool isError, string text) => $"{{\"type\":\"result\",\"session_id\":\"s\",\"is_error\":{(isError ? "true" : "false")},\"result\":\"{text}\"}}";
        static string Say(string text) => $"{{\"type\":\"assistant\",\"session_id\":\"s\",\"message\":{{\"content\":[{{\"type\":\"text\",\"text\":\"{text}\"}}]}}}}";

        var recovered = await ClaudeCodeAdapter.ReadStreamJsonAsync(
            AdapterStreamReaders.Of(Init, Say("bad"), Result(true, "bad"), Say("good"), Result(false, "good")), null, null, TestContext.Current.CancellationToken);
        var failedLate = await ClaudeCodeAdapter.ReadStreamJsonAsync(
            AdapterStreamReaders.Of(Init, Say("good"), Result(false, "good"), Say("bad"), Result(true, "bad")), null, null, TestContext.Current.CancellationToken);

        Assert.Null(recovered.ProviderError);
        Assert.Equal("good", recovered.Text);
        Assert.Equal("bad", failedLate.ProviderError);
        Assert.Equal("bad", failedLate.Text);
    }

    [Fact]
    public async Task A_follow_up_turn_without_text_leaves_the_earlier_turns_answer()
    {
        var result = await ClaudeCodeAdapter.ReadStreamJsonAsync(
            AdapterStreamReaders.Of(
                "{\"type\":\"assistant\",\"session_id\":\"s\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"first\"}]}}",
                "{\"type\":\"result\",\"session_id\":\"s\",\"is_error\":false,\"result\":\"first\"}",
                "{\"type\":\"result\",\"session_id\":\"s\",\"is_error\":false,\"result\":\"\"}"),
            null, null, TestContext.Current.CancellationToken);

        Assert.Equal("first", result.Text);
    }

    [Fact]
    public async Task Tokens_are_the_sum_of_the_turns_and_cost_is_the_last_running_total()
    {
        var (result, _, _) = await ReadAsync("registered-background-command");

        var usage = AdapterUsageParser.ParseClaude(result.RawStdout, sessionCostBefore: 0m)!;

        // Per result: 4 + 12127 + 45288 in and 115 out, then 4 + 383 + 28839 in and 7 out.
        Assert.Equal(4 + 12127 + 45288 + 4 + 383 + 28839, usage.InputTokens);
        Assert.Equal(115 + 7, usage.OutputTokens);
        // total_cost_usd is the launch's running total: the second result's, not the sum of both.
        Assert.Equal(0.0033050700000000002m, usage.CostUsd);
    }

    [Fact]
    public async Task The_cost_of_a_resumed_launch_is_the_last_total_less_the_session_cost_before_it()
    {
        var (result, _, _) = await ReadAsync("registered-background-command");

        var usage = AdapterUsageParser.ParseClaude(result.RawStdout, sessionCostBefore: 0.001m)!;

        Assert.Equal(0.0033050700000000002m - 0.001m, usage.CostUsd);
    }
}
