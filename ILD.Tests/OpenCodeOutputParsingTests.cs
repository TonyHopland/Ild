using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;

namespace ILD.Tests;

/// <summary>
/// The <c>opencode run --format json</c> output parsers and result assembly, fed real event
/// shapes directly rather than through a stand-in CLI process.
/// </summary>
public class OpenCodeOutputParsingTests
{
    private const string StepStart = "{\"type\":\"step_start\",\"timestamp\":1,\"sessionID\":\"ses_x\",\"part\":{\"type\":\"step-start\",\"id\":\"prt_1\",\"messageID\":\"msg_1\",\"sessionID\":\"ses_x\"}}";
    private const string StepFinish = "{\"type\":\"step_finish\",\"timestamp\":3,\"sessionID\":\"ses_x\",\"part\":{\"type\":\"step-finish\",\"id\":\"prt_2\",\"messageID\":\"msg_1\",\"sessionID\":\"ses_x\"}}";
    private const string TextEvent = "{\"type\":\"text\",\"timestamp\":2,\"sessionID\":\"ses_x\",\"part\":{\"type\":\"text\",\"text\":\"final answer\",\"messageID\":\"msg_1\",\"sessionID\":\"ses_x\"}}";

    private static string Events(params string[] lines) => string.Join("\n", lines);

    [Theory]
    [InlineData("{\"text\":\"hello\",\"sessionId\":\"session-from-output\"}", "session-from-output")]
    [InlineData("{\"text\":\"hello\",\"session\":{\"id\":\"nested-session\"}}", "nested-session")]
    [InlineData("{\"type\":\"text\",\"sessionID\":\"ses_x\",\"part\":{\"text\":\"a\"}}", "ses_x")]
    [InlineData("{\"type\":\"step_start\"}", null)]
    [InlineData("not json", null)]
    [InlineData("", null)]
    public void The_session_id_is_found_at_the_root_or_nested_in_a_session_object(string line, string? expected)
        => Assert.Equal(expected, OpenCodeAdapter.ExtractSessionIdFromLine(line));

    [Fact]
    public void The_last_session_id_seen_in_the_stream_is_the_sessions()
    {
        var (_, sessionId, _, _) = OpenCodeAdapter.ExtractTextAndSessionIdFromJsonEvents(
            Events("{\"text\":\"a\",\"sessionId\":\"first\"}", "{\"text\":\"b\",\"sessionId\":\"second\"}"));

        Assert.Equal("second", sessionId);
    }

    [Theory]
    [InlineData("{\"text\":\"thinking...\"}", "thinking...")]
    [InlineData("{\"type\":\"text\",\"text\":\"reading file...\"}", "reading file...")]
    [InlineData("{\"type\":\"step_start\"}", "[step_start]")]
    [InlineData("{\"type\":\"step_finish\"}", "[step_finish]")]
    [InlineData("{\"type\":\"text\",\"text\":\"\"}", "[text]")]
    [InlineData("line one", "line one")]
    [InlineData("   ", null)]
    public void A_line_becomes_the_text_the_live_view_shows(string line, string? expected)
        => Assert.Equal(expected, OpenCodeAdapter.ExtractTextFromJsonLine(line));

    [Fact]
    public void A_text_event_part_shows_only_its_text_not_the_envelope()
        => Assert.Equal("final answer", OpenCodeAdapter.ExtractTextFromJsonLine(TextEvent));

    [Fact]
    public void A_tool_event_shows_a_marker_from_the_wrapped_part_and_not_the_written_content()
    {
        var line = "{\"type\":\"tool_use\",\"sessionID\":\"ses-1\",\"part\":{\"type\":\"tool\",\"tool\":\"write\",\"state\":" +
            "{\"status\":\"completed\",\"input\":{\"filePath\":\"notes.md\",\"content\":\"line one\\nline two\"},\"output\":\"done\"}}}";

        var text = OpenCodeAdapter.ExtractTextFromJsonLine(line);

        Assert.Equal("[tool: write] notes.md", text);
        Assert.DoesNotContain("line two", text);
    }

    [Fact]
    public void A_flat_tool_event_without_arguments_shows_the_bare_tool_name()
        => Assert.Equal("[tool: read]", OpenCodeAdapter.ExtractTextFromJsonLine("{\"type\":\"tool_use\",\"tool\":\"read\"}"));

    [Fact]
    public void Text_comes_from_the_text_parts_and_boundary_events_add_nothing()
    {
        var (text, sessionId, error, sawJsonEvents) = OpenCodeAdapter.ExtractTextAndSessionIdFromJsonEvents(
            Events(StepStart, TextEvent, StepFinish));

        Assert.Equal("final answer", text);
        Assert.Equal("ses_x", sessionId);
        Assert.Null(error);
        Assert.True(sawJsonEvents);
    }

