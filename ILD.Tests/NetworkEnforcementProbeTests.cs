using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.RunHosts;

namespace ILD.Tests;

public sealed class NetworkEnforcementProbeTests
{
    private static NetworkEnforcementProbe Probe(bool ildReaches = true, TimeProvider? clock = null, string? target = null)
        => new(name => name == NetworkEnforcementProbe.TargetSetting ? target : null, clock, (_, _, _) => Task.FromResult(ildReaches));

    private static NetworkEnforcementStatus Status(NetworkEnforcementProbe probe)
        => NetworkEnforcementStatus.FromEnvironment(EgressProxyOptions.Parse("3128")) with { Probe = probe };

    [Fact]
    public async Task A_probe_that_cannot_get_out_makes_the_rules_enforced()
    {
        var probe = Probe();
        Assert.Equal("unverified", Status(probe).EnforcementFor(RunHostKind.Container));

        await probe.ProbeAsync((_, _, _) => Task.FromResult<bool?>(false), TestContext.Current.CancellationToken);

        Assert.Equal("enforced", Status(probe).EnforcementFor(RunHostKind.Container));
    }

    [Fact]
    public async Task A_probe_that_connects_makes_the_rules_advisory_and_says_so()
    {
        var probe = Probe();

        await probe.ProbeAsync((host, port, _) => Task.FromResult<bool?>(host == "1.1.1.1" && port == 443), TestContext.Current.CancellationToken);

        Assert.Equal("advisory", Status(probe).EnforcementFor(RunHostKind.Kubernetes));
        Assert.Contains("connected out directly", Status(probe).ReasonFor(RunHostKind.Kubernetes));
    }

    [Fact]
    public async Task Nothing_is_claimed_when_ild_itself_reaches_nothing_outside_or_the_run_cannot_tell()
    {
        var offline = Probe(ildReaches: false);
        var calls = 0;
        await offline.ProbeAsync((_, _, _) => { calls++; return Task.FromResult<bool?>(false); }, TestContext.Current.CancellationToken);
        var unsure = Probe();
        await unsure.ProbeAsync((_, _, _) => Task.FromResult<bool?>(null), TestContext.Current.CancellationToken);
        var failing = Probe();
        await failing.ProbeAsync((_, _, _) => throw new InvalidOperationException("no exec"), TestContext.Current.CancellationToken);

        Assert.Equal(0, calls);
        foreach (var probe in new[] { offline, unsure, failing })
            Assert.Equal("unverified", Status(probe).EnforcementFor(RunHostKind.Container));
    }

    [Fact]
    public async Task The_target_is_configurable_and_a_verdict_is_reprobed_only_after_the_interval()
    {
        var clock = new StepClock();
        var probe = Probe(clock: clock, target: "probe.example.test:8443");
        var targets = new List<string>();
        Task<bool?> Observe(string host, int port, CancellationToken _) { targets.Add($"{host}:{port}"); return Task.FromResult<bool?>(false); }

        await probe.ProbeAsync(Observe, TestContext.Current.CancellationToken);
        await probe.ProbeAsync(Observe, TestContext.Current.CancellationToken);
        clock.Advance(NetworkEnforcementProbe.Interval + TimeSpan.FromSeconds(1));
        await probe.ProbeAsync(Observe, TestContext.Current.CancellationToken);

        Assert.Equal(["probe.example.test:8443", "probe.example.test:8443"], targets);
    }

    private sealed class StepClock : TimeProvider
    {
        private long _timestamp = 1_000_000;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan by) => _timestamp += (long)(by.TotalSeconds * TimestampFrequency);
    }
}
