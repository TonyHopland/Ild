using System.Text;
using ILD.Core.Services.Implementations;

namespace ILD.Tests;

/// <summary>Every cut a title makes lands between whole characters.</summary>
public class ChatTitlesUnicodeTests
{
    private const string Emoji = "😀";

    /// <summary>No lone surrogate anywhere: the text round-trips through UTF-8 unchanged.</summary>
    private static void AssertWellFormed(string text)
    {
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        Assert.Equal(text, strict.GetString(strict.GetBytes(text)));
    }

    [Fact]
    public void A_fallback_cut_landing_inside_an_emoji_leaves_the_whole_emoji_out()
    {
        var title = ChatTitles.Fallback(new string('a', 59) + Emoji + " and more words after it");

        AssertWellFormed(title);
        Assert.Equal(new string('a', 59) + "…", title);
    }

    [Fact]
    public void A_fallback_keeps_an_emoji_that_fits_whole()
    {
        var title = ChatTitles.Fallback(new string('a', 58) + Emoji + "bbbbbbbbbb");

        Assert.Equal(new string('a', 58) + Emoji + "…", title);
    }

    [Fact]
    public void A_hard_cut_generated_title_leaves_a_split_emoji_out()
    {
        var title = ChatTitles.CleanGenerated(new string('x', 59) + Emoji + "yyyyyyyyyy");

        Assert.NotNull(title);
        AssertWellFormed(title!);
        Assert.Equal(new string('x', 59), title);
    }

    [Fact]
    public void The_prompt_never_carries_half_an_emoji_from_either_capped_text()
    {
        var prompt = ChatTitles.BuildPrompt(
            new string('u', ChatTitles.MaxFirstMessageLength - 1) + Emoji + "tail",
            new string('r', ChatTitles.MaxFirstReplyLength - 1) + Emoji + "tail",
            workItemTitle: null);

        AssertWellFormed(prompt);
        Assert.Contains(new string('u', ChatTitles.MaxFirstMessageLength - 1), prompt);
        Assert.DoesNotContain("tail", prompt);
    }
}
