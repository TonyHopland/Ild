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
        Assert.DoesNotContain("model", names);
        Assert.DoesNotContain("baseUrl", names);
        Assert.DoesNotContain("apiKey", names);
    }

    [Fact]
    public void OpenAiCompatibleAdapter_schema_only_has_per_node_fields()
    {
        var factory = new Mock<IHttpClientFactory>();
        var adapter = new OpenAiCompatibleAdapter(factory.Object);

        var names = adapter.ConfigSchema.Select(f => f.Name).ToList();
        Assert.Equal(new[] { "temperature", "maxTokens" }, names);
    }

    [Fact]
    public void OpenCodeAdapter_schema_excludes_provider_level_fields()
    {
        var adapter = new OpenCodeAdapter();

        var names = adapter.ConfigSchema.Select(f => f.Name).ToList();
        Assert.DoesNotContain("binaryPath", names);
    }

    [Fact]
    public void OpenCodeAdapter_schema_is_empty()
    {
        var adapter = new OpenCodeAdapter();

        Assert.Empty(adapter.ConfigSchema);
    }

    [Fact]
    public void PiAdapter_schema_excludes_provider_level_fields()
    {
        var adapter = new PiAdapter();

        var names = adapter.ConfigSchema.Select(f => f.Name).ToList();
        Assert.DoesNotContain("binaryPath", names);
        Assert.DoesNotContain("provider", names);
        Assert.DoesNotContain("model", names);
        Assert.DoesNotContain("apiKey", names);
    }

    [Fact]
    public void PiAdapter_schema_is_empty()
    {
        var adapter = new PiAdapter();

        Assert.Empty(adapter.ConfigSchema);
    }
}
