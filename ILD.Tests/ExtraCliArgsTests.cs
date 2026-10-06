using ILD.Core.Services.Implementations.Adapters;

namespace ILD.Tests;

/// <summary>
/// The per-provider "Extra CLI arguments" value: split like a shell command
/// line without ever running one, checked on save against the flags ILD owns,
/// and shown back in a form that splits to the same tokens.
/// </summary>
public class ExtraCliArgsTests
{
    public static TheoryData<string, string[]> Splits => new()
    {
        { "--effort high", ["--effort", "high"] },
        { "--append-system-prompt \"be brief\"", ["--append-system-prompt", "be brief"] },
        { "  --a\tb\r\n--c\n  d  ", ["--a", "b", "--c", "d"] },
        { "--a=\"b c\"", ["--a=b c"] },
        { "'it'\"'\"'s'", ["it's"] },
        { "a \"\" b", ["a", "", "b"] },
        { "''", [""] },
        { "'a \"b\" \\c $HOME'", ["a \"b\" \\c $HOME"] },
        { "\"say \\\"hi\\\" x\\\\y\"", ["say \"hi\" x\\y"] },
        { "a\\ b \\'c\\\" \\\\", ["a b", "'c\"", "\\"] },
        { "\"two\nlines\"", ["two\nlines"] },
        { "$(x) `x` $HOME * ; | & > --x='$HOME'", ["$(x)", "`x`", "$HOME", "*", ";", "|", "&", ">", "--x=$HOME"] },
    };

    [Theory]
    [MemberData(nameof(Splits))]
    public void Tokenize_splits_like_a_shell_command_line_without_expanding_anything(string raw, string[] expected)
    {
        var result = ExtraCliArgs.Tokenize(raw);

        Assert.Null(result.Error);
        Assert.Equal(expected, result.Tokens);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \r\n\t ")]
    public void Tokenize_of_nothing_is_no_tokens_and_no_error(string? raw)
    {
        var result = ExtraCliArgs.Tokenize(raw);

        Assert.Null(result.Error);
        Assert.Empty(result.Tokens);
    }

    [Theory]
    [InlineData("--effort \"high")]
    [InlineData("--effort 'high")]
    [InlineData("--effort \"it's\" 'x")]
    [InlineData("--effort high\\")]
    public void Tokenize_rejects_an_unbalanced_quote_or_a_trailing_backslash(string raw)
    {
        Assert.False(string.IsNullOrWhiteSpace(ExtraCliArgs.Tokenize(raw).Error));
    }

    public static TheoryData<string[]> Displayed => new()
    {
        new[] { "--effort", "high" },
        new[] { "--append-system-prompt", "be brief" },
        new[] { "" , "x" },
        new[] { "a\"b", "c\\d", "it's", "tab\there", "line\nbreak", "$(x);|&>", "--a=b c" },
    };

    [Theory]
    [MemberData(nameof(Displayed))]
    public void Format_shows_tokens_so_that_splitting_the_display_gives_them_back(string[] tokens)
    {
        var shown = ExtraCliArgs.Format(tokens);

        var again = ExtraCliArgs.Tokenize(shown);
        Assert.Null(again.Error);
        Assert.Equal(tokens, again.Tokens);
    }

    [Fact]
    public void Format_shows_plain_flags_as_typed()
    {
        Assert.Equal("--effort high", ExtraCliArgs.Format(["--effort", "high"]));
    }

    [Theory]
    [InlineData("claude-code", "--model x")]
    [InlineData("claude-code", "--effort high --model=opus")]
    [InlineData("copilot", "--model x")]
    [InlineData("copilot", "--model=x")]
    [InlineData("pi", "--model x")]
    [InlineData("pi", "--model=x")]
    [InlineData("opencode", "--model x")]
    [InlineData("opencode", "--model=x")]
    [InlineData("opencode", "-m x")]
    [InlineData("opencode", "-m=x")]
    public void Validate_rejects_any_model_flag_and_points_to_the_model_field(string type, string raw)
    {
        var error = ExtraCliArgs.Validate(type, raw);

        Assert.NotNull(error);
        Assert.Contains("--model", error);
        Assert.Contains("Model field", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("claude-code", "--permission-mode plan", "--permission-mode")]
    [InlineData("claude-code", "--permission-mode=plan", "--permission-mode")]
    [InlineData("claude-code", "-p", "-p")]
    [InlineData("claude-code", "--resume=abc", "--resume")]
    [InlineData("Claude-Code", "--print", "--print")]
    [InlineData("copilot", "--additional-mcp-config x", "--additional-mcp-config")]
    [InlineData("copilot", "--additional-mcp-config=x", "--additional-mcp-config")]
    [InlineData("opencode", "--session s", "--session")]
    [InlineData("opencode", "--session=s", "--session")]
    [InlineData("opencode", "-s s", "-s")]
    [InlineData("pi", "--session-dir /x", "--session-dir")]
    [InlineData("pi", "--session-dir=/x", "--session-dir")]
    [InlineData("pi", "-e ext.ts", "-e")]
    public void Validate_rejects_a_flag_ild_already_sets_for_that_adapter_and_names_it(string type, string raw, string flag)
    {
        var error = ExtraCliArgs.Validate(type, raw);

        Assert.NotNull(error);
        Assert.Contains(flag, error);
    }

    [Theory]
    [InlineData("claude-code")]
    [InlineData("copilot")]
    [InlineData("opencode")]
    [InlineData("pi")]
    public void Validate_rejects_a_bare_end_of_options_separator(string type)
    {
        var error = ExtraCliArgs.Validate(type, "--effort high -- extra");

        Assert.NotNull(error);
        Assert.Contains("--", error);
    }

    [Theory]
    [InlineData("claude-code", "--effort \"high")]
    [InlineData("pi", "--thinking high\\")]
    public void Validate_rejects_a_value_that_does_not_split(string type, string raw)
    {
        Assert.False(string.IsNullOrWhiteSpace(ExtraCliArgs.Validate(type, raw)));
    }

    [Theory]
    [InlineData("claude-code", null)]
    [InlineData("claude-code", "   ")]
    [InlineData("claude-code", "--effort high")]
    [InlineData("claude-code", "--fallback-model x")]
    [InlineData("claude-code", "--append-system-prompt \"be brief; $(x) | y\"")]
    [InlineData("copilot", "--effort high")]
    [InlineData("opencode", "--variant high")]
    [InlineData("pi", "--models a,b")]
    [InlineData("pi", "--thinking high")]
    public void Validate_accepts_flags_ild_does_not_own(string type, string? raw)
    {
        Assert.Null(ExtraCliArgs.Validate(type, raw));
    }
}