    [Fact]
    public void What_a_tool_wrote_is_not_part_of_the_reply()
    {
        var (text, _, _, _) = OpenCodeAdapter.ExtractTextAndSessionIdFromJsonEvents(Events(
            StepStart,
            "{\"type\":\"tool_use\",\"sessionID\":\"ses_x\",\"part\":{\"type\":\"tool\",\"tool\":\"write\",\"state\":{\"status\":\"completed\",\"input\":{\"filePath\":\"hello.txt\",\"content\":\"hi\"},\"output\":\"Wrote file\"}}}",
            TextEvent,
            StepFinish));

        Assert.Equal("final answer", text);
    }

    [Fact]
    public void Stdout_that_is_not_typed_events_is_not_taken_for_a_json_stream()
    {
        var (_, _, _, sawJsonEvents) = OpenCodeAdapter.ExtractTextAndSessionIdFromJsonEvents(
            Events("{\"PATH\":\"/usr/bin\"}", "plain"));

        Assert.False(sawJsonEvents);
    }

    [Fact]
    public void An_error_event_yields_the_providers_message()
    {
        var (_, _, error, _) = OpenCodeAdapter.ExtractTextAndSessionIdFromJsonEvents(
            Events("{\"type\":\"error\",\"timestamp\":1,\"sessionID\":\"ses_x\",\"error\":{\"name\":\"ProviderAuthError\",\"data\":{\"message\":\"invalid api key\"}}}"));

        Assert.Equal("invalid api key", error);
    }

    [Fact]
    public void The_usage_of_every_step_in_the_turn_is_summed()
    {
        // Shape of opencode 1.19.1 run --format json: each step_finish part carries that one model call's tokens and cost.
        var stdout = Events(
            StepStart,
            "{\"type\":\"step_finish\",\"timestamp\":2,\"sessionID\":\"ses_x\",\"part\":{\"type\":\"step-finish\",\"id\":\"prt_2\",\"messageID\":\"msg_1\",\"sessionID\":\"ses_x\",\"reason\":\"tool-calls\",\"cost\":0.006135,\"tokens\":{\"total\":7871,\"input\":6023,\"output\":56,\"reasoning\":0,\"cache\":{\"read\":1792,\"write\":0}}}}",
            TextEvent,
            "{\"type\":\"step_finish\",\"timestamp\":5,\"sessionID\":\"ses_x\",\"part\":{\"type\":\"step-finish\",\"id\":\"prt_4\",\"messageID\":\"msg_2\",\"sessionID\":\"ses_x\",\"reason\":\"stop\",\"cost\":0.006171,\"tokens\":{\"total\":7949,\"input\":6143,\"output\":14,\"reasoning\":0,\"cache\":{\"read\":1792,\"write\":0}}}}");

        var usage = AdapterUsageParser.ParseOpenCode(stdout);

        Assert.Equal("final answer", OpenCodeAdapter.ExtractTextAndSessionIdFromJsonEvents(stdout).Text);
        Assert.Equal(15750, usage!.InputTokens);
        Assert.Equal(70, usage.OutputTokens);
        Assert.Equal(0.012306m, usage.CostUsd);
    }

    [Fact]
    public void The_latest_assistant_text_of_the_exported_session_stands_in_when_stdout_has_none()
    {
        var export = "{\"messages\":[{\"role\":\"assistant\",\"parts\":[{\"type\":\"text\",\"text\":\"from the export\"}]}]}";

        Assert.Equal("from the export", OpenCodeAdapter.ExtractLatestAssistantTextFromSessionJson(export));
    }
}

public class OpenCodeRunArgumentsTests
{
    [Fact]
    public void The_worktree_is_passed_with_dir_and_the_session_only_when_there_is_one()
    {
        var withSession = OpenCodeAdapter.BuildRunProcessStartInfo("opencode", "/tmp/wt", "my prompt", "p/m", "{}", "test-session-abc").ArgumentList;
        var without = OpenCodeAdapter.BuildRunProcessStartInfo("opencode", "/tmp/wt", "my prompt", "p/m", "{}", null).ArgumentList;

        Assert.Equal("/tmp/wt", withSession[withSession.IndexOf("--dir") + 1]);
        Assert.Equal("test-session-abc", withSession[withSession.IndexOf("--session") + 1]);
        Assert.DoesNotContain("--session", without);
    }

    [Fact]
    public void A_prompt_starting_with_dashes_is_one_argument_after_the_separator()
    {
        var args = OpenCodeAdapter.BuildRunProcessStartInfo("opencode", "/tmp/wt", "---\nname: to-issues\n", "p/m", "{}", null).ArgumentList;

        Assert.Equal(["--", "---\nname: to-issues\n"], args.Skip(args.IndexOf("--")));
    }
}
