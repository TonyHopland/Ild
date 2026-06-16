using System.Collections.Concurrent;
using ILD.Core.Services.Interfaces;
using ILD.Data.Enums;

namespace ILD.Tests;

/// <summary>
/// Covers the live-output transition markers: when a node starts emitting to
/// the live stream the engine prefixes its first chunk with an
/// <c>[Ild: &lt;label&gt;]</c> header, styled like the adapters' <c>[tool: ...]</c>
/// markers, so the hand-off between nodes is visible in the live view.
/// </summary>
public class LoopEngineProgressTransitionTests
{
    [Fact]
    public async Task First_live_chunk_of_a_node_is_prefixed_with_an_Ild_transition_marker()
    {
        var notifier = new RecordingRunNotifier();
        using var h = new LoopEngineHarness(notifier);
        h.AddNode("s", NodeType.Start, label: "Bootstrap");
        h.AddNode("c", NodeType.Cleanup, label: "Wrap up");
        h.AddEdge("s", "c", EdgeType.OnSuccess);

        h.Registry.Register(new ProgressEmittingExecutor(NodeType.Start,
            new[] { "cloning repo\n", "ready\n" },
            new NodeOutcome.NodeStarting("start"),
            new NodeOutcome.Success(EdgeType.OnSuccess, "ok")));
        h.Registry.Register(new ProgressEmittingExecutor(NodeType.Cleanup,
            new[] { "tidying\n" },
            new NodeOutcome.NodeStarting("cleanup"),
            new NodeOutcome.Terminal("done")));

        h.SeedRun("s");
        await h.RunAsync();

        var lines = notifier.ProgressLines;
        Assert.Equal(
            new[] { "\n[Ild: Bootstrap]\n", "cloning repo\n", "ready\n", "\n[Ild: Wrap up]\n", "tidying\n" },
            lines);
    }

    [Fact]
    public async Task Transition_marker_is_emitted_only_once_per_node_no_matter_how_many_chunks()
    {
        var notifier = new RecordingRunNotifier();
        using var h = new LoopEngineHarness(notifier);
        h.AddNode("s", NodeType.Start, label: "Bootstrap");
        h.AddNode("c", NodeType.Cleanup, label: "Wrap up");
        h.AddEdge("s", "c", EdgeType.OnSuccess);

        h.Registry.Register(new ProgressEmittingExecutor(NodeType.Start,
            new[] { "a\n", "b\n", "c\n" },
            new NodeOutcome.NodeStarting("start"),
            new NodeOutcome.Success(EdgeType.OnSuccess, "ok")));
        h.Registry.Register(new ProgressEmittingExecutor(NodeType.Cleanup,
            System.Array.Empty<string>(),
            new NodeOutcome.NodeStarting("cleanup"),
            new NodeOutcome.Terminal("done")));

        h.SeedRun("s");
        await h.RunAsync();

        // Exactly one marker for the node that streamed, and none for the
        // Cleanup node that produced no live output.
        Assert.Single(notifier.ProgressLines, l => l == "\n[Ild: Bootstrap]\n");
        Assert.DoesNotContain("\n[Ild: Wrap up]\n", notifier.ProgressLines);
    }

    [Fact]
    public async Task Transition_marker_falls_back_to_the_node_type_when_the_label_is_blank()
    {
        var notifier = new RecordingRunNotifier();
        using var h = new LoopEngineHarness(notifier);
        var start = h.AddNode("s", NodeType.Start);
        start.Label = string.Empty;
        h.Db.Context.SaveChanges();
        h.AddNode("c", NodeType.Cleanup, label: "Wrap up");
        h.AddEdge("s", "c", EdgeType.OnSuccess);

        h.Registry.Register(new ProgressEmittingExecutor(NodeType.Start,
            new[] { "working\n" },
            new NodeOutcome.NodeStarting("start"),
            new NodeOutcome.Success(EdgeType.OnSuccess, "ok")));
        h.Registry.Register(new ProgressEmittingExecutor(NodeType.Cleanup,
            System.Array.Empty<string>(),
            new NodeOutcome.NodeStarting("cleanup"),
            new NodeOutcome.Terminal("done")));

        h.SeedRun("s");
        await h.RunAsync();

        Assert.Contains("\n[Ild: Start]\n", notifier.ProgressLines);
    }
}

/// <summary>Captures every <see cref="NodeProgressAsync"/> line in order so
/// tests can assert on the live-output stream.</summary>
internal sealed class RecordingRunNotifier : IRunNotifier
{
    private readonly ConcurrentQueue<string> _lines = new();
    public IReadOnlyList<string> ProgressLines => _lines.ToArray();

    public Task NodeStateChangedAsync(Guid runId, Guid nodeId, LoopRunNodeStatus oldStatus, LoopRunNodeStatus newStatus) => Task.CompletedTask;
    public Task EventLoggedAsync(Guid runId, string message, string eventType, Guid? nodeId, Guid? runNodeId) => Task.CompletedTask;
    public Task RunStateChangedAsync(Guid runId, LoopRunStatus oldStatus, LoopRunStatus newStatus) => Task.CompletedTask;
    public Task PausedAsync(Guid runId) => Task.CompletedTask;
    public Task ResumedAsync(Guid runId) => Task.CompletedTask;
    public Task HaltedAsync(Guid runId) => Task.CompletedTask;

    public Task NodeProgressAsync(Guid runId, Guid nodeId, string line, long seq)
    {
        _lines.Enqueue(line);
        return Task.CompletedTask;
    }
}

/// <summary>Emits a set of live-output chunks through the context's progress
/// callback (after the <see cref="NodeOutcome.NodeStarting"/> outcome) before
/// yielding its terminal outcome, mirroring how real executors stream output.</summary>
internal sealed class ProgressEmittingExecutor : INodeExecutor
{
    private readonly string[] _chunks;
    private readonly NodeOutcome[] _outcomes;
    public NodeType NodeType { get; }

    public ProgressEmittingExecutor(NodeType type, string[] chunks, params NodeOutcome[] outcomes)
    {
        NodeType = type;
        _chunks = chunks;
        _outcomes = outcomes;
    }

    public async IAsyncEnumerable<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
    {
        foreach (var o in _outcomes)
        {
            await Task.Yield();
            yield return o;
            // Stream the live output once the node has been marked Running.
            if (o is NodeOutcome.NodeStarting && ctx.ProgressCallback is not null)
            {
                foreach (var chunk in _chunks)
                    await ctx.ProgressCallback(chunk);
            }
        }
    }
}
