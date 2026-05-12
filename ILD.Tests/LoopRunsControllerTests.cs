using FluentAssertions;
using ILD.Api.Controllers;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ILD.Tests;

public class LoopRunsControllerTests
{
    [Fact]
    public async Task GetAll_returns_runs_from_store()
    {
        var run1 = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = Guid.NewGuid(),
            LoopTemplateVersionId = Guid.NewGuid(),
            Status = LoopRunStatus.Running,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            CreatedAt = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow,
            RunNodes = new List<LoopRunNode>(),
        };
        var run2 = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = Guid.NewGuid(),
            LoopTemplateVersionId = Guid.NewGuid(),
            Status = LoopRunStatus.Completed,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            CreatedAt = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow,
            RunNodes = new List<LoopRunNode>(),
        };

        var store = new Mock<ILoopRunStore>();
        store.Setup(s => s.GetAllAsync()).ReturnsAsync(new[] { run1, run2 });

        var engine = new Mock<ILoopEngine>();
        var events = new Mock<IEventLogService>();
        var controller = new LoopRunsController(engine.Object, events.Object, store.Object);

        var result = await controller.GetAll();

        result.Should().BeOfType<OkObjectResult>();
        var ok = (OkObjectResult)result;
        var payload = ok.Value;
        payload.Should().NotBeNull();
        var items = (System.Collections.IEnumerable)payload!;
        items.Cast<object>().Should().HaveCount(2);
    }

    [Fact]
    public async Task GetEvents_includes_runNodeId_in_response()
    {
        var runId = Guid.NewGuid();
        var runNodeId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();

        var entry = new EventLog
        {
            Id = Guid.NewGuid(),
            LoopRunId = runId,
            Sequence = 1,
            EventType = EventType.NodeStarted,
            NodeId = nodeId,
            RunNodeId = runNodeId,
            Timestamp = DateTime.UtcNow,
            Data = "test payload",
        };

        var eventLogService = new Mock<IEventLogService>();
        eventLogService.Setup(s => s.GetByRunIdAfterCursorAsync(runId, 0, 100))
            .ReturnsAsync(new Data.DTOs.EventLogPage
            {
                Entries = new[] { entry },
                NextCursor = 1,
                HasMore = false,
            });

        var engine = new Mock<ILoopEngine>();
        var store = new Mock<ILoopRunStore>();
        store.Setup(s => s.GetByIdAsync(runId)).ReturnsAsync(new LoopRun
        {
            Id = runId,
            WorkItemId = Guid.NewGuid(),
            LoopTemplateVersionId = Guid.NewGuid(),
            Status = LoopRunStatus.Running,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            CreatedAt = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow,
            RunNodes = new List<LoopRunNode>(),
        });

        var controller = new LoopRunsController(engine.Object, eventLogService.Object, store.Object);

        var result = await controller.GetEvents(runId.ToString());

        result.Should().BeOfType<OkObjectResult>();
        var ok = (OkObjectResult)result;
        var payload = ok.Value!;
        var entries = payload!.GetType().GetProperty("entries")!.GetValue(payload)!;
        var firstEntry = ((System.Collections.IEnumerable)entries).Cast<object>().First();
        var runNodeIdValue = firstEntry.GetType().GetProperty("runNodeId")?.GetValue(firstEntry);
        runNodeIdValue.Should().BeEquivalentTo(runNodeId, "runNodeId should be projected from the event entity");
    }

    [Fact]
    public async Task GetById_includes_available_sessions_for_run()
    {
        var runId = Guid.NewGuid();
        var run = new LoopRun
        {
            Id = runId,
            WorkItemId = Guid.NewGuid(),
            LoopTemplateVersionId = Guid.NewGuid(),
            Status = LoopRunStatus.Running,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            CreatedAt = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow,
            SessionsJson = "[{\"providerId\":\"provider-1\",\"sessionId\":\"ses_current\"}]",
        };

        var store = new Mock<ILoopRunStore>();
        store.Setup(s => s.GetByIdAsync(runId)).ReturnsAsync(run);
        store.Setup(s => s.GetRunNodesAsync(runId)).ReturnsAsync(Array.Empty<LoopRunNode>());
        store.Setup(s => s.GetSessionSnapshotsAsync(runId)).ReturnsAsync(new[]
        {
            new AdapterSessionSnapshot
            {
                LoopRunId = runId,
                AdapterName = "OpenCode",
                SessionId = "ses_current",
                CreatedAt = DateTime.UtcNow.AddMinutes(-10),
                UpdatedAt = DateTime.UtcNow.AddMinutes(-5),
                SessionJson = "{}",
            },
            new AdapterSessionSnapshot
            {
                LoopRunId = runId,
                AdapterName = "OpenCode",
                SessionId = "ses_old",
                CreatedAt = DateTime.UtcNow.AddMinutes(-30),
                UpdatedAt = DateTime.UtcNow.AddMinutes(-20),
                SessionJson = "{}",
            },
        });
        store.Setup(s => s.GetSessionBindingsAsync(runId)).ReturnsAsync(new[]
        {
            new LoopRunSessionBinding
            {
                LoopRunId = runId,
                AdapterName = "OpenCode",
                PlaceholderId = "research",
                SessionId = "ses_current",
            },
        });

        var engine = new Mock<ILoopEngine>();
        var events = new Mock<IEventLogService>();
        var controller = new LoopRunsController(engine.Object, events.Object, store.Object);

        var result = await controller.GetById(runId.ToString());

        result.Should().BeOfType<OkObjectResult>();
        var ok = (OkObjectResult)result;
        var payload = ok.Value!;
        var sessions = (System.Collections.IEnumerable)payload.GetType().GetProperty("availableSessions")!.GetValue(payload)!;
        var items = sessions.Cast<object>().ToList();
        items.Should().HaveCount(2);

        var current = items.Single(i => (string)i.GetType().GetProperty("sessionId")!.GetValue(i)! == "ses_current");
        current.GetType().GetProperty("isCurrent")!.GetValue(current).Should().BeEquivalentTo(true);
        var placeholders = (System.Collections.IEnumerable)current.GetType().GetProperty("placeholders")!.GetValue(current)!;
        placeholders.Cast<object>().Should().ContainSingle().Which.Should().Be("research");
    }
}
