using System.Text.Json;
using ILD.Core.Services.Implementations;
using ILD.McpServer.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Keeps the <c>get_preview_config_schema</c> MCP tool honest: the starter it
/// ships to enrolling agents must actually parse and validate against the real
/// <see cref="WorktreePreviewService"/> that consumes ild.config.json. If the
/// config model or <c>ValidateProfile</c> rules change without the tool's schema
/// keeping up, these tests fail instead of agents silently producing a config
/// that installs or previews nothing.
/// </summary>
[Collection("EnvironmentPath")]
public class WorktreePreviewServiceConfigSchemaTests : IDisposable
{
    private readonly string _worktree;

    public WorktreePreviewServiceConfigSchemaTests()
    {
        _worktree = Path.Combine(Path.GetTempPath(), "ild-schema-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_worktree);
    }

    public void Dispose()
    {
        try { Directory.Delete(_worktree, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private WorktreePreviewService BuildService()
    {
        var factory = new Mock<IHttpClientFactory>();
        var configuration = new ConfigurationBuilder().Build();
        return new WorktreePreviewService(factory.Object, configuration, NullLogger<WorktreePreviewService>.Instance);
    }

    private static JsonElement ToolDocument()
        => JsonDocument.Parse(new ConfigTools().GetPreviewConfigSchema()).RootElement;

    private void WriteStarterConfig()
    {
        var starter = ToolDocument().GetProperty("starter");
        File.WriteAllText(
            Path.Combine(_worktree, "ild.config.json"),
            JsonSerializer.Serialize(starter));
    }

    [Fact]
    public void Schema_document_is_valid_json_and_advertises_the_expected_tokens()
    {
        var doc = ToolDocument();

        Assert.Equal("ild.config.json", doc.GetProperty("configFileName").GetString());

        var tokens = doc.GetProperty("templateTokens");
        // Every token the resolver in WorktreePreviewService.ResolveTemplate supports
        // must be documented, or agents will avoid tokens that actually work.
        foreach (var token in new[] { "${WORKTREE}", "${STATE_DIR}", "${HOST}", "${PUBLIC_HOST}", "${PORT}", "${PORT:<alias>}" })
        {
            Assert.True(tokens.TryGetProperty(token, out _), $"templateTokens should document {token}");
        }
    }

    [Fact]
    public void Starter_conforms_to_its_own_schema_required_service_fields()
    {
        var doc = ToolDocument();
        var requiredServiceFields = doc
            .GetProperty("schema").GetProperty("definitions").GetProperty("service")
            .GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToList();

        var services = doc
            .GetProperty("starter").GetProperty("preview").GetProperty("profiles")
            .GetProperty("app").GetProperty("services").EnumerateArray().ToList();

        Assert.NotEmpty(services);
        foreach (var service in services)
        {
            foreach (var field in requiredServiceFields)
            {
                Assert.True(service.TryGetProperty(field, out _),
                    $"starter service is missing schema-required field '{field}'");
            }
        }
    }

    [Fact]
    public async Task Starter_validates_through_the_real_preview_parser_and_validator()
    {
        WriteStarterConfig();
        var service = BuildService();

        var result = await service.ValidateConfigAsync(_worktree, cancellationToken: CancellationToken.None);

        Assert.True(result.Configured);
        Assert.Equal("app", result.ProfileName);
        // The starter demonstrates two wired services; ValidateConfigAsync runs the
        // same per-service checks (name/command/port/healthUrl/suggestedPort) the
        // preview-start path uses, so reaching here proves every required rule passes.
        Assert.Equal(new[] { "backend", "app" }, result.Services);
    }

    [Fact]
    public async Task Starter_install_steps_run_through_the_real_install_runner()
    {
        WriteStarterConfig();
        var service = BuildService();

        // The starter's install command is a harmless echo placeholder, so this both
        // proves the install shape parses and that running it is side-effect free.
        var result = await service.InstallAsync(_worktree, cancellationToken: CancellationToken.None);

        Assert.True(result.Installed);
    }
}
