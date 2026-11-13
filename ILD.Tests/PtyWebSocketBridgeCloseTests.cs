using System.Text;
using ILD.Api.Services;

namespace ILD.Tests;

/// <summary>A close frame's reason holds at most 123 bytes of UTF-8; a longer one makes the close throw and the socket drop.</summary>
public sealed class PtyWebSocketBridgeCloseTests
{
    [Fact]
    public void A_short_reason_is_kept_as_it_is()
        => Assert.Equal("The run has no worktree any more: it was cleaned up.", PtyWebSocketBridge.CloseDescription("The run has no worktree any more: it was cleaned up."));

    [Theory]
    [InlineData("x")]
    [InlineData("é")]
    [InlineData("😀")]
    public void A_long_reason_is_cut_to_fit_the_close_frame_at_a_character_boundary(string unit)
    {
        var text = string.Concat(Enumerable.Repeat(unit, 200));
        var cut = PtyWebSocketBridge.CloseDescription(text);

        Assert.True(Encoding.UTF8.GetByteCount(cut) <= 123);
        Assert.EndsWith("…", cut);
        Assert.False(char.IsHighSurrogate(cut[^2]));
    }
}
