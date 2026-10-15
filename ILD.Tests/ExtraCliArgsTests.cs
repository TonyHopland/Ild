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
    [InlineData("codex", "--model x")]
    [InlineData("codex", "--model=x")]
    [InlineData("codex", "-m x")]
    [InlineData("codex", "-mgpt-5")]
    [InlineData("Codex", "-m=x")]
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
    [InlineData("codex", "--json", "--json")]
    [InlineData("codex", "--skip-git-repo-check", "--skip-git-repo-check")]
    [InlineData("codex", "--dangerously-bypass-approvals-and-sandbox", "--dangerously-bypass-approvals-and-sandbox")]
    [InlineData("codex", "--add-dir /x", "--add-dir")]
    [InlineData("codex", "-C /tmp", "-C")]
    [InlineData("codex", "-C/tmp", "-C")]
    [InlineData("codex", "--cd /tmp", "--cd")]
    [InlineData("codex", "--cd=/tmp", "--cd")]
    [InlineData("codex", "-s read-only", "-s")]
    [InlineData("codex", "-sread-only", "-s")]
    [InlineData("codex", "--sandbox=read-only", "--sandbox")]
    [InlineData("CODEX", "--add-dir=/x", "--add-dir")]
    public void Validate_rejects_a_flag_ild_already_sets_for_that_adapter_and_names_it(string type, string raw, string flag)
    {
        var error = ExtraCliArgs.Validate(type, raw);

        Assert.NotNull(error);
        Assert.Contains(flag, error);
    }

    [Theory]
    [InlineData("codex", "--json", "--json is not allowed, because ILD already sets it for this provider.")]
    [InlineData("codex", "-C /tmp", "-C is not allowed, because it would move Codex out of the worktree.")]
    [InlineData("codex", "--cd=/tmp", "--cd is not allowed, because it would move Codex out of the worktree.")]
    [InlineData("codex", "-s read-only", "-s is not allowed, because it contradicts how ILD runs Codex (sandbox bypassed; the agent user is the sandbox).")]
    [InlineData("codex", "--sandbox read-only", "--sandbox is not allowed, because it contradicts how ILD runs Codex (sandbox bypassed; the agent user is the sandbox).")]
    public void Validate_says_why_a_codex_flag_is_refused(string type, string raw, string message)
    {
        Assert.Equal("Extra CLI arguments: " + message, ExtraCliArgs.Validate(type, raw));
    }

    /// <summary>Each key ILD sets, in each form codex takes a config override, with the flag the message names.</summary>
    public static TheoryData<string, string, string> RefusedCodexConfigOverrides()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (key, setting) in new[]
                 {
                     ("mcp_servers.ild", "MCP servers"),
                     ("sandbox_mode", "sandbox"),
                     ("sandbox_workspace_write.network_access", "sandbox"),
                     ("approval_policy", "approvals"),
                     ("profiles.fast.model", "profiles"),
                 })
        {
            data.Add($"-c {key}=x", $"-c {key}", setting);
            data.Add($"--config {key}=x", $"--config {key}", setting);
            data.Add($"--config={key}=x", $"--config {key}", setting);
            data.Add($"-c{key}=x", $"-c {key}", setting);
            data.Add($"-c=\" {key} =x\"", $"-c {key}", setting);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(RefusedCodexConfigOverrides))]
    public void Validate_refuses_a_codex_config_override_of_what_ild_sets_itself(string raw, string named, string setting)
    {
        Assert.Equal(
            $"Extra CLI arguments: {named} is not allowed, because ILD sets Codex's {setting} itself.",
            ExtraCliArgs.Validate("codex", "--enable web_search " + raw));
    }

    [Theory]
    [InlineData("-c", "-c")]
    [InlineData("--enable web_search --config", "--config")]
    [InlineData("-c --json", "-c")]
    [InlineData("--config -C/tmp", "--config")]
    public void Validate_refuses_a_codex_config_flag_with_no_value(string raw, string flag)
    {
        Assert.Equal(
            $"Extra CLI arguments: {flag} is not allowed without a key=value after it.",
            ExtraCliArgs.Validate("codex", raw));
    }

    [Theory]
    [InlineData("claude-code")]
    [InlineData("copilot")]
    [InlineData("opencode")]
    [InlineData("pi")]
    public void Validate_leaves_config_like_flags_of_other_providers_alone(string type)
    {
        Assert.Null(ExtraCliArgs.Validate(type, "-c mcp_servers.ild=x --config sandbox_mode=read-only"));
    }

    [Theory]
    [InlineData("claude-code")]
    [InlineData("copilot")]
    [InlineData("codex")]
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
    [InlineData("claude-code", "--append-system-prompt \"-p means print\"")]
    [InlineData("opencode", "--variant \"-sharp\"")]
    [InlineData("copilot", "--effort high")]
    [InlineData("codex", "--enable web_search")]
    [InlineData("codex", "-c model_reasoning_effort=high")]
    [InlineData("codex", "-c model_reasoning_effort=\"high\"")]
    [InlineData("codex", "--config model_reasoning_effort=high")]
    [InlineData("codex", "--config=model_verbosity=low")]
    [InlineData("codex", "-cmodel_reasoning_effort=high")]
    [InlineData("codex", "-c=model_reasoning_effort=high")]
    [InlineData("codex", "-c mcp_servers_note=x")]
    [InlineData("opencode", "--variant high")]
    [InlineData("pi", "--models a,b")]
    [InlineData("pi", "--thinking high")]
    public void Validate_accepts_flags_ild_does_not_own(string type, string? raw)
    {
        Assert.Null(ExtraCliArgs.Validate(type, raw));
    }
}
