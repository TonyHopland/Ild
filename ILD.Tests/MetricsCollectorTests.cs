using FluentAssertions;
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

        snapshot.Should().NotBeNullOrEmpty();
        snapshot.Should().Contain("ild_loop_runs_total");
        snapshot.Should().Contain("# HELP");
        snapshot.Should().Contain("# TYPE");
    }

    [Fact]
    public void MetricsController_returns_prometheus_text_plain_content_type()
    {
        using var db = new TestDb();
        var collector = new MetricsCollector(db.Context);
        var controller = new ILD.Api.Controllers.MetricsController(collector);

        var result = controller.Get();

        result.Should().BeOfType<ContentResult>();
        ((ContentResult)result).ContentType.Should().Be("text/plain; version=0.0.4; charset=utf-8");
    }

    [Fact]
    public void MetricsController_response_contains_all_required_metric_families()
    {
        using var db = new TestDb();
        var collector = new MetricsCollector(db.Context);
        var controller = new ILD.Api.Controllers.MetricsController(collector);

        var result = (ContentResult)controller.Get();
        var body = (string?)result.Content;

        body.Should().Contain("ild_loop_runs_total");
        body.Should().Contain("ild_node_execution_duration_seconds");
        body.Should().Contain("ild_llm_api_latency_seconds");
        body.Should().Contain("ild_llm_tokens_total");
        body.Should().Contain("ild_workitems_total");
        body.Should().Contain("ild_db_connection_healthy");
        body.Should().Contain("ild_disk_space_bytes");
    }

    [Fact]
    public void LoopRunsTotal_reflects_completed_run_count()
    {
        using var db = new TestDb();
        SeedAndRun(db, LoopRunStatus.Completed);

        var collector = new MetricsCollector(db.Context);
        var snapshot = collector.Snapshot();

        snapshot.Should().Contain("ild_loop_runs_total{status=\"completed\"} 1");
    }

    [Fact]
    public void LoopRunsTotal_reflects_failed_run_count()
    {
        using var db = new TestDb();
        SeedAndRun(db, LoopRunStatus.Failed);

        var collector = new MetricsCollector(db.Context);
        var snapshot = collector.Snapshot();

        snapshot.Should().Contain("ild_loop_runs_total{status=\"failed\"} 1");
    }

    [Fact]
    public void LoopRunsTotal_reflects_cancelled_run_count()
    {
        using var db = new TestDb();
        SeedAndRun(db, LoopRunStatus.Cancelled);

        var collector = new MetricsCollector(db.Context);
        var snapshot = collector.Snapshot();

        snapshot.Should().Contain("ild_loop_runs_total{status=\"cancelled\"} 1");
    }

    private void SeedAndRun(TestDb db, LoopRunStatus status)
    {
        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://example/r.git" };
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = RecoveryPolicy.AutoResume, MaxNodeExecutions = 200, MaxWallClockHours = 24 };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        var wi = new WorkItem { Id = Guid.NewGuid(), Title = "test", Status = WorkItemStatus.Running, RepositoryId = repo.Id, LoopTemplateVersionId = version.Id };
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = wi.Id, LoopTemplateVersionId = version.Id, Status = status, RecoveryPolicy = RecoveryPolicy.AutoResume };

        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.WorkItems.Add(wi);
        db.Context.LoopRuns.Add(run);
        db.Context.SaveChanges();
    }

    [Fact]
    public void WorkItemsTotal_reflects_workitem_count_by_status()
    {
        using var db = new TestDb();

        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://example/r.git" };
        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);

        db.Context.WorkItems.Add(new WorkItem { Id = Guid.NewGuid(), Title = "Backlog Item", Status = WorkItemStatus.Backlog, RepositoryId = repo.Id });
        db.Context.WorkItems.Add(new WorkItem { Id = Guid.NewGuid(), Title = "Running Item", Status = WorkItemStatus.Running, RepositoryId = repo.Id });
        db.Context.WorkItems.Add(new WorkItem { Id = Guid.NewGuid(), Title = "Done Item", Status = WorkItemStatus.Done, RepositoryId = repo.Id });
        db.Context.SaveChanges();

        var collector = new MetricsCollector(db.Context);
        var snapshot = collector.Snapshot();

        snapshot.Should().Contain("ild_workitems_total{status=\"Backlog\"} 1");
        snapshot.Should().Contain("ild_workitems_total{status=\"Running\"} 1");
        snapshot.Should().Contain("ild_workitems_total{status=\"Done\"} 1");
    }

    [Fact]
    public void DbConnectionHealthy_returns_1_when_db_is_accessible()
    {
        using var db = new TestDb();
        var collector = new MetricsCollector(db.Context);

        var snapshot = collector.Snapshot();

        snapshot.Should().Contain("ild_db_connection_healthy 1");
    }

    [Fact]
    public void DiskSpaceBytes_returns_non_zero_value()
    {
        using var db = new TestDb();
        var collector = new MetricsCollector(db.Context);

        var snapshot = collector.Snapshot();

        snapshot.Should().Contain("ild_disk_space_bytes");
        var lines = snapshot.Split('\n');
        var diskLine = lines.FirstOrDefault(l => l.StartsWith("ild_disk_space_bytes "));
        diskLine.Should().NotBeNull();
    }

    [Fact]
    public void LlmMetrics_exist_as_placeholders_with_zero_values()
    {
        using var db = new TestDb();
        var collector = new MetricsCollector(db.Context);

        var snapshot = collector.Snapshot();

        snapshot.Should().Contain("ild_llm_api_latency_seconds_count 0");
        snapshot.Should().Contain("ild_llm_tokens_total{type=\"prompt\"} 0");
        snapshot.Should().Contain("ild_llm_tokens_total{type=\"completion\"} 0");
    }
}
