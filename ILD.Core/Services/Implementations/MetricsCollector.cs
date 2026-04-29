using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;
using System.IO;
using System.Text;

namespace ILD.Core.Services.Implementations;

public class MetricsCollector : IMetricsCollector
{
    private readonly AppDbContext _db;

    public MetricsCollector(AppDbContext db)
    {
        _db = db;
    }

    public string Snapshot()
    {
        var sb = new StringBuilder();

        sb.Append(LoopRunsTotalMetrics());
        sb.Append(NodeExecutionDurationMetrics());
        sb.Append(LlmApiLatencyMetrics());
        sb.Append(LlmTokensMetrics());
        sb.Append(WorkItemsTotalMetrics());
        sb.Append(DbConnectionHealthyMetrics());
        sb.Append(DiskSpaceBytesMetrics());

        return sb.ToString();
    }

    private string LoopRunsTotalMetrics()
    {
        var completed = _db.LoopRuns.Count(r => r.Status == LoopRunStatus.Completed);
        var failed = _db.LoopRuns.Count(r => r.Status == LoopRunStatus.Failed);
        var cancelled = _db.LoopRuns.Count(r => r.Status == LoopRunStatus.Cancelled);

        return $"# HELP ild_loop_runs_total Total loop runs by status\n" +
               $"# TYPE ild_loop_runs_total counter\n" +
               $"ild_loop_runs_total{{status=\"completed\"}} {completed}\n" +
               $"ild_loop_runs_total{{status=\"failed\"}} {failed}\n" +
               $"ild_loop_runs_total{{status=\"cancelled\"}} {cancelled}\n";
    }

    private string NodeExecutionDurationMetrics()
    {
        var nodes = _db.LoopRunNodes
            .Where(n => n.StartedAt.HasValue && n.CompletedAt.HasValue)
            .Select(n => new { n.LoopNode.NodeType, Duration = (n.CompletedAt!.Value - n.StartedAt!.Value).TotalSeconds })
            .ToList()
            .GroupBy(n => n.NodeType)
            .ToList();

        var lines = new List<string>();
        lines.Add("# HELP ild_node_execution_duration_seconds Node execution duration");
        lines.Add("# TYPE ild_node_execution_duration_seconds histogram");

        foreach (var group in nodes)
        {
            var maxDuration = group.Max(n => n.Duration);
            var count = group.Count();
            lines.Add($"ild_node_execution_duration_seconds{{node_type=\"{group.Key}\"}}_max {maxDuration:F3}");
            lines.Add($"ild_node_execution_duration_seconds{{node_type=\"{group.Key}\"}}_count {count}");
        }

        return string.Join("\n", lines) + "\n";
    }

    private string LlmApiLatencyMetrics()
    {
        return "# HELP ild_llm_api_latency_seconds LLM API call latency\n" +
               "# TYPE ild_llm_api_latency_seconds histogram\n" +
               "ild_llm_api_latency_seconds_sum 0\n" +
               "ild_llm_api_latency_seconds_count 0\n";
    }

    private string LlmTokensMetrics()
    {
        return "# HELP ild_llm_tokens_total Total LLM tokens by type\n" +
               "# TYPE ild_llm_tokens_total counter\n" +
               "ild_llm_tokens_total{type=\"prompt\"} 0\n" +
               "ild_llm_tokens_total{type=\"completion\"} 0\n";
    }

    private string WorkItemsTotalMetrics()
    {
        var statuses = Enum.GetValues<WorkItemStatus>().ToList();
        var lines = new List<string>();
        lines.Add("# HELP ild_workitems_total Total work items by status");
        lines.Add("# TYPE ild_workitems_total gauge");

        foreach (var status in statuses)
        {
            var count = _db.WorkItems.Count(w => w.Status == status);
            lines.Add($"ild_workitems_total{{status=\"{status}\"}} {count}");
        }

        return string.Join("\n", lines) + "\n";
    }

    private string DbConnectionHealthyMetrics()
    {
        try
        {
            _db.Database.CanConnect();
            return "# HELP ild_db_connection_healthy Database connectivity gauge\n" +
                   "# TYPE ild_db_connection_healthy gauge\n" +
                   "ild_db_connection_healthy 1\n";
        }
        catch
        {
            return "# HELP ild_db_connection_healthy Database connectivity gauge\n" +
                   "# TYPE ild_db_connection_healthy gauge\n" +
                   "ild_db_connection_healthy 0\n";
        }
    }

    private string DiskSpaceBytesMetrics()
    {
        try
        {
            var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            if (Directory.Exists(dataDir))
            {
                var drive = new DriveInfo(Path.GetPathRoot(dataDir)!);
                return "# HELP ild_disk_space_bytes Available disk space in bytes\n" +
                       "# TYPE ild_disk_space_bytes gauge\n" +
                       $"ild_disk_space_bytes {drive.AvailableFreeSpace}\n";
            }
        }
        catch
        {
            // fall through
        }

        return "# HELP ild_disk_space_bytes Available disk space in bytes\n" +
               "# TYPE ild_disk_space_bytes gauge\n" +
               "ild_disk_space_bytes 0\n";
    }
}
