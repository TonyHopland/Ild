using ILD.Core.Services.Implementations.Adapters;

namespace ILD.Tests;

public class AdapterUsageParserTests
{
    [Fact]
    public void Parses_claude_result_usage_and_cost()
    {
        // claude-code stream-json: a terminal `result` event carries the
        // cumulative usage and the dollar cost.
        var stdout = string.Join('\n',
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s1\"}",
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"hi\"}],\"usage\":{\"input_tokens\":10,\"output_tokens\":2}}}",
            "{\"type\":\"result\",\"subtype\":\"success\",\"session_id\":\"s1\",\"total_cost_usd\":0.0123,\"usage\":{\"input_tokens\":100,\"cache_read_input_tokens\":50,\"cache_creation_input_tokens\":5,\"output_tokens\":40}}");

        var usage = AdapterUsageParser.ParseClaude(stdout, 0m);

        Assert.NotNull(usage);
        // input = 100 + 50 + 5 (cache fields fold into input); result event wins
        // over the earlier partial assistant usage.
        Assert.Equal(155, usage!.InputTokens);
        Assert.Equal(40, usage.OutputTokens);
        Assert.Equal(0.0123m, usage.CostUsd);
    }

    // Shapes from @earendil-works/pi-coding-agent 1.0.4 docs/json.md and
    // docs/message-types.md: every assistant message carries its own usage, which
    // pi repeats on message_update, turn_end and agent_end.
    private static string PiUsage(int input, int output, int cacheRead, int cacheWrite, decimal cost)
        => $"{{\"input\":{input},\"output\":{output},\"cacheRead\":{cacheRead},\"cacheWrite\":{cacheWrite},"
            + $"\"totalTokens\":{input + output + cacheRead + cacheWrite},"
            + $"\"cost\":{{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"total\":{cost.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}}}";

    private static string PiAssistant(string usage)
        => $"{{\"role\":\"assistant\",\"content\":[{{\"type\":\"text\",\"text\":\"hi\"}}],\"usage\":{usage}}}";

    private static string PiRunStream(decimal firstCost, decimal secondCost)
    {
        var first = PiUsage(100, 10, 20, 5, firstCost);
        var second = PiUsage(200, 30, 40, 0, secondCost);
        return string.Join('\n',
            "{\"type\":\"session\",\"version\":3,\"id\":\"s1\",\"cwd\":\"/w\"}",
            "{\"type\":\"agent_start\"}",
            "{\"type\":\"message_end\",\"message\":{\"role\":\"user\",\"content\":\"go\",\"timestamp\":1}}",
            $"{{\"type\":\"message_update\",\"usage\":{first},\"message\":{PiAssistant(first)},\"assistantMessageEvent\":{{\"type\":\"text_delta\",\"contentIndex\":0,\"delta\":\"hi\"}}}}",
            $"{{\"type\":\"message_end\",\"message\":{PiAssistant(first)}}}",
            $"{{\"type\":\"turn_end\",\"message\":{PiAssistant(first)},\"toolResults\":[]}}",
            $"{{\"type\":\"message_update\",\"usage\":{second},\"message\":{PiAssistant(second)},\"assistantMessageEvent\":{{\"type\":\"text_delta\",\"contentIndex\":0,\"delta\":\"hi\"}}}}",
            $"{{\"type\":\"message_end\",\"message\":{PiAssistant(second)}}}",
            $"{{\"type\":\"turn_end\",\"message\":{PiAssistant(second)},\"toolResults\":[]}}",
            $"{{\"type\":\"agent_end\",\"messages\":[{PiAssistant(first)},{PiAssistant(second)}]}}");
    }

    [Fact]
    public void Pi_usage_sums_assistant_message_ends_not_their_repeats_and_reports_no_cost_for_an_unpriced_model()
    {
        var usage = AdapterUsageParser.ParsePi(PiRunStream(0m, 0m));

        Assert.NotNull(usage);
        Assert.Equal((100 + 20 + 5) + (200 + 40 + 0), usage!.InputTokens);
        Assert.Equal(10 + 30, usage.OutputTokens);
        Assert.Null(usage.CostUsd);
    }

