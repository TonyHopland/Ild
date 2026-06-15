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

    [Fact]
    public void Parses_pi_usage_without_cost()
    {
        // pi reports tokens but no monetary cost — CostUsd stays null.
        var stdout =
            "{\"type\":\"turn_end\",\"message\":{\"role\":\"assistant\",\"usage\":{\"input_tokens\":12,\"output_tokens\":34}}}";

        var usage = AdapterUsageParser.Parse(stdout);

        Assert.NotNull(usage);
        Assert.Equal(12, usage!.InputTokens);
        Assert.Equal(34, usage.OutputTokens);
        Assert.Null(usage.CostUsd);
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
