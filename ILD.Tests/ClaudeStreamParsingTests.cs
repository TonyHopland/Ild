using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;

namespace ILD.Tests;

/// <summary>
/// The stream-json parser and the result assembly of <see cref="ClaudeCodeAdapter"/>, fed real
/// event shapes directly rather than through a stand-in CLI process.
/// </summary>
public class ClaudeStreamParsingTests
{
    private const string Init = "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"sess-abc\"}";

    private static string Assistant(string sid, string parts)
        => $"{{\"type\":\"assistant\",\"session_id\":\"{sid}\",\"message\":{{\"content\":[{parts}]}}}}";

    private static string Result(string sid, bool isError, string result)
        => $"{{\"type\":\"result\",\"session_id\":\"{sid}\",\"is_error\":{(isError ? "true" : "false")},\"result\":\"{result}\"}}";

    private static NodeExecutionResult Assemble(ClaudeCodeAdapter.ClaudeStreamOutput stdout, int exitCode = 0, string stderr = "")
        => ClaudeCodeAdapter.AssembleResult(stdout, stderr, exitCode, "the prompt", requestedSessionId: null, incomingSessionId: null, sessionCostBefore: 0m);

    [Fact]
    public async Task Assistant_text_and_the_session_id_are_read_from_stream_json_and_text_is_streamed()
    {
        var progress = new List<string>();
        var output = await ClaudeCodeAdapter.ReadStreamJsonAsync(
            AdapterStreamReaders.Of(
                Init,
                Assistant("sess-abc", "{\"type\":\"text\",\"text\":\"Hello, \"},{\"type\":\"text\",\"text\":\"world.\"}"),
                Result("sess-abc", false, "Hello, world.")),
            line => { progress.Add(line); return Task.CompletedTask; },
            onSessionId: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("Hello, world.", output.Content);
        Assert.Equal("sess-abc", output.SessionId);
        Assert.True(output.SawJsonEvents);
        Assert.Null(output.ErrorMessage);
        Assert.Equal(["Hello, world."], progress);
    }

    [Fact]
    public async Task Tool_calls_reach_the_progress_stream_as_one_line_markers_and_stay_out_of_the_output()
    {
        var progress = new List<string>();
        var output = await ClaudeCodeAdapter.ReadStreamJsonAsync(
            AdapterStreamReaders.Of(
                Init,
                Assistant("sess-abc",
                    "{\"type\":\"text\",\"text\":\"Listing files.\"}," +
                    "{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{\"description\":\"list\",\"command\":\"ls\\n  -la\"}}"),
                Result("sess-abc", false, "Listing files.")),
            line => { progress.Add(line); return Task.CompletedTask; },
            onSessionId: null,
            TestContext.Current.CancellationToken);

        var marker = Assert.Single(progress, p => p.Contains("[tool: Bash]"));
        Assert.Equal("\n[tool: Bash] ls -la\n", marker);
        Assert.Contains("Listing files.", progress);
        Assert.Equal("Listing files.", output.Content);
    }

    [Fact]
    public async Task The_session_id_fires_OnSessionId_once_though_every_event_carries_it()
    {
        var captured = new List<string>();
        var output = await ClaudeCodeAdapter.ReadStreamJsonAsync(
            AdapterStreamReaders.Of(
                "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"sess-live\"}",
                Assistant("sess-live", "{\"type\":\"text\",\"text\":\"hi\"}"),
                Result("sess-live", false, "hi")),
            progressCallback: null,
            onSessionId: captured.Add,
            TestContext.Current.CancellationToken);

        Assert.Equal(["sess-live"], captured);
        Assert.Equal("sess-live", output.SessionId);
    }

    [Fact]
    public async Task A_result_marked_as_error_is_kept_as_the_error_message()
    {
        var output = await ClaudeCodeAdapter.ReadStreamJsonAsync(
            AdapterStreamReaders.Of(Init, Result("sess-abc", true, "upstream broke")),
            null, null, TestContext.Current.CancellationToken);

        Assert.Equal("upstream broke", output.ErrorMessage);
        Assert.Equal("", output.Content);
    }

    [Fact]
    public async Task Lines_that_are_not_json_are_ignored_and_do_not_count_as_events()
    {
        var output = await ClaudeCodeAdapter.ReadStreamJsonAsync(
            AdapterStreamReaders.Of("plain text", "", "still not json"),
            null, null, TestContext.Current.CancellationToken);

        Assert.False(output.SawJsonEvents);
        Assert.Contains("plain text", output.RawStdout);
        Assert.Null(output.SessionId);
    }

    [Fact]
    public async Task An_error_result_fails_the_run_with_the_providers_message()
    {
        var output = await ClaudeCodeAdapter.ReadStreamJsonAsync(
            AdapterStreamReaders.Of(Init, Result("sess-abc", true, "upstream broke")),
            null, null, TestContext.Current.CancellationToken);

        var result = Assemble(output);

        Assert.False(result.Success);
        Assert.Contains("upstream broke", result.Error);
    }

    [Fact]
    public async Task A_usage_limit_result_error_is_classified_as_an_interruption()
    {
        // The result event's own error text is the provider's message, isolated, so it is classified from that.
        var output = await ClaudeCodeAdapter.ReadStreamJsonAsync(
            AdapterStreamReaders.Of(
                Init,
                Result("sess-abc", true, "Claude usage limit reached. Your limit will reset at 9:40am (UTC).")),
            null, null, TestContext.Current.CancellationToken);

        var result = Assemble(output);

        Assert.False(result.Success);
        Assert.Equal(FailureKind.Interrupted, result.Failure);
    }

    [Fact]
    public async Task A_successful_stream_assembles_the_text_session_and_usage()
    {
        var output = await ClaudeCodeAdapter.ReadStreamJsonAsync(
            AdapterStreamReaders.Of(
                Init,
                Assistant("sess-abc", "{\"type\":\"text\",\"text\":\"Hello, world.\"}"),
                "{\"type\":\"result\",\"subtype\":\"success\",\"session_id\":\"sess-abc\",\"is_error\":false,\"result\":\"Hello, world.\",\"total_cost_usd\":0.5,\"usage\":{\"input_tokens\":100,\"cache_creation_input_tokens\":5,\"cache_read_input_tokens\":50,\"output_tokens\":40}}"),
            null, null, TestContext.Current.CancellationToken);

        var result = Assemble(output);

        Assert.True(result.Success, result.Error);
        Assert.Equal("Hello, world.", result.Output);
        Assert.Equal("sess-abc", result.SessionId);
        Assert.Equal("the prompt", result.ResolvedPrompt);
        Assert.Equal(155, result.Usage!.InputTokens);
        Assert.Equal(40, result.Usage.OutputTokens);
    }

    [Fact]
    public async Task A_clean_exit_with_json_events_but_no_text_fails_and_does_not_return_the_raw_stream()
    {
        var output = await ClaudeCodeAdapter.ReadStreamJsonAsync(
            AdapterStreamReaders.Of(Init),
            null, null, TestContext.Current.CancellationToken);

        var result = Assemble(output);

        Assert.False(result.Success);
        Assert.Contains("[claude-code] no assistant text response from model", result.Error);
        Assert.DoesNotContain("session_id", result.Error);
    }

    [Fact]
    public async Task A_nonzero_exit_fails_with_the_exit_code_and_stderr()
    {
        var output = await ClaudeCodeAdapter.ReadStreamJsonAsync(
            AdapterStreamReaders.Of(Init, Assistant("sess-abc", "{\"type\":\"text\",\"text\":\"partial\"}")),
            null, null, TestContext.Current.CancellationToken);

        var result = Assemble(output, exitCode: 2, stderr: "boom");

        Assert.False(result.Success);
        Assert.Equal("exit=2 stderr=boom", result.Error);
        Assert.Equal("partial", result.Output);
    }
}
