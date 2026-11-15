using System.Text.Json.Nodes;

namespace ILD.Core.Services.RunHosts.Container;

/// <summary>Reads the answer of the engine's stats call, with the container's own settings for its limits, into <see cref="ContainerStats"/>.</summary>
internal static class ContainerStatsParser
{
    public static ContainerStats Parse(JsonNode? stats, JsonNode? inspected)
    {
        var host = inspected?["HostConfig"];
        var memory = stats?["memory_stats"];
        return new ContainerStats(
            Running: true,
            CpuCores: CpuCores(stats),
            CpuLimitCores: CpuLimit(host),
            MemoryBytes: MemoryUsage(memory),
            MemoryLimitBytes: MemoryLimit(memory, host),
            NetworkRxBytes: NetworkTotal(stats, "rx_bytes"),
            NetworkTxBytes: NetworkTotal(stats, "tx_bytes"),
            DiskReadBytes: DiskTotal(stats, "read"),
            DiskWriteBytes: DiskTotal(stats, "write"));
    }

    private static double? CpuCores(JsonNode? stats)
    {
        var current = stats?["cpu_stats"];
        var previous = stats?["precpu_stats"];
        if (Long(current?["cpu_usage"]?["total_usage"]) is not { } usage || Long(previous?["cpu_usage"]?["total_usage"]) is not { } before)
            return null;
        // A reading without an earlier one (the first sample of a stream) has no interval to divide by.
        if (Long(current?["system_cpu_usage"]) is not { } system || Long(previous?["system_cpu_usage"]) is not { } systemBefore)
            return null;
        var systemDelta = system - systemBefore;
        var usageDelta = usage - before;
        if (systemDelta <= 0 || usageDelta < 0) return null;
        var online = Long(current?["online_cpus"]) is > 0 and var count
            ? count
            : (current?["cpu_usage"]?["percpu_usage"] as JsonArray)?.Count is > 0 and var perCpu ? perCpu : 1;
        return (double)usageDelta / systemDelta * online;
    }

    private static double? CpuLimit(JsonNode? host)
    {
        if (Long(host?["NanoCpus"]) is > 0 and var nano) return nano / 1e9;
        if (Long(host?["CpuQuota"]) is > 0 and var quota && Long(host?["CpuPeriod"]) is > 0 and var period) return (double)quota / period;
        return null;
    }

    // Docker's own `stats` subtracts the page cache: cgroup v1 reports it as cache, v2 as inactive_file.
    private static long? MemoryUsage(JsonNode? memory)
    {
        if (Long(memory?["usage"]) is not { } usage) return null;
        var detail = memory?["stats"];
        var cache = Long(detail?["inactive_file"]) ?? Long(detail?["total_inactive_file"]) ?? Long(detail?["cache"]) ?? 0;
        return cache > 0 && cache <= usage ? usage - cache : usage;
    }

    // A container with no memory limit reports the host's RAM as its limit, which a bar against it would show as nearly empty and meaningless; the container's own setting says whether there is one.
    private static long? MemoryLimit(JsonNode? memory, JsonNode? host)
    {
        if (Long(host?["Memory"]) is > 0 and var configured) return configured;
        if (host is not null) return null;
        return Long(memory?["limit"]) is > 0 and var limit ? limit : null;
    }

    private static long? NetworkTotal(JsonNode? stats, string direction)
    {
        if (stats?["networks"] is not JsonObject networks || networks.Count == 0) return null;
        long total = 0;
        var any = false;
        foreach (var (_, network) in networks)
            if (Long(network?[direction]) is { } bytes) { total += bytes; any = true; }
        return any ? total : null;
    }

    private static long? DiskTotal(JsonNode? stats, string op)
    {
        // cgroup v1 names the list io_service_bytes_recursive, v2 reports the same one; a runtime without it sends null.
        if (stats?["blkio_stats"]?["io_service_bytes_recursive"] is not JsonArray entries) return null;
        long total = 0;
        var any = false;
        foreach (var entry in entries)
            if (string.Equals(entry?["op"]?.GetValue<string>(), op, StringComparison.OrdinalIgnoreCase) && Long(entry?["value"]) is { } value)
            {
                total += value;
                any = true;
            }
        return any ? total : null;
    }

    private static long? Long(JsonNode? node)
    {
        try { return node?.GetValue<long>(); }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException) { return null; }
    }
}
