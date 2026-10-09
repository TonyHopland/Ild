using ILD.Data.Entities;

namespace ILD.Tests;

/// <summary>A firing's reason is cut to fit its column without breaking the text.</summary>
public sealed class ChatScheduleReasonTests
{
    [Fact]
    public void A_reason_cut_at_an_emoji_keeps_the_emoji_whole_or_leaves_it_out()
    {
        const string emoji = "\U0001F600";
        // The cut falls right after the emoji's high surrogate.
        var reason = new string('a', ChatScheduleFiring.MaxReasonLength - 2) + emoji + "tail";

        var clipped = ChatScheduleFiring.ClipReason(reason);

        Assert.True(clipped.Length <= ChatScheduleFiring.MaxReasonLength);
        Assert.EndsWith("a…", clipped);
        Assert.DoesNotContain(clipped, char.IsSurrogate);
    }

    [Fact]
    public void A_reason_that_fits_is_kept_as_it_is()
    {
        var reason = new string('a', ChatScheduleFiring.MaxReasonLength - 2) + "\U0001F600";
        Assert.Equal(reason, ChatScheduleFiring.ClipReason(reason));
    }
}
