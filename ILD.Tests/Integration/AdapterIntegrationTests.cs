using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using Microsoft.Extensions.Http;

namespace ILD.Tests.Integration;

/// <summary>
/// Manual-run-only integration tests for agent adapters. These tests invoke a real
/// LLM via the adapter and are marked Explicit so they never run in CI.
///
/// To run:
///   ILD_INTEGRATION_OPENCODE_BINARY=opencode \
///   ILD_INTEGRATION_LLM_BASE_URL=https://localhost:1234/v1 \
///   ILD_INTEGRATION_LLM_API_KEY=sLocalOnly \
///   ILD_INTEGRATION_LLM_MODEL=default_model \
///   dotnet test --filter "FullyQualifiedName~ILD.Tests.Integration.AdapterIntegrationTests"
/// </summary>
[Trait("category", "manual")]
[Trait("category", "explicit")]
public class AdapterIntegrationTests
{
    #region OpenCode Adapter — binary reachable

    [ExplicitFact]
    public async Task OpenCodeAdapter_binary_is_reachable()
    {
        var binary = ReadEnv("ILD_INTEGRATION_OPENCODE_BINARY");

        var psi = new ProcessStartInfo(binary)
        {
            ArgumentList = { "--version" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null");

        await proc.WaitForExitAsync();

        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();

        Console.WriteLine($"[adapter-integration] binary={binary} exitCode={proc.ExitCode}");
        Console.WriteLine($"[adapter-integration] stdout={stdout.Trim()}");
        if (!string.IsNullOrEmpty(stderr))
            Console.WriteLine($"[adapter-integration] stderr={stderr.Trim()}");

        proc.ExitCode.Should().Be(0, "the opencode binary should be reachable and return --version successfully");
    }

    #endregion

    #region OpenCode Adapter — real LLM smoke test

    [ExplicitFact]
    public async Task OpenCodeAdapter_smoke_test_against_real_llm()
    {
        var binary = ReadEnv("ILD_INTEGRATION_OPENCODE_BINARY");
        var baseUrl = ReadEnv("ILD_INTEGRATION_LLM_BASE_URL");
        var apiKey = ReadEnv("ILD_INTEGRATION_LLM_API_KEY");
        var model = ReadEnv("ILD_INTEGRATION_LLM_MODEL");

        var adapter = new OpenCodeAdapter();

        var provider = new AiProvider
        {
            Name = "integration-test-provider",
            Type = "opencode",
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            Model = model,
            Config = JsonSerializer.Serialize(new { binaryPath = binary, timeoutSeconds = 120 })
        };

        var ctx = BuildContext(provider, "Reply with exactly the word hello and nothing else.");

        Console.WriteLine($"[adapter-integration] binary={binary}");
        Console.WriteLine($"[adapter-integration] model={provider.Name}/{model}");
        Console.WriteLine($"[adapter-integration] baseUrl={baseUrl}");

        var result = await adapter.ExecuteAsync(ctx);

        Console.WriteLine($"[adapter-integration] success={result.Success}");
        Console.WriteLine($"[adapter-integration] output={result.Output}");
        if (!string.IsNullOrEmpty(result.Error))
            Console.WriteLine($"[adapter-integration] error={result.Error}");

        result.Success.Should().BeTrue($"the adapter should succeed against a live provider", result.Error);
        result.Output.Should().NotBeNullOrEmpty("the adapter should return non-empty output");
        result.Output.Should().Contain("hello", "the model should have replied with the expected word");
    }

    #endregion

    #region OpenCode Adapter — config JSON shape diagnostic

    [ExplicitFact]
    public async Task OpenCodeAdapter_log_config_json_shape()
    {
        var binary = ReadEnv("ILD_INTEGRATION_OPENCODE_BINARY");
        var baseUrl = ReadEnv("ILD_INTEGRATION_LLM_BASE_URL");
        var apiKey = ReadEnv("ILD_INTEGRATION_LLM_API_KEY");
        var model = ReadEnv("ILD_INTEGRATION_LLM_MODEL");

        var adapter = new OpenCodeAdapter();

        var provider = new AiProvider
        {
            Name = "My Custom Provider",
            Type = "opencode",
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            Model = model,
            Config = JsonSerializer.Serialize(new { binaryPath = binary, timeoutSeconds = 120 })
        };

        var ctx = BuildContext(provider, "{{WorkItem.Title}}");

        var result = await adapter.ExecuteAsync(ctx);

        Console.WriteLine($"[adapter-integration] config-shape-test success={result.Success}");
        Console.WriteLine($"[adapter-integration] config-shape-test output={result.Output}");
        if (!string.IsNullOrEmpty(result.Error))
            Console.WriteLine($"[adapter-integration] config-shape-test error={result.Error}");

        result.Success.Should().BeTrue("opencode should accept the injected config shape", result.Error);
    }

    #endregion

    #region OpenAI Compatible Adapter — real LLM smoke test

    [ExplicitFact]
    public async Task OpenAiCompatibleAdapter_smoke_test_against_real_llm()
    {
        var baseUrl = ReadEnv("ILD_INTEGRATION_LLM_BASE_URL");
        var apiKey = ReadEnv("ILD_INTEGRATION_LLM_API_KEY");
        var model = ReadEnv("ILD_INTEGRATION_LLM_MODEL");

        var adapter = new OpenAiCompatibleAdapter(new TestHttpClientFactory());

        var provider = new AiProvider
        {
            Name = "integration-test-openai",
            Type = "openai",
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            Model = model,
            Config = JsonSerializer.Serialize(new
            {
                baseUrl,
                apiKey,
                model,
                maxTokens = 256
            })
        };

        var ctx = BuildContext(provider, "Reply with exactly the word hello and nothing else.");

        var result = await adapter.ExecuteAsync(ctx);

        Console.WriteLine($"[adapter-integration] openai-compat success={result.Success}");
        Console.WriteLine($"[adapter-integration] openai-compat output={result.Output}");
        if (!string.IsNullOrEmpty(result.Error))
            Console.WriteLine($"[adapter-integration] openai-compat error={result.Error}");

        result.Success.Should().BeTrue($"the adapter should succeed against a live provider", result.Error);
        result.Output.Should().NotBeNullOrEmpty("the adapter should return non-empty output");
    }

    #endregion

    #region Helpers

    static string ReadEnv(string name)
    {
        return Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"Required environment variable '{name}' is not set.");
    }

    static AgentExecutionContext BuildContext(AiProvider provider, string prompt)
    {
        return new AgentExecutionContext(
            Provider: provider,
            InitialPrompt: prompt,
            LoopPrompt: prompt,
            RunContext: new LoopRunContext(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Integration Test",
                "Verify adapter produces valid output",
                Environment.CurrentDirectory,
                "main",
                new List<string>(),
                null),
            ExecutionCount: 1,
            Cancel: CancellationToken.None);
    }

    #endregion
}

/// <summary>Minimal HttpClientFactory impl for the OpenAI-compatible adapter test.</summary>
sealed class TestHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string? name = null) => new();
}