using FluentAssertions;
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
        templates.Should().HaveCount(3);

        var aiFeature = templates.Single(t => t.Name == "AI-Assisted Feature");
        var aiFeatureGraph = await mgr.GetVersionGraphAsync(aiFeature.Id, 1);
        aiFeatureGraph.Should().NotBeNull();
        var implementConfig = aiFeatureGraph!.Nodes.Single(n => n.Label == "AI Implement").Config;
        ReadBool(implementConfig, "useSession").Should().BeTrue();
        ReadString(implementConfig, "prompt").Should().Be("{{PreviousNode.Output}}");
        ReadString(implementConfig, "sessionPlaceholder").Should().Be("implementation");
        implementConfig.Should().NotContainKey("initialPrompt");
        implementConfig.Should().NotContainKey("sessionPrompt");

        var implementInitialConfig = aiFeatureGraph.Nodes.Single(n => n.Label == "Prompt implement initial").Config;
        ReadString(implementInitialConfig, "prompt").Should().Contain("You are in charge of implementing this workitem");

        var implementRetryConfig = aiFeatureGraph.Nodes.Single(n => n.Label == "Prompt implement retry").Config;
        ReadString(implementRetryConfig, "prompt").Should().Contain("Your implementation was rejected");

        var reviewConfig = aiFeatureGraph.Nodes.Single(n => n.Label == "AI Review").Config;
        reviewConfig.Should().NotContainKey("useSession");
        ReadString(reviewConfig, "prompt").Should().Contain("Do a thorough review of this change.");
        reviewConfig.Should().NotContainKey("sessionPrompt");
        reviewConfig.Should().NotContainKey("sessionPlaceholder");

        var plan = templates.Single(t => t.Name == "Plan");
        var planGraph = await mgr.GetVersionGraphAsync(plan.Id, 1);
        planGraph.Should().NotBeNull();

        var grillConfig = planGraph!.Nodes.Single(n => n.Label == "AI Grill").Config;
        ReadBool(grillConfig, "useSession").Should().BeTrue();
        ReadString(grillConfig, "prompt").Should().Be("{{PreviousNode.Output}}");
        ReadString(grillConfig, "sessionPlaceholder").Should().Be("plan");
        grillConfig.Should().NotContainKey("initialPrompt");
        grillConfig.Should().NotContainKey("sessionPrompt");

        var grillInitialConfig = planGraph.Nodes.Single(n => n.Label == "Prompt grill initial").Config;
        ReadString(grillInitialConfig, "prompt").Should().Contain("Interview me relentlessly");

        var grillFollowupConfig = planGraph.Nodes.Single(n => n.Label == "Prompt grill followup").Config;
        ReadString(grillFollowupConfig, "prompt").Should().Be("{{PreviousNode.Output}}");

        var promptCreateTasksConfig = planGraph.Nodes.Single(n => n.Label == "Prompt create tasks").Config;
        ReadString(promptCreateTasksConfig, "prompt").Should().Contain("name: to-issues");

        var createTasksConfig = planGraph.Nodes.Single(n => n.Label == "AI create tasks").Config;
        ReadBool(createTasksConfig, "useSession").Should().BeTrue();
        ReadString(createTasksConfig, "prompt").Should().Be("{{PreviousNode.Output}}");
        ReadString(createTasksConfig, "sessionPlaceholder").Should().Be("plan");
        createTasksConfig.Should().NotContainKey("initialPrompt");
        createTasksConfig.Should().NotContainKey("sessionPrompt");
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