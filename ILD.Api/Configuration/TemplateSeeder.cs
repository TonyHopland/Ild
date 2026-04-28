using ILD.Core.DTOs;
using ILD.Core.Models;
using ILD.Core.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Api.Configuration;

/// <summary>
/// Creates the three PRD seed loop templates on first run.
/// </summary>
public static class TemplateSeeder
{
    public static async Task SeedAsync(AppDbContext db, ILoopTemplateManager mgr)
    {
        if (await db.LoopTemplates.AnyAsync()) return;

        await CreateSimpleCodeChangeAsync(mgr);
        await CreateAiAssistedFeatureAsync(mgr);
        await CreatePlanAsync(mgr);
    }

    private static async Task CreateSimpleCodeChangeAsync(ILoopTemplateManager mgr)
    {
        var nodes = new List<LoopNodeDto>
        {
            new() { Id = "start", Label = "Start", NodeType = "Start" },
            new() { Id = "build", Label = "Build", NodeType = "Cmd",
                Config = new Dictionary<string, object> { ["command"] = "echo build" } },
            new() { Id = "test", Label = "Test", NodeType = "Cmd",
                Config = new Dictionary<string, object> { ["command"] = "echo test" } },
            new() { Id = "cleanup", Label = "Cleanup", NodeType = "Cleanup" },
        };
        var edges = new List<LoopNodeEdgeDto>
        {
            new() { SourceNodeId = "start", TargetNodeId = "build", EdgeType = "OnSuccess" },
            new() { SourceNodeId = "build", TargetNodeId = "test", EdgeType = "OnSuccess" },
            new() { SourceNodeId = "test", TargetNodeId = "cleanup", EdgeType = "OnSuccess" },
        };
        await mgr.CreateLoopTemplateAsync(
            "Simple Code Change",
            "Build then test then cleanup",
            new LoopTemplateGraph(Guid.Empty, nodes, edges));
    }

    private static async Task CreateAiAssistedFeatureAsync(ILoopTemplateManager mgr)
    {
        var nodes = new List<LoopNodeDto>
        {
            new() { Id = "start", Label = "Start", NodeType = "Start" },
            new() { Id = "ai", Label = "AI Implement", NodeType = "AI",
                Config = new Dictionary<string, object>
                {
                    ["prompt"] = "Implement: {{WorkItem.Description}}",
                    ["model"] = "default",
                } },
            new() { Id = "build", Label = "Build", NodeType = "Cmd",
                Config = new Dictionary<string, object> { ["command"] = "echo build" } },
            new() { Id = "review", Label = "Human Review", NodeType = "Human" },
            new() { Id = "cleanup", Label = "Cleanup", NodeType = "Cleanup" },
        };
        var edges = new List<LoopNodeEdgeDto>
        {
            new() { SourceNodeId = "start", TargetNodeId = "ai", EdgeType = "OnSuccess" },
            new() { SourceNodeId = "ai", TargetNodeId = "build", EdgeType = "OnSuccess" },
            new() { SourceNodeId = "build", TargetNodeId = "review", EdgeType = "OnSuccess" },
            new() { SourceNodeId = "build", TargetNodeId = "ai", EdgeType = "OnFailure" },
            new() { SourceNodeId = "review", TargetNodeId = "cleanup", EdgeType = "OnSuccess" },
        };
        await mgr.CreateLoopTemplateAsync(
            "AI-Assisted Feature",
            "AI implements, then human reviews",
            new LoopTemplateGraph(Guid.Empty, nodes, edges));
    }

    private static async Task CreatePlanAsync(ILoopTemplateManager mgr)
    {
        var nodes = new List<LoopNodeDto>
        {
            new() { Id = "start", Label = "Start", NodeType = "Start" },
            new() { Id = "plan", Label = "AI Plan", NodeType = "AI",
                Config = new Dictionary<string, object>
                {
                    ["prompt"] = "Create a plan for: {{WorkItem.Title}}\n{{WorkItem.Description}}",
                } },
            new() { Id = "review", Label = "Human Review", NodeType = "Human" },
            new() { Id = "cleanup", Label = "Cleanup", NodeType = "Cleanup" },
        };
        var edges = new List<LoopNodeEdgeDto>
        {
            new() { SourceNodeId = "start", TargetNodeId = "plan", EdgeType = "OnSuccess" },
            new() { SourceNodeId = "plan", TargetNodeId = "review", EdgeType = "OnSuccess" },
            new() { SourceNodeId = "review", TargetNodeId = "cleanup", EdgeType = "OnSuccess" },
        };
        await mgr.CreateLoopTemplateAsync(
            "Plan",
            "AI proposes plan, human reviews",
            new LoopTemplateGraph(Guid.Empty, nodes, edges));
    }
}
