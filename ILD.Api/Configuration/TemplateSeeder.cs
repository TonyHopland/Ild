using System.Text.Json;
using System.Text.Json.Serialization;
using ILD.Data.DTOs;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;

namespace ILD.Api.Configuration;

/// <summary>
/// Seeds the curated example loop templates (see the repo's example-loops/
/// folder) on first run. The JSON files are embedded into this assembly so the
/// seed works regardless of the deployment's working directory.
/// </summary>
public static class TemplateSeeder
{
    /// <summary>Logical-name prefix the example-loops JSON files are embedded under.</summary>
    private const string ResourcePrefix = "ExampleLoops.";

    private static readonly JsonSerializerOptions SeedJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task SeedAsync(ILoopTemplateStore templateStore, ILoopTemplateManager mgr)
    {
        var existing = await templateStore.GetAllAsync();
        if (existing.Any()) return;

        foreach (var template in LoadSeedTemplates())
        {
            await mgr.CreateLoopTemplateAsync(
                template.Name,
                template.Description,
                new LoopTemplateGraph(Guid.Empty, template.Nodes, template.Edges),
                template.RecoveryPolicy);
        }
    }

    public static async Task SeedWorkItemServerAsync(IAppSettingStore settingStore)
    {
        var serverUrl = Environment.GetEnvironmentVariable("ILD_WORKITEM_SERVER_URL");
        var apiKey = Environment.GetEnvironmentVariable("ILD_WORKITEM_SERVER_API_KEY");

        if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(apiKey))
            return;

        var existing = await settingStore.GetByKeyAsync(AppSettingKeys.WorkItemServerUrl);
        if (existing != null && !string.IsNullOrEmpty(existing.Value))
            return;

        await settingStore.UpsertAsync(AppSettingKeys.WorkItemServerUrl, serverUrl);
        await settingStore.UpsertAsync(AppSettingKeys.WorkItemServerApiKey, apiKey);
    }

    /// <summary>
    /// Reads every embedded example-loops template, ordered by resource name so
    /// the seed is deterministic.
    /// </summary>
    private static IEnumerable<SeedTemplate> LoadSeedTemplates()
    {
        var assembly = typeof(TemplateSeeder).Assembly;
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal);

        foreach (var name in resourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Seed template resource '{name}' could not be opened.");
            var template = JsonSerializer.Deserialize<SeedTemplate>(stream, SeedJsonOptions)
                ?? throw new InvalidOperationException($"Seed template resource '{name}' could not be parsed.");
            yield return template;
        }
    }

    /// <summary>
    /// Deserialization shape for the ild-loop-template/v1 export JSON. The node
    /// and edge DTOs already match the file's field names, so they bind directly.
    /// </summary>
    private sealed class SeedTemplate
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public RecoveryPolicy RecoveryPolicy { get; set; } = RecoveryPolicy.AutoResume;
        public List<LoopNodeDto> Nodes { get; set; } = new();
        public List<LoopNodeEdgeDto> Edges { get; set; } = new();
    }
}
