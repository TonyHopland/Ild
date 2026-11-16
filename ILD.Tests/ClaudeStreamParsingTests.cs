using ILD.Core.Services.Implementations.Adapters;

namespace ILD.Tests;

/// <summary>
/// The stream-json parser of <see cref="ClaudeCodeAdapter"/>, fed real
/// event shapes directly rather than through a stand-in CLI process.
/// </summary>
public class ClaudeStreamParsingTests
{
    private const string Init = "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"sess-abc\"}";

    private static string Assistant(string sid, string parts)
        => $"{{\"type\":\"assistant\",\"session_id\":\"{sid}\",\"message\":{{\"content\":[{parts}]}}}}";

    private static string Result(string sid, bool isError, string result)
        => $"{{\"type\":\"result\",\"session_id\":\"{sid}\",\"is_error\":{(isError ? "true" : "false")},\"result\":\"{result}\"}}";

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

        Assert.Equal("Hello, world.", output.Text);
        Assert.Equal("sess-abc", output.SessionId);
        Assert.True(output.SawJsonEvents);
        Assert.Null(output.ProviderError);
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
        Assert.Equal("Listing files.", output.Text);
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

        Assert.Equal("upstream broke", output.ProviderError);
        Assert.Equal("", output.Text);
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
    public async Task A_successful_stream_is_read_into_its_text_session_and_usage()
    {
        var output = await ClaudeCodeAdapter.ReadStreamJsonAsync(
            AdapterStreamReaders.Of(
                Init,
                Assistant("sess-abc", "{\"type\":\"text\",\"text\":\"Hello, world.\"}"),
                "{\"type\":\"result\",\"subtype\":\"success\",\"session_id\":\"sess-abc\",\"is_error\":false,\"result\":\"Hello, world.\",\"total_cost_usd\":0.5,\"usage\":{\"input_tokens\":100,\"cache_creation_input_tokens\":5,\"cache_read_input_tokens\":50,\"output_tokens\":40}}"),
            null, null, TestContext.Current.CancellationToken);

        var usage = AdapterUsageParser.ParseClaude(output.RawStdout, 0m);

        Assert.Equal("Hello, world.", output.Text);
        Assert.Equal("sess-abc", output.SessionId);
        Assert.Equal(155, usage!.InputTokens);
        Assert.Equal(40, usage.OutputTokens);
    }
}
