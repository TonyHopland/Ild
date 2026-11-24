using ILD.Core.Services.Implementations.Adapters;

namespace ILD.Tests;

/// <summary>
/// The <c>agy --output-format stream-json</c> stdout parser and its usage reading, fed the event shapes the
/// CLI prints (captured from agy 1.5.16 against a fake Gemini endpoint) rather than a stand-in process.
/// </summary>
public class AgyOutputParsingTests
{
    private const string Conversation = "31fe3f09-6406-48c6-862e-cf5eb36655c8";

    private static string Init(string id = Conversation)
        => $"{{\"event\":\"init\",\"conversation_id\":\"{id}\",\"init\":{{\"cwd\":\"/w\",\"tools\":[\"view_file\"],\"permission_mode\":\"always-proceed\"}}}}";

    private static string Step(string type, string? text = null, int input = 0, int output = 0, int thinking = 0, int cacheRead = 0)
    {
        var body = $"\"conversation_id\":\"{Conversation}\",\"step_index\":1,\"state\":\"DONE\",\"step_type\":\"{type}\"";
        if (text is not null) body += $",\"text_delta\":\"{text}\"";
        if (input + output > 0)
            body += $",\"usage\":{{\"input_tokens\":{input},\"output_tokens\":{output},\"thinking_tokens\":{thinking},\"cache_read_tokens\":{cacheRead},\"total_tokens\":{input + output + thinking}}}";
        return $"{{\"event\":\"step_update\",\"step_update\":{{{body}}}}}";
    }

    private static string Result(string status, string response, string? error = null, string id = Conversation)
        => $"{{\"event\":\"result\",\"result\":{{\"conversation_id\":\"{id}\",\"status\":\"{status}\",\"response\":\"{response}\""
            + (error is null ? "" : $",\"error\":\"{error}\"")
            + ",\"num_turns\":3,\"usage\":{\"input_tokens\":999,\"output_tokens\":999}}}";

    private static Task<StreamResult> Read(Func<string, Task>? progress, Action<string>? onSessionId, params string[] lines)
        => AgyAdapter.ReadStreamJsonAsync(AdapterStreamReaders.Of(lines), progress, onSessionId, TestContext.Current.CancellationToken);

    [Fact]
    public async Task The_result_holds_the_answer_and_the_conversation_id()
    {
        var output = await Read(null, null, Init(), Step("user_input"), Step("agent_response", "PONG\\n", 11, 3), Result("SUCCESS", "PONG\\n"));

        Assert.Equal("PONG", output.Text);
        Assert.Equal(Conversation, output.SessionId);
        Assert.Null(output.ProviderError);
        Assert.Null(output.Truncation);
        Assert.True(output.SawJsonEvents);
    }

    [Fact]
    public async Task Agent_text_is_streamed_as_progress_and_stands_in_for_a_result_without_a_response()
    {
        var progress = new List<string>();
        var output = await Read(
            chunk => { progress.Add(chunk); return Task.CompletedTask; }, null,
            Init(), Step("agent_response", "one ", 1, 1), Step("agent_response", "two", 1, 1), Result("SUCCESS", ""));

        Assert.Equal("one two", output.Text);
        Assert.Equal(["one", "two"], progress);
    }

    [Fact]
    public async Task The_conversation_id_fires_OnSessionId_once_from_the_init_event()
    {
        var captured = new List<string>();
        await Read(null, captured.Add, Init(), Init(), Result("SUCCESS", "ok"));

        Assert.Equal([Conversation], captured);
    }

    [Fact]
    public async Task A_resumed_conversation_that_was_not_found_names_the_new_one()
    {
        var output = await Read(null, null, Init("aaaaaaaa-0000-0000-0000-000000000000"), Result("SUCCESS", "ok", id: "bbbbbbbb-0000-0000-0000-000000000000"));

        Assert.Equal("bbbbbbbb-0000-0000-0000-000000000000", output.SessionId);
    }

    [Fact]
    public async Task A_failed_turn_carries_the_providers_error()
    {
        var output = await Read(null, null, Init(), Step("error_message"), Result("ERROR", "", "API error (attempt 7): RESOURCE_EXHAUSTED (code 429): Quota exceeded"));

        Assert.Equal("API error (attempt 7): RESOURCE_EXHAUSTED (code 429): Quota exceeded", output.ProviderError);
        Assert.Equal("", output.Text);
    }

    [Fact]
    public async Task A_failed_turn_with_no_error_text_names_its_status()
    {
        var output = await Read(null, null, Init(), Result("CANCELLED", ""));

        Assert.Equal("the turn ended with status CANCELLED", output.ProviderError);
    }

    [Fact]
    public async Task A_stream_that_ends_without_a_result_ended_before_its_turn_did()
    {
        var output = await Read(null, null, Init(), Step("agent_response", "half a sen", 1, 1));

        Assert.NotNull(output.Truncation);
        Assert.Equal("half a sen", output.Text);
    }

    [Fact]
    public async Task Lines_that_are_not_events_are_kept_in_the_raw_output_and_ignored()
    {
        var output = await Read(null, null, "Authentication required. Please visit the URL to log in:", Init(), Result("SUCCESS", "ok"));

        Assert.Equal("ok", output.Text);
        Assert.Contains("Authentication required", output.RawStdout);
    }

    [Fact]
    public async Task Output_with_no_events_at_all_is_not_an_event_stream()
    {
        var output = await Read(null, null, "error: authentication failed or timed out");

        Assert.False(output.SawJsonEvents);
        Assert.Null(output.Truncation);
    }

    [Fact]
    public void Usage_is_the_sum_of_this_launchs_agent_responses_not_the_results_running_total()
    {
        var raw = string.Join('\n', Init(), Step("agent_response", "a", 100, 10, thinking: 5, cacheRead: 40), Step("agent_response", "b", 200, 20), Result("SUCCESS", "ab"));

        var usage = AdapterUsageParser.ParseAgy(raw);

        Assert.NotNull(usage);
        Assert.Equal(300, usage!.InputTokens);
        Assert.Equal(35, usage.OutputTokens);
        Assert.Equal(40, usage.CacheReadTokens);
        Assert.Null(usage.CostUsd);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    public void Usage_is_unknown_without_a_reporting_step(string raw)
        => Assert.Null(AdapterUsageParser.ParseAgy(raw));

    [Fact]
    public void Usage_ignores_steps_that_are_not_model_responses()
        => Assert.Null(AdapterUsageParser.ParseAgy(string.Join('\n', Init(), Step("user_input", null, 5, 5), Result("SUCCESS", "x"))));
}
