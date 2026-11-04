using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;

namespace ILD.Tests;

public class ManagedAgentUsageTests
{
    private readonly IManagedAgentUsage _usage = new ManagedAgentUsage();

    [Fact]
    public void A_lease_marks_the_agent_in_use_until_it_is_disposed()
    {
        var lease = _usage.Acquire("opencode");

        Assert.NotNull(lease);
        Assert.True(_usage.IsBusy("opencode"));
        Assert.False(_usage.IsBusy("pi"));

        lease.Dispose();
        Assert.False(_usage.IsBusy("opencode"));
    }

    [Fact]
    public void Leases_count_so_one_release_does_not_free_the_agent_and_dispose_is_idempotent()
    {
        var first = _usage.Acquire("pi")!;
        var second = _usage.Acquire("pi")!;

        first.Dispose();
        first.Dispose();
        Assert.True(_usage.IsBusy("pi"));

        second.Dispose();
        Assert.False(_usage.IsBusy("pi"));
    }

    [Fact]
    public void The_provider_type_is_matched_case_insensitively_and_unmanaged_types_take_no_lease()
    {
        using (_usage.Acquire("Claude-Code"))
            Assert.True(_usage.IsBusy("claude-code"));

        Assert.Null(_usage.Acquire("custom"));
        Assert.Null(_usage.Acquire(null));
        Assert.Null(_usage.Acquire(""));
    }

    [Fact]
    public void An_idle_agent_runs_the_action_and_an_agent_in_use_does_not()
    {
        var swaps = 0;

        Assert.True(_usage.TryRunWhenIdle("codex", () => swaps++));
        Assert.Equal(1, swaps);

        using (_usage.Acquire("codex"))
        {
            Assert.False(_usage.TryRunWhenIdle("codex", () => swaps++));
            Assert.Equal(1, swaps);
            Assert.True(_usage.TryRunWhenIdle("copilot", () => swaps++));
        }

        Assert.Equal(2, swaps);
        Assert.True(_usage.TryRunWhenIdle("codex", () => swaps++));
        Assert.Equal(3, swaps);
    }

    [Fact]
    public void No_lease_can_be_taken_while_the_idle_action_runs()
    {
        IDisposable? taken = null;
        var acquiring = new Thread(() => taken = _usage.Acquire("codex"));

        Assert.True(_usage.TryRunWhenIdle("codex", () =>
        {
            acquiring.Start();
            // The other thread can only be waiting for the lock this action holds.
            Assert.True(SpinWait.SpinUntil(() => acquiring.ThreadState.HasFlag(ThreadState.WaitSleepJoin), TimeSpan.FromSeconds(10)));
            Assert.False(_usage.IsBusy("codex"));
            Assert.Null(taken);
        }));

        acquiring.Join();
        Assert.NotNull(taken);
        Assert.True(_usage.IsBusy("codex"));
        taken.Dispose();
    }
}
