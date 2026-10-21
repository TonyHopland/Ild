using System.Text;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;

namespace ILD.Tests;

/// <summary>
/// Preview pages: about a megabyte of lines at most, a longer single line cut off at that, and
/// pages that walk back from the end of a session to its start.
/// </summary>
public sealed class AdapterSessionSnapshotPageTests : IDisposable
{
    private const int Budget = 1024 * 1024;
    private const int LineBytes = 600 * 1024;

    private readonly TestDb _db = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _db.Dispose();

    private IAdapterSessionSnapshotStore Store() => new AdapterSessionSnapshotStore(_db.Fresh());

    [Fact]
    public async Task A_page_of_long_lines_stops_before_a_megabyte_and_says_where_the_next_starts()
    {
        var key = await KeyAsync();
        string[] lines = [new('a', LineBytes), new('b', LineBytes), new('c', LineBytes)];
        await SessionSnapshotTestSupport.SeedAsync(Store(), key, SessionSnapshotFormats.ClaudeJsonl, string.Concat(lines.Select(l => l + "\n")), Ct);

        var range = await Store().ReadRangeAsync(key, 0, 200, Ct);
        var tail = await Store().ReadTailAsync(key, 200, null, Ct);

        Assert.Equal(lines[..1], range.Events);
        Assert.Equal(LineBytes + 1, range.NextOffset);
        Assert.False(range.Truncated);
        Assert.Equal(lines[2..], tail.Events);
        Assert.Equal(2L * (LineBytes + 1), tail.FromOffset);
        Assert.Equal(2L * (LineBytes + 1), tail.PreviousOffset);
    }

    [Fact]
    public async Task A_single_line_session_is_served_cut_off_at_the_page_budget()
    {
        var key = await KeyAsync();
        var line = "{\"messages\":\"" + string.Concat(Enumerable.Repeat("é", 2 * Budget)) + "\"}";
        await SessionSnapshotTestSupport.SeedAsync(Store(), key, SessionSnapshotFormats.OpenCodeJson, line, Ct);

        foreach (var page in new[] { await Store().ReadRangeAsync(key, 0, 200, Ct), await Store().ReadTailAsync(key, 200, null, Ct) })
        {
            var only = Assert.Single(page.Events);
            Assert.True(page.Truncated);
            Assert.True(Encoding.UTF8.GetByteCount(only) <= Budget);
            Assert.StartsWith(only, line, StringComparison.Ordinal);
            Assert.True(only.Length > Budget / 3);
            Assert.Equal(0, page.FromOffset);
            Assert.Null(page.NextOffset);
            Assert.Null(page.PreviousOffset);
        }
    }

    [Fact]
    public async Task A_line_cut_off_at_the_budget_is_the_only_event_on_its_page()
    {
        var key = await KeyAsync();
        var line = new string('x', 2 * Budget);
        await SessionSnapshotTestSupport.SeedAsync(Store(), key, SessionSnapshotFormats.ClaudeJsonl, line + "\n\n\n{\"n\":1}\n", Ct);

        var page = await Store().ReadRangeAsync(key, 0, 200, Ct);

        Assert.Single(page.Events);
        Assert.True(page.Truncated);
        Assert.Equal(line.Length + 1, page.NextOffset);
    }

    [Fact]
    public async Task A_tail_page_never_returns_a_line_whose_start_it_did_not_read()
    {
        var key = await KeyAsync();
        const string cutOff = "{\"head\":";
        const string continued = "\"rest\"}";
        var last = "{\"n\":\"" + new string('z', Budget - continued.Length - 10) + "\"}";
        await AppendAsync(key, cutOff, 0);
        await AppendAsync(key, continued + "\n" + last + "\n", cutOff.Length);

        var page = await Store().ReadTailAsync(key, 200, null, Ct);

        Assert.Equal(new[] { last }, page.Events);
        Assert.Equal(cutOff.Length + continued.Length + 1, page.FromOffset);
    }

    [Fact]
    public async Task Tail_pages_walk_back_from_the_end_to_the_start_of_a_session()
    {
        var key = await KeyAsync();
        var lines = Enumerable.Range(0, 1000).Select(n => $"{{\"n\":{n}}}").ToArray();
        await SessionSnapshotTestSupport.SeedAsync(Store(), key, SessionSnapshotFormats.ClaudeJsonl, string.Concat(lines.Select(l => l + "\n")), Ct);

        var pages = new List<SessionSnapshotPage> { await Store().ReadTailAsync(key, 300, null, Ct) };
        while (pages[^1].PreviousOffset is { } before)
            pages.Add(await Store().ReadTailAsync(key, 300, before, Ct));

        Assert.Equal(new[] { 300, 300, 300, 100 }, pages.Select(p => p.Events.Count));
        Assert.Equal(lines, pages.AsEnumerable().Reverse().SelectMany(p => p.Events));
        Assert.Null(pages[0].NextOffset);
        Assert.All(pages.Skip(1), p => Assert.NotNull(p.NextOffset));
        Assert.Equal(0, pages[^1].FromOffset);
    }

    private async Task<SessionSnapshotKey> KeyAsync() => SessionSnapshotKey.ForRun(await TestRuns.SeedAsync(_db), "ClaudeCode", "sess-1");

    private async Task AppendAsync(SessionSnapshotKey key, string text, long expected)
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(text));
        Assert.Equal(SessionSnapshotAppendResult.Appended, await Store().AppendAsync(key, SessionSnapshotFormats.ClaudeJsonl, content, expected, "tail", Ct));
    }
}
