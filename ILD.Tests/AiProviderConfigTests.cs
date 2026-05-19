using ILD.Data.Entities;

namespace ILD.Tests;

public class AiProviderConfigTests
{
    [Fact]
    public async Task CreateProvider_with_config_persists_and_reads_back()
    {
        using var db = new TestDb();

        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "test-provider",
            Type = "pi",
            BaseUrl = "https://api.example.com",
            Model = "gpt-4",
            Config = "{\"customField\":\"value\"}",
            CreatedAt = DateTime.UtcNow,
        };

        await db.Providers.CreateAiProviderAsync(provider);
        var saved = await db.Providers.GetAiProviderByIdAsync(provider.Id);

        Assert.NotNull(saved);
        Assert.Equal("{\"customField\":\"value\"}", saved!.Config);
    }

    [Fact]
    public async Task UpdateProvider_config_persists()
    {
        using var db = new TestDb();

        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "test-provider",
            Type = "pi",
            BaseUrl = "https://api.example.com",
            Model = "gpt-4",
            CreatedAt = DateTime.UtcNow,
        };

        await db.Providers.CreateAiProviderAsync(provider);

        provider.Config = "{\"updatedField\":\"newValue\"}";
        await db.Providers.UpdateAiProviderAsync(provider);

        var saved = await db.Providers.GetAiProviderByIdAsync(provider.Id);
        Assert.Equal("{\"updatedField\":\"newValue\"}", saved!.Config);
    }

    [Fact]
    public async Task Provider_without_config_has_null_config()
    {
        using var db = new TestDb();

        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "test-provider",
            Type = "pi",
            BaseUrl = "https://api.example.com",
            Model = "gpt-4",
            CreatedAt = DateTime.UtcNow,
        };

        await db.Providers.CreateAiProviderAsync(provider);
        var saved = await db.Providers.GetAiProviderByIdAsync(provider.Id);

        Assert.Null(saved!.Config);
    }
}