    [Fact]
    public void Pi_usage_sums_the_cost_of_every_assistant_message()
    {
        var usage = AdapterUsageParser.ParsePi(PiRunStream(0.0125m, 0.5m));

        Assert.NotNull(usage);
        Assert.Equal(365, usage!.InputTokens);
        Assert.Equal(40, usage.OutputTokens);
        Assert.Equal(0.5125m, usage.CostUsd);
    }

    [Fact]
    public void Pi_usage_counts_each_compaction_once_alongside_the_assistant_messages()
    {
        // docs/json.md: a successful compaction_end carries the summarizing call's
        // usage under result; an aborted one has no result.
        var stdout = string.Join('\n',
            PiRunStream(0.0125m, 0.5m),
            "{\"type\":\"compaction_start\",\"reason\":\"threshold\"}",
            $"{{\"type\":\"compaction_end\",\"reason\":\"threshold\",\"result\":{{\"summary\":\"s\",\"firstKeptEntryId\":\"e1\",\"tokensBefore\":150000,\"usage\":{PiUsage(1000, 50, 0, 7, 0.25m)},\"details\":{{}}}},\"aborted\":false,\"willRetry\":false}}",
            "{\"type\":\"compaction_end\",\"reason\":\"manual\",\"aborted\":true,\"willRetry\":false}");

        var usage = AdapterUsageParser.ParsePi(stdout);

        Assert.NotNull(usage);
        Assert.Equal(365 + 1007, usage!.InputTokens);
        Assert.Equal(40 + 50, usage.OutputTokens);
        Assert.Equal(0.7625m, usage.CostUsd);
    }

