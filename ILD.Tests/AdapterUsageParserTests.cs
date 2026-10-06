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

        var usage = AdapterUsageParser.Parse(stdout);

        Assert.NotNull(usage);
        // input = 100 + 50 + 5 (cache fields fold into input); result event wins
        // over the earlier partial assistant usage.
        Assert.Equal(155, usage!.InputTokens);
        Assert.Equal(40, usage.OutputTokens);
        Assert.Equal(0.0123m, usage.CostUsd);
    }

    [Fact]
    public void Parses_opencode_tokens_and_cost()
    {
        // opencode --format json: assistant/step events carry a `tokens` object
        // (with a nested cache) and a `cost`.
        var stdout =
            "{\"type\":\"step_finish\",\"cost\":0.5,\"tokens\":{\"input\":200,\"output\":80,\"cache\":{\"read\":20,\"write\":10}}}";

        var usage = AdapterUsageParser.Parse(stdout);

        Assert.NotNull(usage);
        Assert.Equal(230, usage!.InputTokens); // 200 + 20 + 10
        Assert.Equal(80, usage.OutputTokens);
        Assert.Equal(0.5m, usage.CostUsd);
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

        Assert.Null(AdapterUsageParser.Parse(stdout));
    }

    [Fact]
    public void Returns_null_for_empty_input()
    {
        Assert.Null(AdapterUsageParser.Parse(null));
        Assert.Null(AdapterUsageParser.Parse(""));
        Assert.Null(AdapterUsageParser.Parse("   "));
    }

    [Fact]
    public void Last_usage_event_wins_for_cumulative_streams()
    {
        // Edge case: multiple usage objects across the stream — the last one
        // (the cumulative final event) must win, not the sum of all.
        var stdout = string.Join('\n',
            "{\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}",
            "{\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}",
            "{\"usage\":{\"input_tokens\":500,\"output_tokens\":250}}");

        var usage = AdapterUsageParser.Parse(stdout);

        Assert.NotNull(usage);
        Assert.Equal(500, usage!.InputTokens);
        Assert.Equal(250, usage.OutputTokens);
    }
}
