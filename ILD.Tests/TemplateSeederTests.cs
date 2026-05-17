using ILD.Api.Configuration;
using ILD.Core.Services.Implementations;
using System.Text.Json;

namespace ILD.Tests;

public class TemplateSeederTests
{
    [Fact]
    public async Task SeedAsync_creates_templates_with_single_ai_prompt_shape()
    {
        using var db = new TestDb();
        var mgr = new LoopTemplateManager(db.LoopTemplates);

        await TemplateSeeder.SeedAsync(db.LoopTemplates, mgr);

        var templates = await db.LoopTemplates.GetAllAsync();
        Assert.Equal(3, templates.Count());

        var aiFeature = templates.Single(t => t.Name == "AI-Assisted Feature");
        var aiFeatureGraph = await mgr.GetVersionGraphAsync(aiFeature.Id, 1);
        Assert.NotNull(aiFeatureGraph);
        var implementConfig = aiFeatureGraph!.Nodes.Single(n => n.Label == "AI Implement").Config;
        Assert.True(ReadBool(implementConfig, "useSession"));
        Assert.Equal("{{PreviousNode.Output}}", ReadString(implementConfig, "prompt"));
        Assert.Equal("implementation", ReadString(implementConfig, "sessionPlaceholder"));
        Assert.False(implementConfig.ContainsKey("initialPrompt"));
        Assert.False(implementConfig.ContainsKey("sessionPrompt"));

        var implementInitialConfig = aiFeatureGraph.Nodes.Single(n => n.Label == "Prompt implement initial").Config;
        Assert.Contains("You are in charge of implementing this workitem", ReadString(implementInitialConfig, "prompt"));

        var implementRetryConfig = aiFeatureGraph.Nodes.Single(n => n.Label == "Prompt implement retry").Config;
        Assert.Contains("Your implementation was rejected", ReadString(implementRetryConfig, "prompt"));

        var reviewConfig = aiFeatureGraph.Nodes.Single(n => n.Label == "AI Review").Config;
        Assert.False(reviewConfig.ContainsKey("useSession"));
        Assert.Contains("Do a thorough review of this change.", ReadString(reviewConfig, "prompt"));
        Assert.False(reviewConfig.ContainsKey("sessionPrompt"));
        Assert.False(reviewConfig.ContainsKey("sessionPlaceholder"));

        var plan = templates.Single(t => t.Name == "Plan");
        var planGraph = await mgr.GetVersionGraphAsync(plan.Id, 1);
        Assert.NotNull(planGraph);

        var grillConfig = planGraph!.Nodes.Single(n => n.Label == "AI Grill").Config;
        Assert.True(ReadBool(grillConfig, "useSession"));
        Assert.Equal("{{PreviousNode.Output}}", ReadString(grillConfig, "prompt"));
        Assert.Equal("plan", ReadString(grillConfig, "sessionPlaceholder"));
        Assert.False(grillConfig.ContainsKey("initialPrompt"));
        Assert.False(grillConfig.ContainsKey("sessionPrompt"));

        var grillInitialConfig = planGraph.Nodes.Single(n => n.Label == "Prompt grill initial").Config;
        Assert.Contains("Interview me relentlessly", ReadString(grillInitialConfig, "prompt"));

        var grillFollowupConfig = planGraph.Nodes.Single(n => n.Label == "Prompt grill followup").Config;
        Assert.Equal("{{PreviousNode.Output}}", ReadString(grillFollowupConfig, "prompt"));

        var promptCreateTasksConfig = planGraph.Nodes.Single(n => n.Label == "Prompt create tasks").Config;
        Assert.Contains("name: to-issues", ReadString(promptCreateTasksConfig, "prompt"));

        var createTasksConfig = planGraph.Nodes.Single(n => n.Label == "AI create tasks").Config;
        Assert.True(ReadBool(createTasksConfig, "useSession"));
        Assert.Equal("{{PreviousNode.Output}}", ReadString(createTasksConfig, "prompt"));
        Assert.Equal("plan", ReadString(createTasksConfig, "sessionPlaceholder"));
        Assert.False(createTasksConfig.ContainsKey("initialPrompt"));
        Assert.False(createTasksConfig.ContainsKey("sessionPrompt"));
    }

    private static bool ReadBool(Dictionary<string, object> config, string key)
    {
        return config[key] switch
        {
            bool value => value,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => throw new InvalidOperationException($"Config key '{key}' is not a boolean.")
        };
    }

    private static string? ReadString(Dictionary<string, object> config, string key)
    {
        return config[key] switch
        {
            string value => value,
            JsonElement { ValueKind: JsonValueKind.String } value => value.GetString(),
            _ => throw new InvalidOperationException($"Config key '{key}' is not a string.")
        };
    }
}