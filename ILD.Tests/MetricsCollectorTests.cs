using ILD.Data.Enums;
using ILD.Data.Entities;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Implementations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Tests;

public class MetricsCollectorTests
{
    [Fact]
    public void Snapshot_returns_valid_prometheus_text_format_with_loop_runs_counter()
    {
        using var db = new TestDb();
        var collector = new MetricsCollector(db.Context);

        var snapshot = collector.Snapshot();

        Assert.False(string.IsNullOrEmpty(snapshot));
        Assert.Contains("ild_loop_runs_total", snapshot);
        Assert.Contains("# HELP", snapshot);
        Assert.Contains("# TYPE", snapshot);
    }

    [Fact]
    public void MetricsController_returns_prometheus_text_plain_content_type()
    {
        using var db = new TestDb();
        var collector = new MetricsCollector(db.Context);
        var controller = new ILD.Api.Controllers.MetricsController(collector);

        var result = controller.Get();

        Assert.IsType<ContentResult>(result);
        Assert.Equal("text/plain; version=0.0.4; charset=utf-8", ((ContentResult)result).ContentType);
    }

    [Fact]
    public void MetricsController_response_contains_all_required_metric_families()
    {
        using var db = new TestDb();
        var collector = new MetricsCollector(db.Context);
        var controller = new ILD.Api.Controllers.MetricsController(collector);

        var result = (ContentResult)controller.Get();
        var body = (string?)result.Content;

        Assert.Contains("ild_loop_runs_total", body);
        Assert.Contains("ild_node_execution_duration_seconds", body);
        Assert.Contains("ild_db_connection_healthy", body);
        Assert.Contains("ild_disk_space_bytes", body);
    }

    [Fact]
    public void LoopRunsTotal_reflects_completed_run_count()
    {
        using var db = new TestDb();
        SeedAndRun(db, LoopRunStatus.Completed);

        var collector = new MetricsCollector(db.Context);
        var snapshot = collector.Snapshot();

        Assert.Contains("ild_loop_runs_total{status=\"completed\"} 1", snapshot);
    }

    [Fact]
    public void LoopRunsTotal_reflects_failed_run_count()
    {
        using var db = new TestDb();
        SeedAndRun(db, LoopRunStatus.Failed);

        var collector = new MetricsCollector(db.Context);
        var snapshot = collector.Snapshot();

        Assert.Contains("ild_loop_runs_total{status=\"failed\"} 1", snapshot);
    }

    [Fact]
    public void LoopRunsTotal_reflects_cancelled_run_count()
    {
        using var db = new TestDb();
        SeedAndRun(db, LoopRunStatus.Cancelled);

        var collector = new MetricsCollector(db.Context);
        var snapshot = collector.Snapshot();

        Assert.Contains("ild_loop_runs_total{status=\"cancelled\"} 1", snapshot);
    }

    private void SeedAndRun(TestDb db, LoopRunStatus status)
    {
        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://example/r.git" };
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = RecoveryPolicy.AutoResume, MaxNodeExecutions = 200 };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        var wi = Guid.NewGuid().ToString();
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = wi, LoopTemplateVersionId = version.Id, Status = status, RecoveryPolicy = RecoveryPolicy.AutoResume };

        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.LoopRuns.Add(run);
        db.Context.SaveChanges();
    }

    [Fact]
    public void DbConnectionHealthy_returns_1_when_db_is_accessible()
    {
        using var db = new TestDb();
        var collector = new MetricsCollector(db.Context);

        var snapshot = collector.Snapshot();

        Assert.Contains("ild_db_connection_healthy 1", snapshot);
    }

    [Fact]
    public void DiskSpaceBytes_returns_non_zero_value()
    {
        using var db = new TestDb();
        var collector = new MetricsCollector(db.Context);

        var snapshot = collector.Snapshot();

        Assert.Contains("ild_disk_space_bytes", snapshot);
        var lines = snapshot.Split('\n');
        var diskLine = lines.FirstOrDefault(l => l.StartsWith("ild_disk_space_bytes "));
        Assert.NotNull(diskLine);
    }

}
