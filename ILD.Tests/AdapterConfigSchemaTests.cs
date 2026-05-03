using FluentAssertions;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Data.DTOs;
using Moq;
using System.Net.Http;

namespace ILD.Tests;

public class AdapterConfigSchemaTests
{
    [Fact]
    public void OpenAiCompatibleAdapter_schema_excludes_provider_level_fields()
    {
        var factory = new Mock<IHttpClientFactory>();
        var adapter = new OpenAiCompatibleAdapter(factory.Object);

        var names = adapter.ConfigSchema.Select(f => f.Name).ToList();
        names.Should().NotContain("model", "model is a provider-level setting");
        names.Should().NotContain("baseUrl", "baseUrl is a provider-level setting");
        names.Should().NotContain("apiKey", "apiKey is a provider-level setting");
    }

    [Fact]
    public void OpenAiCompatibleAdapter_schema_only_has_per_node_fields()
    {
        var factory = new Mock<IHttpClientFactory>();
        var adapter = new OpenAiCompatibleAdapter(factory.Object);

        var names = adapter.ConfigSchema.Select(f => f.Name).ToList();
        names.Should().BeEquivalentTo("temperature", "maxTokens");
    }

    [Fact]
    public void OpenCodeAdapter_schema_excludes_provider_level_fields()
    {
        var adapter = new OpenCodeAdapter();

        var names = adapter.ConfigSchema.Select(f => f.Name).ToList();
        names.Should().NotContain("binaryPath", "binaryPath is a provider-level setting");
    }

    [Fact]
    public void OpenCodeAdapter_schema_only_has_timeout_field()
    {
        var adapter = new OpenCodeAdapter();

        var names = adapter.ConfigSchema.Select(f => f.Name).ToList();
        names.Should().BeEquivalentTo("timeoutSeconds");
    }
}
