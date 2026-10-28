using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;

namespace ILD.Tests;

/// <summary>
/// The <c>pi --mode json</c> stdout parser and result assembly, fed real event shapes directly
/// rather than through a stand-in CLI process.
/// </summary>
public class PiOutputParsingTests
{
    private const string Session = "{\"type\":\"session\",\"version\":3,\"id\":\"pi-live\",\"cwd\":\"/w\"}";

    private static string Delta(string text)
        => $"{{\"type\":\"message_update\",\"message\":{{\"role\":\"assistant\",\"content\":[]}},\"assistantMessageEvent\":{{\"type\":\"text_delta\",\"delta\":\"{text}\"}}}}";

    private static string MessageEnd(string text)
        => $"{{\"type\":\"message_end\",\"message\":{{\"role\":\"assistant\",\"content\":[{{\"text\":\"{text}\"}}]}}}}";

    private static Task<PiAdapter.PiExecutionOutput> Read(Func<string, Task>? progress, Action<string>? onSessionId, params string[] lines)
        => PiAdapter.ReadStdoutAsync(AdapterStreamReaders.Of(lines), progress, onSessionId, TestContext.Current.CancellationToken);

    private static NodeExecutionResult Assemble(PiAdapter.PiExecutionOutput stdout, int exitCode = 0, string stderr = "")
        => PiAdapter.AssembleResult(stdout, stderr, exitCode, "the prompt", effectiveSessionId: stdout.SessionId, incomingSessionId: null);

    [Fact]
    public async Task Text_deltas_are_streamed_and_the_session_header_gives_the_session_id()
    {
        var progress = new List<string>();
        var output = await Read(
            chunk => { progress.Add(chunk); return Task.CompletedTask; }, null,
            Session, Delta("hello "), Delta("world"), MessageEnd("hello world"));

        Assert.Equal("hello world", output.Content);
        Assert.Equal("pi-live", output.SessionId);
        Assert.True(output.SawTurnEnd);
        Assert.Equal(["hello ", "world"], progress);
    }

    [Fact]
    public async Task The_completed_assistant_message_is_the_text_when_no_deltas_streamed()
    {
        var output = await Read(null, null, Session, MessageEnd("whole message"));

        Assert.Equal("whole message", output.Content);
    }

    [Fact]
    public async Task The_session_id_fires_OnSessionId_once()
    {
        var captured = new List<string>();
        await Read(null, captured.Add, Session, Session, Delta("hi"), MessageEnd("hi"));

        Assert.Equal(["pi-live"], captured);
    }

    [Fact]
    public async Task A_tool_call_is_a_one_line_marker_on_the_progress_stream_and_not_in_the_output()
    {
        var progress = new List<string>();
        var output = await Read(
            chunk => { progress.Add(chunk); return Task.CompletedTask; }, null,
            Session,
            "{\"type\":\"tool_execution_start\",\"toolCallId\":\"call-1\",\"toolName\":\"bash\",\"args\":{\"command\":\"npm\\n  test\"}}",
            Delta("ran the tests"), MessageEnd("ran the tests"));

        Assert.Contains("\n[tool: bash] npm test\n", progress);
        Assert.Equal("ran the tests", output.Content);
    }

    [Fact]
    public async Task An_ild_tool_called_from_codemode_gets_its_own_marker()
    {
        // Pi 1.0.4 dist/core/nested-tool-calls.js: a tool called from codemode is announced with its own
        // tool_execution_start carrying parentToolCallId.
        var progress = new List<string>();
        var output = await Read(
            chunk => { progress.Add(chunk); return Task.CompletedTask; }, null,
            Session,
            "{\"type\":\"tool_execution_start\",\"toolCallId\":\"call-1\",\"toolName\":\"codemode\",\"args\":{\"code\":\"await mcp.ild.get_workitem({workItemId: 1})\"}}",
            "{\"type\":\"tool_execution_start\",\"toolCallId\":\"call-1/1\",\"toolName\":\"mcp__ild__get_workitem\",\"args\":{\"workItemId\":\"wi-7\"},\"parentToolCallId\":\"call-1\"}",
            "{\"type\":\"tool_execution_end\",\"toolCallId\":\"call-1/1\",\"toolName\":\"mcp__ild__get_workitem\",\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"{}\"}]},\"isError\":false,\"parentToolCallId\":\"call-1\"}",
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"done\"}]}}");

        Assert.Contains("\n[tool: mcp__ild__get_workitem] wi-7\n", progress);
        Assert.Equal("done", output.Content);
    }

    [Fact]
    public async Task A_stream_that_ends_before_message_end_fails_as_truncated_keeping_the_partial_text()
    {
        var output = await Read(null, null, Session, Delta("partial response"));

        var result = Assemble(output);

        Assert.False(output.SawTurnEnd);
        Assert.False(result.Success);
        Assert.Contains("truncated", result.Error);
        Assert.Equal("partial response", result.Output);
    }

    [Fact]
    public async Task A_completed_turn_without_text_fails_with_a_diagnostic()
    {
        var output = await Read(null, null, Session,
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":[]}}");

        var result = Assemble(output, stderr: "provider dropped it");

        Assert.False(result.Success);
        Assert.Contains("[pi] no assistant text response. stderr: provider dropped it", result.Error);
    }

    [Fact]
    public async Task Stdout_that_is_not_json_is_the_response()
    {
        var output = await Read(null, null, "plain output");

        var result = Assemble(output);

        Assert.True(result.Success, result.Error);
        Assert.Equal("plain output", result.Output!.Trim());
    }

    [Fact]
    public async Task A_nonzero_exit_fails_with_the_exit_code_and_stderr()
    {
        var output = await Read(null, null, Session, Delta("x"), MessageEnd("x"));

        var result = Assemble(output, exitCode: 2, stderr: "boom");

        Assert.False(result.Success);
        Assert.Equal("exit=2 stderr=boom", result.Error);
    }

    [Fact]
    public async Task Usage_is_summed_over_the_runs_assistant_messages()
    {
        const string first = "{\"input\":100,\"output\":10,\"cacheRead\":20,\"cacheWrite\":5,\"totalTokens\":135,\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"total\":0.25}}";
        const string second = "{\"input\":200,\"output\":30,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":230,\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"total\":0.5}}";
        var output = await Read(null, null,
            Session,
            $"{{\"type\":\"message_end\",\"message\":{{\"role\":\"assistant\",\"content\":[{{\"type\":\"toolCall\",\"id\":\"c1\",\"name\":\"bash\",\"arguments\":{{}}}}],\"usage\":{first}}}}}",
            $"{{\"type\":\"turn_end\",\"message\":{{\"role\":\"assistant\",\"content\":[],\"usage\":{first}}},\"toolResults\":[]}}",
            $"{{\"type\":\"message_end\",\"message\":{{\"role\":\"assistant\",\"content\":[{{\"type\":\"text\",\"text\":\"done\"}}],\"usage\":{second}}}}}",
            $"{{\"type\":\"turn_end\",\"message\":{{\"role\":\"assistant\",\"content\":[{{\"type\":\"text\",\"text\":\"done\"}}],\"usage\":{second}}},\"toolResults\":[]}}");

        var result = Assemble(output);

        Assert.True(result.Success, result.Error);
        Assert.Equal(325, result.Usage!.InputTokens);
        Assert.Equal(40, result.Usage.OutputTokens);
        Assert.Equal(0.75m, result.Usage.CostUsd);
    }
}
