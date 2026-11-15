using System.Text.Json.Nodes;
using ILD.Core.Services.RunHosts.Container;

namespace ILD.Tests;

public sealed class ContainerStatsParserTests
{
    private static JsonNode Stats(string json) => JsonNode.Parse(json)!;

    private const string Cgroup2 = """
        {
          "cpu_stats": { "cpu_usage": { "total_usage": 2000000000 }, "system_cpu_usage": 10000000000, "online_cpus": 8 },
          "precpu_stats": { "cpu_usage": { "total_usage": 1000000000 }, "system_cpu_usage": 8000000000 },
          "memory_stats": { "usage": 3000, "limit": 999999, "stats": { "inactive_file": 1000 } },
          "networks": { "eth0": { "rx_bytes": 100, "tx_bytes": 10 }, "eth1": { "rx_bytes": 50, "tx_bytes": 5 } },
          "blkio_stats": { "io_service_bytes_recursive": [ { "op": "read", "value": 4096 }, { "op": "Write", "value": 8192 }, { "op": "write", "value": 1 } ] }
        }
        """;

    private const string Limits = """{ "HostConfig": { "NanoCpus": 4000000000, "Memory": 8000 } }""";

    [Fact]
    public void Reads_cpu_memory_network_and_disk_against_the_containers_own_limits()
    {
        var stats = ContainerStatsParser.Parse(Stats(Cgroup2), Stats(Limits));

        Assert.True(stats.Running);
        Assert.Equal(4.0, stats.CpuCores!.Value, 6); // 1e9 / 2e9 * 8
        Assert.Equal(4.0, stats.CpuLimitCores);
        Assert.Equal(2000, stats.MemoryBytes); // usage less the inactive file cache
        Assert.Equal(8000, stats.MemoryLimitBytes);
        Assert.Equal(150, stats.NetworkRxBytes);
        Assert.Equal(15, stats.NetworkTxBytes);
        Assert.Equal(4096, stats.DiskReadBytes);
        Assert.Equal(8193, stats.DiskWriteBytes);
    }

    [Fact]
    public void Subtracts_the_cache_of_cgroup_v1()
    {
        var v1 = Stats("""{ "memory_stats": { "usage": 5000, "stats": { "cache": 1500 } } }""");

        Assert.Equal(3500, ContainerStatsParser.Parse(v1, Stats(Limits)).MemoryBytes);
    }

    [Fact]
    public void A_container_without_limits_has_none_even_when_the_engine_reports_the_hosts_memory()
    {
        var stats = ContainerStatsParser.Parse(Stats(Cgroup2), Stats("""{ "HostConfig": { "NanoCpus": 0, "Memory": 0 } }"""));

        Assert.Null(stats.CpuLimitCores);
        Assert.Null(stats.MemoryLimitBytes);
        Assert.Equal(2000, stats.MemoryBytes);
    }

    [Fact]
    public void A_cpu_quota_is_a_limit_when_there_are_no_nano_cpus()
    {
        var stats = ContainerStatsParser.Parse(Stats(Cgroup2), Stats("""{ "HostConfig": { "CpuQuota": 150000, "CpuPeriod": 100000 } }"""));

        Assert.Equal(1.5, stats.CpuLimitCores);
    }

    [Fact]
    public void Leaves_out_what_the_engine_does_not_expose()
    {
        var stats = ContainerStatsParser.Parse(Stats("""{ "memory_stats": { "usage": 10 }, "blkio_stats": { "io_service_bytes_recursive": null } }"""), Stats(Limits));

        Assert.Null(stats.CpuCores);
        Assert.Null(stats.NetworkRxBytes);
        Assert.Null(stats.NetworkTxBytes);
        Assert.Null(stats.DiskReadBytes);
        Assert.Null(stats.DiskWriteBytes);
        Assert.Equal(10, stats.MemoryBytes);
    }

    [Fact]
    public void A_zero_system_delta_gives_no_cpu_reading_rather_than_dividing_by_it()
    {
        var first = Stats("""
            { "cpu_stats": { "cpu_usage": { "total_usage": 5 }, "system_cpu_usage": 100, "online_cpus": 2 },
              "precpu_stats": { "cpu_usage": { "total_usage": 0 }, "system_cpu_usage": 100 } }
            """);

        Assert.Null(ContainerStatsParser.Parse(first, Stats(Limits)).CpuCores);
    }

    [Fact]
    public void Counts_the_cpus_from_the_per_cpu_list_when_the_engine_gives_no_online_count()
    {
        var old = Stats("""
            { "cpu_stats": { "cpu_usage": { "total_usage": 200, "percpu_usage": [1, 2] }, "system_cpu_usage": 1000 },
              "precpu_stats": { "cpu_usage": { "total_usage": 100 }, "system_cpu_usage": 800 } }
            """);

        Assert.Equal(1.0, ContainerStatsParser.Parse(old, Stats(Limits)).CpuCores!.Value, 6);
    }
}
