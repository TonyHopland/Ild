using System.Text.Json;
using ILD.Core.Services.Implementations.Adapters;

namespace ILD.Tests;

public class ToolMarkerFormatterTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

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
