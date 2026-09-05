using System.Text.Json;
using ILD.Core.Services.Implementations.Adapters;

namespace ILD.Tests;

public class ToolMarkerFormatterTests
{
    private static JsonElement Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void Format_picks_the_most_telling_argument_regardless_of_property_order()
    {
        var marker = ToolMarkerFormatter.Format(
            "Bash",
            Args("""{"description":"run the suite","timeout":"600","command":"npm test"}"""));

        Assert.Equal("[tool: Bash] npm test", marker);
    }

    [Theory]
    [InlineData("file_path")]
    [InlineData("filePath")]
    public void Format_reads_a_preferred_key_however_the_cli_spells_it(string key)
    {
        var marker = ToolMarkerFormatter.Format("Write", Args($$"""{"content":"...","{{key}}":"src/a.cs"}"""));

        Assert.Equal("[tool: Write] src/a.cs", marker);
    }

    [Fact]
    public void Format_falls_back_to_the_first_string_argument()
    {
        var marker = ToolMarkerFormatter.Format("mcp__ild__get_workitem", Args("""{"limit":5,"workItemId":"wi-144"}"""));

        Assert.Equal("[tool: mcp__ild__get_workitem] wi-144", marker);
    }

    [Fact]
    public void Format_leaves_a_payload_it_cannot_name_off_the_marker()
    {
        var marker = ToolMarkerFormatter.Format(
            "mcp__docs__publish",
            Args($$"""{"body":"{{new string('p', 5000)}}"}"""));

        Assert.Equal("[tool: mcp__docs__publish]", marker);
    }

    [Fact]
    public void Format_keeps_scanning_past_a_payload_for_something_that_reads_like_a_label()
    {
        var marker = ToolMarkerFormatter.Format(
            "mcp__docs__publish",
            Args($$"""{"body":"{{new string('p', 5000)}}","slug":"release-notes"}"""));

        Assert.Equal("[tool: mcp__docs__publish] release-notes", marker);
    }

    [Fact]
    public void Format_summarizes_a_long_argument_it_can_name()
    {
        // The payload guard is for arguments the formatter cannot name: a heredoc
        // under `command` is still exactly what the call is doing.
        var marker = ToolMarkerFormatter.Format("Bash", Args($$"""{"command":"git commit -m {{new string('m', 5000)}}"}"""));

        Assert.StartsWith("[tool: Bash] git commit -m mmm", marker);
        Assert.EndsWith("…", marker);
    }

    [Fact]
    public void Format_collapses_a_multi_line_argument_to_one_line()
    {
        var marker = ToolMarkerFormatter.Format(
            "Bash",
            Args("""{"command":"cat <<'EOF' > a.txt\n  first\n\n\tsecond\nEOF"}"""));

        Assert.Equal("[tool: Bash] cat <<'EOF' > a.txt first second EOF", marker);
        Assert.DoesNotContain('\n', marker);
    }

    [Fact]
    public void Format_truncates_a_long_argument_with_an_ellipsis()
    {
        var marker = ToolMarkerFormatter.Format("Bash", Args($$"""{"command":"{{new string('x', 500)}}"}"""));

        var argument = marker["[tool: Bash] ".Length..];
        Assert.Equal(120, argument.Length);
        Assert.EndsWith("…", argument);
        Assert.Equal(new string('x', 119), argument[..^1]);
    }

    [Fact]
    public void Format_holds_the_cap_when_the_collapsed_space_lands_on_the_boundary()
    {
        // 119 characters, a whitespace run, then one last character: the space the
        // collapse inserts is what tips the argument over the cap, and the value
        // ends there, so nothing later would have trimmed it back.
        var marker = ToolMarkerFormatter.Format("Bash", Args($$"""{"command":"{{new string('x', 119)}}\n\n  t"}"""));

        Assert.Equal($"[tool: Bash] {new string('x', 119)}…", marker);
    }

    [Theory]
    [InlineData(115)]
    [InlineData(117)]
    [InlineData(118)]
    [InlineData(119)]
    [InlineData(120)]
    [InlineData(121)]
    public void Format_never_renders_an_argument_past_the_cap(int leadingLength)
    {
        var marker = ToolMarkerFormatter.Format(
            "Bash",
            Args($$"""{"command":"{{new string('x', leadingLength)}} \t tail end"}"""));

        var argument = marker["[tool: Bash] ".Length..];
        Assert.InRange(argument.Length, 1, 120);
        Assert.DoesNotContain(" …", argument);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"recursive":true,"depth":3}""")]
    [InlineData("""{"command":"   "}""")]
    [InlineData("[1,2,3]")]
    public void Format_emits_the_bare_marker_when_no_argument_reads_usefully(string arguments)
    {
        Assert.Equal("[tool: Read]", ToolMarkerFormatter.Format("Read", Args(arguments)));
    }

    [Fact]
    public void Format_emits_the_bare_marker_when_the_tool_has_no_arguments()
    {
        Assert.Equal("[tool: Read]", ToolMarkerFormatter.Format("Read"));
    }

    [Fact]
    public void Format_names_an_unnamed_tool_generically()
    {
        Assert.Equal("[tool] npm test", ToolMarkerFormatter.Format("  ", Args("""{"command":"npm test"}""")));
    }

    [Fact]
    public void Format_summarizes_arguments_delivered_as_a_string()
    {
        Assert.Equal("[tool: Bash] npm test", ToolMarkerFormatter.Format("Bash", Args("\"npm test\"")));
    }
}
