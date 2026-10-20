using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;

namespace ILD.Tests;

/// <summary>A preview page stops at about a megabyte of lines, however many it was asked for.</summary>
public sealed class AdapterSessionSnapshotPageTests
{
    private const int LineBytes = 600 * 1024;

    [Fact]
    public async Task A_page_of_long_lines_stops_before_a_megabyte_and_says_where_the_next_starts()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = new TestDb();
        var key = SessionSnapshotKey.ForRun(await TestRuns.SeedAsync(db), "ClaudeCode", "sess-1");
        string[] lines = [Line('a'), Line('b'), Line('c')];
        await SessionSnapshotTestSupport.SeedAsync(
            new AdapterSessionSnapshotStore(db.Fresh()), key, SessionSnapshotFormats.ClaudeJsonl, string.Concat(lines.Select(l => l + "\n")), ct);

        var range = await new AdapterSessionSnapshotStore(db.Fresh()).ReadRangeAsync(key, 0, 200, ct);
        var tail = await new AdapterSessionSnapshotStore(db.Fresh()).ReadTailAsync(key, 200, ct);

        Assert.Equal(lines[..1], range.Events);
        Assert.Equal(LineBytes + 1, range.NextOffset);
        Assert.Equal(lines[2..], tail.Events);
        Assert.Equal(2L * (LineBytes + 1), tail.FromOffset);
    }

    private static string Line(char fill) => new(fill, LineBytes);
}
