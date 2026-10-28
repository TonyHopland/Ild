using System.ComponentModel.DataAnnotations;
using ILD.Api.Controllers;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The rules a proposal endpoint applies before it stores anything, run on the
/// controller without a host. The HTTP suites keep one row per endpoint for the wiring.
/// </summary>
public sealed class AgentControllerProposalValidationTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly Mock<IWorkItemManager> _workItems = new();
    private readonly Mock<ISchedulerSettingsService> _settings = new();
    private Guid _runId;

    public AgentControllerProposalValidationTests()
    {
        _settings.Setup(s => s.GetGlobalVariableMaxLengthAsync(It.IsAny<CancellationToken>())).ReturnsAsync(4000);
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = "WI-1",
            LoopTemplateVersionId = version.Id,
            Status = LoopRunStatus.Running,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
        };
        _db.Context.LoopTemplates.Add(template);
        _db.Context.LoopTemplateVersions.Add(version);
        _db.Context.LoopRuns.Add(run);
        _db.Context.SaveChanges();
        _runId = run.Id;
    }

    public void Dispose() => _db.Dispose();

    private AgentController Build()
    {
        var controller = new AgentController(
            _workItems.Object,
            new Mock<ILoopTemplateManager>().Object,
            _db.LoopRuns,
            _db.Context,
            _db.Providers,
            NoPackageFeeds.Resolver,
            new Mock<IWorktreePreviewService>().Object,
            new Mock<IChatLoopScratchpad>().Object,
            new Mock<IChatNotifier>().Object,
            TestActors.Instance);
        var http = new DefaultHttpContext();
        http.Request.Headers["X-ILD-Run-Id"] = _runId.ToString();
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static List<ValidationResult> Validate(object dto)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(dto, new ValidationContext(dto), results, validateAllProperties: true);
        return results;
    }

    // -- Loop template proposals --------------------------------------------------

    private Task<IActionResult> ProposeLoop(AgentLoopTemplateProposalRequest request)
        => Build().ProposeLoopTemplateChange(request, new LoopTemplateProposalStore(_db.Context), default);

    public static TheoryData<string, int?, string?, string?> InvalidLoopProposals => new()
    {
        { "base version zero", 0, "{}", "Because." },
        { "negative base version", -1, "{}", "Because." },
        { "no base version", null, "{}", "Because." },
        { "blank rationale", 1, "{}", "  \t " },
        { "no rationale", 1, "{}", null },
        { "rationale over 2000", 1, "{}", new string('r', 2001) },
        { "document over 1,000,000 characters", 1, new string('x', 1_000_001), "Because." },
    };

    [Theory]
    [MemberData(nameof(InvalidLoopProposals))]
    public async Task An_invalid_loop_template_proposal_is_refused_and_stores_nothing(
        string _, int? baseVersion, string? document, string? rationale)
    {
        var result = await ProposeLoop(new AgentLoopTemplateProposalRequest
        {
            NameOrId = "Release Loop",
            BaseVersion = baseVersion,
            Document = document,
            Rationale = rationale,
        });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await _db.Context.LoopTemplateProposals.ToListAsync(TestContext.Current.CancellationToken));
    }

    // -- Work item edit proposals -------------------------------------------------

    private Task<IActionResult> ProposeEdit(AgentWorkItemEditProposalRequest request)
        => Build().ProposeWorkItemEdit("WI-1", request, default);

    public static TheoryData<string, AgentWorkItemEditProposalRequest> InvalidEdits => new()
    {
        { "nothing proposed", new() },
        { "only a rationale", new() { Rationale = "just because" } },
        { "empty title", new() { Title = "" } },
        { "blank title", new() { Title = "   " } },
        { "illegal branch name", new() { BranchNameOverride = "bad..name" } },
        { "illegal base branch", new() { BaseBranchOverride = "has space" } },
        { "blank addition", new() { Title = "ok", AddDependencies = ["  "] } },
        { "blank removal", new() { Title = "ok", RemoveDependencies = [""] } },
        { "addition repeated", new() { Title = "ok", AddDependencies = ["WI-2", "WI-2"] } },
        { "removal repeated", new() { Title = "ok", RemoveDependencies = ["WI-2", "WI-2"] } },
        { "in both lists", new() { Title = "ok", AddDependencies = ["WI-2"], RemoveDependencies = ["WI-2"] } },
    };

    [Theory]
    [MemberData(nameof(InvalidEdits))]
    public async Task An_invalid_work_item_edit_proposal_is_refused_and_stores_nothing(string _, AgentWorkItemEditProposalRequest request)
    {
        var result = await ProposeEdit(request);

        Assert.IsType<BadRequestObjectResult>(result);
        _workItems.Verify(m => m.ProposeEditAsync(
            It.IsAny<ILD.Core.Services.Remote.WorkItemActor>(), It.IsAny<string>(),
            It.IsAny<ILD.Core.Services.Remote.RemoteCreateEditProposalRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void A_work_item_edit_proposal_over_the_length_limits_fails_validation()
    {
        Assert.Contains(nameof(AgentWorkItemEditProposalRequest.Title),
            Validate(new AgentWorkItemEditProposalRequest { Title = new string('t', 513) }).SelectMany(r => r.MemberNames));
        Assert.Contains(nameof(AgentWorkItemEditProposalRequest.BranchNameOverride),
            Validate(new AgentWorkItemEditProposalRequest { BranchNameOverride = new string('b', 257) }).SelectMany(r => r.MemberNames));
        Assert.Contains(nameof(AgentWorkItemEditProposalRequest.BaseBranchOverride),
            Validate(new AgentWorkItemEditProposalRequest { BaseBranchOverride = new string('b', 257) }).SelectMany(r => r.MemberNames));
        Assert.Contains(nameof(AgentWorkItemEditProposalRequest.Rationale),
            Validate(new AgentWorkItemEditProposalRequest { Title = "ok", Rationale = new string('r', 2001) }).SelectMany(r => r.MemberNames));
        Assert.Empty(Validate(new AgentWorkItemEditProposalRequest
        {
            Title = new string('t', 512),
            BranchNameOverride = new string('b', 256),
            BaseBranchOverride = new string('b', 256),
            Rationale = new string('r', 2000),
        }));
    }

    // -- Global variable suggestions ----------------------------------------------

    private Task<IActionResult> Suggest(AgentGlobalVariableProposalRequest request)
        => Build().ProposeGlobalVariableChange(request, new GlobalVariableProposalStore(_db.Context), _settings.Object, default);

    public static TheoryData<string, AgentGlobalVariableProposalRequest> InvalidSuggestions => new()
    {
        { "no name", new() { Value = "x", Rationale = "r" } },
        { "blank name", new() { Name = "   ", Value = "x", Rationale = "r" } },
        { "name starting with a digit", new() { Name = "9lives", Value = "x", Rationale = "r" } },
        { "name with a hyphen", new() { Name = "bad-name", Value = "x", Rationale = "r" } },
        { "name with a dot", new() { Name = "Global.rules", Value = "x", Rationale = "r" } },
        { "name over 128 characters", new() { Name = "a" + new string('b', 128), Value = "x", Rationale = "r" } },
        { "no value", new() { Name = "rules", Rationale = "r" } },
        { "no rationale", new() { Name = "rules", Value = "x" } },
        { "blank rationale", new() { Name = "rules", Value = "x", Rationale = "  " } },
    };

    [Theory]
    [MemberData(nameof(InvalidSuggestions))]
    public async Task An_invalid_global_variable_suggestion_is_refused_and_stores_nothing(string _, AgentGlobalVariableProposalRequest request)
    {
        var result = await Suggest(request);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await _db.Context.GlobalVariableProposals.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_rationale_over_2000_fails_validation_on_the_loop_and_variable_requests_and_2000_passes()
    {
        Assert.NotEmpty(Validate(new AgentLoopTemplateProposalRequest { Rationale = new string('r', 2001) }));
        Assert.NotEmpty(Validate(new AgentGlobalVariableProposalRequest { Rationale = new string('r', 2001) }));
        Assert.Empty(Validate(new AgentLoopTemplateProposalRequest { Rationale = new string('r', 2000) }));
        Assert.Empty(Validate(new AgentGlobalVariableProposalRequest { Rationale = new string('r', 2000) }));
    }
}