    [Fact]
    public void Pi_usage_is_null_without_an_assistant_message_usage()
    {
        // Usage only on a user message and on streaming updates is not a recorded figure.
        var stdout = string.Join('\n',
            "{\"type\":\"session\",\"version\":3,\"id\":\"s1\",\"cwd\":\"/w\"}",
            $"{{\"type\":\"message_end\",\"message\":{{\"role\":\"user\",\"content\":\"go\",\"usage\":{PiUsage(5, 5, 0, 0, 0m)}}}}}",
            $"{{\"type\":\"message_update\",\"usage\":{PiUsage(7, 7, 0, 0, 0m)},\"assistantMessageEvent\":{{\"type\":\"text_delta\",\"delta\":\"hi\"}}}}",
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]}}",
            "not json");

        Assert.Null(AdapterUsageParser.ParsePi(stdout));
        Assert.Null(AdapterUsageParser.ParsePi(null));
    }

    [Fact]
    public void Returns_null_when_no_usage_present()
    {
        var stdout = string.Join('\n',
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]}}",
            "not json at all",
            "{\"type\":\"result\",\"subtype\":\"success\"}");

        Assert.Null(AdapterUsageParser.ParseClaude(stdout, 0m));
    }

    [Fact]
    public void Returns_null_for_empty_input()
    {
        Assert.Null(AdapterUsageParser.ParseClaude(null, 0m));
        Assert.Null(AdapterUsageParser.ParseClaude("", 0m));
        Assert.Null(AdapterUsageParser.ParseClaude("   ", 0m));
    }

    [Fact]
    public void Last_usage_event_wins_for_cumulative_streams()
    {
        // Edge case: multiple result events across the stream — the last one
        // (the cumulative final event) must win, not the sum of all.
        var stdout = string.Join('\n',
            "{\"type\":\"result\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}",
            "{\"type\":\"result\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}",
            "{\"type\":\"result\",\"usage\":{\"input_tokens\":500,\"output_tokens\":250}}");

        var usage = AdapterUsageParser.ParseClaude(stdout, 0m);

        Assert.NotNull(usage);
        Assert.Equal(500, usage!.InputTokens);
        Assert.Equal(250, usage.OutputTokens);
    }

    [Fact]
    public void Claude_cost_comes_only_from_the_results_total_never_from_a_cost_elsewhere_in_the_stream()
    {
        var stdout = string.Join('\n',
            "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":{\"cost\":9.5,\"costUSD\":7.25}}]}}",
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"hi\"}],\"cost\":4.5}}",
            "{\"type\":\"result\",\"subtype\":\"success\",\"costUSD\":3.25,\"modelUsage\":{\"m\":{\"costUSD\":2.5}},\"usage\":{\"input_tokens\":100,\"output_tokens\":40}}");

        var usage = AdapterUsageParser.ParseClaude(stdout, 0m);

        Assert.NotNull(usage);
        Assert.Equal(100, usage!.InputTokens);
        Assert.Equal(40, usage.OutputTokens);
        Assert.Null(usage.CostUsd);
    }

    // Claude Code 2.1.x appends one cost-state line per invocation to the session
    // transcript and restores the last one on --resume, so the result event's
    // total_cost_usd is the session's running total.
    private static string ClaudeCostState(string sessionId, string total)
        => $"{{\"type\":\"cost-state\",\"sessionId\":\"{sessionId}\",\"totalCostUSD\":{total},\"totalAPIDuration\":11774,\"modelUsage\":{{}}}}";

    private static string ClaudeResultStream(string sessionId, string totalCostUsd)
        => string.Join('\n',
            $"{{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"{sessionId}\"}}",
            $"{{\"type\":\"assistant\",\"session_id\":\"{sessionId}\",\"message\":{{\"content\":[{{\"type\":\"text\",\"text\":\"b\"}}],\"usage\":{{\"input_tokens\":3,\"output_tokens\":1}}}}}}",
            $"{{\"type\":\"result\",\"subtype\":\"success\",\"session_id\":\"{sessionId}\",\"total_cost_usd\":{totalCostUsd},\"usage\":{{\"input_tokens\":100,\"cache_creation_input_tokens\":5,\"cache_read_input_tokens\":50,\"output_tokens\":40}}}}");

    [Fact]
    public void Claude_session_cost_before_is_the_last_valid_cost_state_of_that_session_not_a_sum()
    {
        var transcript = string.Join('\n',
            "{\"type\":\"user\",\"sessionId\":\"s1\",\"message\":{\"role\":\"user\",\"content\":\"go\"}}",
            ClaudeCostState("s1", "1.18"),
            ClaudeCostState("s1", "3.50"),
            ClaudeCostState("other", "9.75"),
            "{\"type\":\"cost-state\",\"sessionId\":\"s1\",\"totalCostUSD\":\"12\"}",
            "{\"type\":\"cost-state\",\"sessionId\":\"s1\"}",
            "{\"type\":\"cost-state\",\"sessionId\":\"s1\",\"totalCostUSD\":",
            "not json");

        Assert.Equal(3.50m, AdapterUsageParser.ParseClaudeSessionCostBefore(new StringReader(transcript), "s1"));
        Assert.Equal(9.75m, AdapterUsageParser.ParseClaudeSessionCostBefore(new StringReader(transcript), "other"));
    }

    [Fact]
    public void Claude_session_cost_before_is_zero_when_the_transcript_restores_nothing_for_that_session()
    {
        Assert.Equal(0m, AdapterUsageParser.ParseClaudeSessionCostBefore(new StringReader(""), "s1"));
        Assert.Equal(0m, AdapterUsageParser.ParseClaudeSessionCostBefore(new StringReader("  \n "), "s1"));
        Assert.Equal(0m, AdapterUsageParser.ParseClaudeSessionCostBefore(new StringReader(ClaudeCostState("other", "3.50")), "s1"));
        Assert.Equal(0m, AdapterUsageParser.ParseClaudeSessionCostBefore(new StringReader(string.Join('\n',
            "{\"type\":\"user\",\"sessionId\":\"s1\",\"totalCostUSD\":3.5}",
            "{\"type\":\"cost-state\",\"sessionId\":\"s1\",\"totalCostUSD\":\"3.5\"}",
            "{\"type\":\"cost-state\",\"sessionId\":\"s1\",\"totalCostUSD\":null}",
            "{\"type\":\"cost-state\",\"sessionId\":\"s1\",\"totalCostUSD\":3.5",
            "not json")), "s1"));
    }

    [Fact]
    public void Claude_new_session_records_the_streams_total_and_its_result_tokens()
    {
        var usage = AdapterUsageParser.ParseClaude(ClaudeResultStream("s1", "0.0123"), 0m);

        Assert.NotNull(usage);
        Assert.Equal(155, usage!.InputTokens);
        Assert.Equal(40, usage.OutputTokens);
        Assert.Equal(0.0123m, usage.CostUsd);
    }

    [Fact]
    public void Claude_resumed_session_records_what_this_invocation_added_to_the_cumulative_total()
    {
        // WI-270's Q&A run: a one-letter answer took the session from 3.50 to 3.56.
        var usage = AdapterUsageParser.ParseClaude(ClaudeResultStream("s1", "3.56"), 3.50m);

        Assert.NotNull(usage);
        Assert.Equal(0.06m, usage!.CostUsd);
        Assert.Equal(155, usage.InputTokens);
        Assert.Equal(40, usage.OutputTokens);
    }

    [Fact]
    public void Claude_total_below_the_earlier_total_keeps_the_tokens_with_an_unknown_cost()
    {
        var usage = AdapterUsageParser.ParseClaude(ClaudeResultStream("s1", "3.00"), 3.50m);

        Assert.NotNull(usage);
        Assert.Null(usage!.CostUsd);
        Assert.Equal(155, usage.InputTokens);
        Assert.Equal(40, usage.OutputTokens);
    }

    [Fact]
    public void Claude_stream_without_a_cost_keeps_its_tokens_and_one_without_usage_records_none()
    {
        Assert.Null(AdapterUsageParser.ParseClaude(null, 3.50m));
        Assert.Null(AdapterUsageParser.ParseClaude("{\"type\":\"result\",\"subtype\":\"success\"}", 3.50m));

        var noCost = AdapterUsageParser.ParseClaude(
            "{\"type\":\"result\",\"usage\":{\"input_tokens\":10,\"output_tokens\":2}}", 3.50m);
        Assert.NotNull(noCost);
        Assert.Equal(10, noCost!.InputTokens);
        Assert.Null(noCost.CostUsd);
    }

    // Shape of `opencode run --format json` 1.19.1 (packages/schema/src/v1/session.ts
    // StepFinishPart, emitted by cli/cmd/run.ts as {type, timestamp, sessionID, part});
    // a live run showed each step's tokens and cost are that one model call's.
    private static string OpenCodeStep(long input, long output, long reasoning, long cacheRead, long cacheWrite, string? cost)
        => "{\"type\":\"step_finish\",\"timestamp\":3,\"sessionID\":\"ses_x\",\"part\":{\"id\":\"prt_2\",\"messageID\":\"msg_1\",\"sessionID\":\"ses_x\",\"type\":\"step-finish\",\"reason\":\"stop\""
            + (cost is null ? "" : $",\"cost\":{cost}")
            + $",\"tokens\":{{\"total\":{input + output + reasoning + cacheRead + cacheWrite},\"input\":{input},\"output\":{output},\"reasoning\":{reasoning},\"cache\":{{\"read\":{cacheRead},\"write\":{cacheWrite}}}}}}}}}";

    private const string OpenCodeStepStart =
        "{\"type\":\"step_start\",\"timestamp\":1,\"sessionID\":\"ses_x\",\"part\":{\"id\":\"prt_1\",\"messageID\":\"msg_1\",\"sessionID\":\"ses_x\",\"type\":\"step-start\"}}";

    private const string OpenCodeText =
        "{\"type\":\"text\",\"timestamp\":2,\"sessionID\":\"ses_x\",\"part\":{\"id\":\"prt_3\",\"messageID\":\"msg_1\",\"sessionID\":\"ses_x\",\"type\":\"text\",\"text\":\"hi\"}}";

    [Fact]
    public void OpenCode_sums_every_step_finish_of_a_multi_step_turn()
    {
        var stdout = string.Join('\n',
            OpenCodeStepStart,
            "{\"type\":\"tool_use\",\"timestamp\":2,\"sessionID\":\"ses_x\",\"part\":{\"type\":\"tool\",\"tool\":\"read\",\"state\":{\"status\":\"completed\"}}}",
            OpenCodeStep(6023, 56, 0, 1792, 0, "0.006135"),
            OpenCodeStepStart,
            OpenCodeText,
            OpenCodeStep(6143, 14, 0, 1792, 0, "0.006171"));

        var usage = AdapterUsageParser.ParseOpenCode(stdout);

        Assert.NotNull(usage);
        Assert.Equal(15750, usage!.InputTokens);
        Assert.Equal(70, usage.OutputTokens);
        Assert.Equal(0.012306m, usage.CostUsd);
    }

    [Fact]
    public void OpenCode_single_step_maps_input_with_both_caches_and_output_without_reasoning()
    {
        var usage = AdapterUsageParser.ParseOpenCode(string.Join('\n',
            OpenCodeStepStart, OpenCodeText, OpenCodeStep(200, 80, 33, 20, 10, "0.5")));

        Assert.NotNull(usage);
        Assert.Equal(230, usage!.InputTokens);
        Assert.Equal(80, usage.OutputTokens);
        Assert.Equal(0.5m, usage.CostUsd);
    }

    [Fact]
    public void OpenCode_cost_sums_the_steps_that_report_one_and_is_null_when_none_does()
    {
        var oneCosted = AdapterUsageParser.ParseOpenCode(string.Join('\n',
            OpenCodeStep(100, 10, 0, 0, 0, null),
            OpenCodeStep(50, 5, 0, 0, 0, "0.25"),
            OpenCodeStep(0, 0, 0, 0, 0, "0")));

        Assert.NotNull(oneCosted);
        Assert.Equal(150, oneCosted!.InputTokens);
        Assert.Equal(15, oneCosted.OutputTokens);
        Assert.Equal(0.25m, oneCosted.CostUsd);

        var uncosted = AdapterUsageParser.ParseOpenCode(string.Join('\n',
            OpenCodeStep(100, 10, 0, 0, 0, null),
            OpenCodeStep(50, 5, 0, 0, 0, null)));

        Assert.NotNull(uncosted);
        Assert.Equal(150, uncosted!.InputTokens);
        Assert.Equal(15, uncosted.OutputTokens);
        Assert.Null(uncosted.CostUsd);
    }

    [Fact]
    public void OpenCode_counts_only_step_finish_events_and_each_once()
    {
        // A non-step event carrying a tokens object and a cost (an assistant
        // message summary repeating its steps) must not be added on top of them.
        var stdout = string.Join('\n',
            OpenCodeStepStart,
            OpenCodeStep(6023, 56, 0, 1792, 0, "0.006135"),
            "{\"type\":\"message\",\"timestamp\":4,\"sessionID\":\"ses_x\",\"part\":{\"type\":\"assistant\",\"cost\":0.006135,\"tokens\":{\"input\":6023,\"output\":56,\"reasoning\":0,\"cache\":{\"read\":1792,\"write\":0}}}}",
            "{\"type\":\"step_finish\",\"timestamp\":5,\"sessionID\":\"ses_x\"}",
            "not json");

        var usage = AdapterUsageParser.ParseOpenCode(stdout);

        Assert.NotNull(usage);
        Assert.Equal(7815, usage!.InputTokens);
        Assert.Equal(56, usage.OutputTokens);
        Assert.Equal(0.006135m, usage.CostUsd);
    }

    [Fact]
    public void OpenCode_returns_null_without_step_finish_usage()
    {
        Assert.Null(AdapterUsageParser.ParseOpenCode(null));
        Assert.Null(AdapterUsageParser.ParseOpenCode(""));
        Assert.Null(AdapterUsageParser.ParseOpenCode(string.Join('\n',
            OpenCodeStepStart,
            OpenCodeText,
            "{\"type\":\"step_finish\",\"timestamp\":3,\"sessionID\":\"ses_x\",\"part\":{\"type\":\"step-finish\",\"id\":\"prt_2\",\"messageID\":\"msg_1\",\"sessionID\":\"ses_x\"}}")));
    }
}
