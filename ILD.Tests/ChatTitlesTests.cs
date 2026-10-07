using ILD.Core.Services.Implementations;

namespace ILD.Tests;

/// <summary>
/// The two title clean-ups: the fallback a chat is named with from its first
/// message, setting or no setting, and what a model's answer is reduced to before
/// it may become the chat's title.
/// </summary>
public class ChatTitlesTests
{
    [Theory]
    [InlineData("## Fix the **login** page", "Fix the login page")]
    [InlineData("Look at [the docs](https://example.com) for `vp`", "Look at the docs for vp")]
    [InlineData("Help me wire up a deploy loop", "Help me wire up a deploy loop")]
    [InlineData("  plain   text\n\twith  spaces ", "plain text with spaces")]
    [InlineData("> quoted line\n> second line", "quoted line second line")]
    [InlineData("### Deploy loop", "Deploy loop")]
    [InlineData("- item one\n* item two\n+ item three", "item one item two item three")]
    [InlineData("1. first step\n2) second step", "first step second step")]
    [InlineData("- [ ] open task\n- [x] done task", "open task done task")]
    [InlineData("![diagram](https://example.com/a.png) shows the flow", "diagram shows the flow")]
    [InlineData("```csharp\nvar x = 1;\n```\nwhy does this fail", "var x = 1; why does this fail")]
    [InlineData("__bold__ and ~~gone~~ and *em* and _under_", "bold and gone and em and under")]
    public void The_fallback_is_the_first_message_without_its_markdown(string firstMessage, string expected)
        => Assert.Equal(expected, ChatTitles.Fallback(firstMessage));

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    [InlineData("## ")]
    [InlineData("```\n```")]
    public void A_first_message_with_nothing_left_after_cleanup_falls_back_to_new_chat(string firstMessage)
        => Assert.Equal("New chat", ChatTitles.Fallback(firstMessage));

    [Fact]
    public void A_long_first_message_keeps_as_much_as_fits_the_stored_name_with_an_ellipsis()
    {
        var title = ChatTitles.Fallback(new string('a', 200));

        Assert.Equal(new string('a', 119) + "…", title);
        Assert.Equal(ChatTitles.MaxFallbackLength, title.Length);
    }

    [Fact]
    public void A_cut_landing_on_a_space_is_trimmed_before_the_ellipsis()
        => Assert.Equal(new string('a', 118) + "…", ChatTitles.Fallback(new string('a', 118) + " bbbbbbbbbb"));

    [Fact]
    public void Exactly_the_stored_name_length_is_kept_whole()
        => Assert.Equal(new string('a', 120), ChatTitles.Fallback(new string('a', 120)));

    [Fact]
    public void The_length_is_measured_after_the_markdown_is_gone()
        => Assert.Equal(new string('c', 118), ChatTitles.Fallback("## " + new string('c', 118)));

    [Theory]
    [InlineData("Login page fix", "Login page fix")]
    [InlineData("\"Chat session sidebar with unread markers\"", "Chat session sidebar with unread markers")]
    [InlineData("\"Login page fix.\"", "Login page fix")]
    [InlineData("“Login page fix”.", "Login page fix")]
    [InlineData("'Deploy loop wiring'", "Deploy loop wiring")]
    [InlineData("‘Fix login’", "Fix login")]
    [InlineData("«Résumé du projet»", "Résumé du projet")]
    [InlineData("`Fix flaky tests`", "Fix flaky tests")]
    [InlineData("Fix \"login\" page", "Fix \"login\" page")]
    [InlineData("# Login page fix...", "Login page fix")]
    [InlineData("\n\n  **Login page fix**  \nThis title sums up the conversation.", "Login page fix")]
    [InlineData("Login   page\tfix", "Login page fix")]
    public void A_generated_title_is_its_first_line_without_markdown_quotes_or_trailing_periods(string output, string expected)
        => Assert.Equal(expected, ChatTitles.CleanGenerated(output));

    [Fact]
    public void A_long_generated_title_is_cut_at_the_last_word_that_fits_with_no_ellipsis()
        => Assert.Equal(
            "Alpha bravo charlie delta echo foxtrot golf hotel india",
            ChatTitles.CleanGenerated("Alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo lima"));

    [Fact]
    public void A_long_generated_title_with_no_word_boundary_is_cut_hard_at_sixty()
        => Assert.Equal(new string('x', 60), ChatTitles.CleanGenerated(new string('x', 70)));

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("\"\"")]
    [InlineData("...")]
    [InlineData("**  **")]
    public void A_generated_title_with_nothing_left_after_cleaning_is_no_title(string output)
        => Assert.Null(ChatTitles.CleanGenerated(output));
}
