using ILD.Core.Services.Implementations;

namespace ILD.Tests;

public class DotEnvParserTests
{
    [Fact]
    public void Parses_simple_key_value_pairs()
    {
        var env = DotEnvParser.Parse("FOO=bar\nBAZ=qux");
        Assert.Equal("bar", env["FOO"]);
        Assert.Equal("qux", env["BAZ"]);
        Assert.Equal(2, env.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  \n\t")]
    public void Empty_or_whitespace_input_yields_no_entries(string? input)
    {
        Assert.Empty(DotEnvParser.Parse(input));
    }

    [Fact]
    public void Skips_blank_lines_and_full_line_comments()
    {
        var env = DotEnvParser.Parse("# a comment\n\nFOO=bar\n   # indented comment\nBAZ=qux\n");
        Assert.Equal(2, env.Count);
        Assert.Equal("bar", env["FOO"]);
        Assert.Equal("qux", env["BAZ"]);
    }

    [Fact]
    public void Inline_hash_is_kept_as_part_of_the_value()
    {
        // A '#' is legal in a password or URL fragment; truncating it would corrupt
        // the secret, which is worse than keeping a stray inline comment.
        var env = DotEnvParser.Parse("TOKEN=abc#123 # not a comment");
        Assert.Equal("abc#123 # not a comment", env["TOKEN"]);
    }

    [Fact]
    public void Preserves_equals_signs_inside_the_value()
    {
        var env = DotEnvParser.Parse("CONN=key=value;other=thing");
        Assert.Equal("key=value;other=thing", env["CONN"]);
    }

    [Fact]
    public void Trims_whitespace_around_key_and_unquoted_value()
    {
        var env = DotEnvParser.Parse("  FOO  =   bar  ");
        Assert.True(env.ContainsKey("FOO"));
        Assert.Equal("bar", env["FOO"]);
    }

    [Fact]
    public void Strips_matching_double_quotes_and_unescapes_common_sequences()
    {
        var env = DotEnvParser.Parse("MSG=\"line1\\nline2\\ttabbed\"");
        Assert.Equal("line1\nline2\ttabbed", env["MSG"]);
    }

    [Fact]
    public void Strips_matching_single_quotes_literally()
    {
        var env = DotEnvParser.Parse("RAW='no \\n escapes here'");
        Assert.Equal("no \\n escapes here", env["RAW"]);
    }

    [Fact]
    public void Quotes_preserve_leading_and_trailing_spaces()
    {
        var env = DotEnvParser.Parse("PADDED=\"  spaced  \"");
        Assert.Equal("  spaced  ", env["PADDED"]);
    }

    [Fact]
    public void Strips_optional_export_prefix()
    {
        var env = DotEnvParser.Parse("export FOO=bar");
        Assert.Equal("bar", env["FOO"]);
        Assert.False(env.ContainsKey("export FOO"));
    }

    [Fact]
    public void Ignores_lines_without_an_equals_or_with_an_empty_key()
    {
        var env = DotEnvParser.Parse("JUST_A_WORD\n=novalue\nFOO=bar");
        Assert.Single(env);
        Assert.Equal("bar", env["FOO"]);
    }

    [Fact]
    public void Last_duplicate_key_wins()
    {
        var env = DotEnvParser.Parse("FOO=first\nFOO=second");
        Assert.Equal("second", env["FOO"]);
    }

    [Fact]
    public void Handles_empty_values()
    {
        var env = DotEnvParser.Parse("EMPTY=\nNEXT=x");
        Assert.Equal(string.Empty, env["EMPTY"]);
        Assert.Equal("x", env["NEXT"]);
    }

    [Fact]
    public void Keys_are_case_sensitive()
    {
        var env = DotEnvParser.Parse("Foo=upper\nfoo=lower");
        Assert.Equal("upper", env["Foo"]);
        Assert.Equal("lower", env["foo"]);
    }

    [Fact]
    public void Tolerates_crlf_line_endings()
    {
        var env = DotEnvParser.Parse("FOO=bar\r\nBAZ=qux\r\n");
        Assert.Equal("bar", env["FOO"]);
        Assert.Equal("qux", env["BAZ"]);
    }
}
