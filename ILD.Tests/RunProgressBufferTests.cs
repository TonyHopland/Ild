using ILD.Core.Services.Implementations;

namespace ILD.Tests;

public class RunProgressBufferTests
{
    [Fact]
    public void Append_assigns_monotonic_sequence_numbers_per_run()
    {
        var buffer = new RunProgressBuffer();
        var run = Guid.NewGuid();

        Assert.Equal(1, buffer.Append(run, "a"));
        Assert.Equal(2, buffer.Append(run, "b"));
        Assert.Equal(3, buffer.Append(run, "c"));
    }

    [Fact]
    public void Sequence_numbers_are_isolated_between_runs()
    {
        var buffer = new RunProgressBuffer();
        var runA = Guid.NewGuid();
        var runB = Guid.NewGuid();

        Assert.Equal(1, buffer.Append(runA, "a1"));
        Assert.Equal(1, buffer.Append(runB, "b1"));
        Assert.Equal(2, buffer.Append(runA, "a2"));

        Assert.Equal("a1a2", buffer.Snapshot(runA).Text);
        Assert.Equal("b1", buffer.Snapshot(runB).Text);
    }

    [Fact]
    public void Snapshot_returns_full_text_and_last_sequence()
    {
        var buffer = new RunProgressBuffer();
        var run = Guid.NewGuid();
        buffer.Append(run, "hello ");
        buffer.Append(run, "world");

        var snapshot = buffer.Snapshot(run);

        Assert.Equal("hello world", snapshot.Text);
        Assert.Equal(2, snapshot.LastSeq);
    }

    [Fact]
    public void Snapshot_of_unknown_run_is_empty()
    {
        var buffer = new RunProgressBuffer();

        var snapshot = buffer.Snapshot(Guid.NewGuid());

        Assert.Equal(string.Empty, snapshot.Text);
        Assert.Equal(0, snapshot.LastSeq);
    }

    [Fact]
    public void Buffer_is_bounded_and_keeps_the_tail_while_advancing_sequence()
    {
        var buffer = new RunProgressBuffer();
        var run = Guid.NewGuid();

        // Fill past the cap, then append a recognisable tail.
        buffer.Append(run, new string('x', RunProgressBuffer.MaxCharsPerRun));
        var lastSeq = buffer.Append(run, "TAIL");

        var snapshot = buffer.Snapshot(run);

        Assert.Equal(RunProgressBuffer.MaxCharsPerRun, snapshot.Text.Length);
        Assert.EndsWith("TAIL", snapshot.Text);
        // Sequence keeps counting even though older text was dropped.
        Assert.Equal(2, lastSeq);
        Assert.Equal(2, snapshot.LastSeq);
    }

    [Fact]
    public void Clear_drops_the_run_buffer()
    {
        var buffer = new RunProgressBuffer();
        var run = Guid.NewGuid();
        buffer.Append(run, "data");

        buffer.Clear(run);

        var snapshot = buffer.Snapshot(run);
        Assert.Equal(string.Empty, snapshot.Text);
        Assert.Equal(0, snapshot.LastSeq);
    }
}
