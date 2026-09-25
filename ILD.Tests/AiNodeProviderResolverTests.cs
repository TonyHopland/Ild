using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Remote;
using ILD.Data.Entities;

namespace ILD.Tests;

public class AiNodeProviderResolverTests
{
    [Theory]
    [MemberData(nameof(AiNodeProviderResolutionScenarios.NoDefaultCases), MemberType = typeof(AiNodeProviderResolutionScenarios))]
    public async Task With_no_default_provider_resolves_the_override_or_the_tag_holder(
        string nodeConfig, RemoteAiProviderOverrideMode mode, bool overrideTargetSet, string expected)
    {
        using var db = new TestDb();
        var seeded = await AiNodeProviderResolutionScenarios.SeedAsync(db.Providers, withDefault: false);
        var tag = NodeConfig.Parse<NodeConfig.Ai>(nodeConfig).AiProviderTag;

        var (provider, error) = await AiNodeProviderResolver.ResolveAsync(
            db.Providers, tag, mode, overrideTargetSet ? seeded.Bravo.Id : null);

        Assert.Null(error);
        Assert.Equal(expected, provider?.Name);
    }

    [Theory]
    [InlineData("Nightly", RemoteAiProviderOverrideMode.None)]
    [InlineData("Nightly", RemoteAiProviderOverrideMode.OverrideAll)]
    [InlineData("Nightly", RemoteAiProviderOverrideMode.OverrideDefault)]
    [InlineData(null, RemoteAiProviderOverrideMode.OverrideDefault)]
    public async Task With_no_default_provider_and_no_applicable_override_fails_naming_the_tag(
        string? tag, RemoteAiProviderOverrideMode mode)
    {
        using var db = new TestDb();
        await AiNodeProviderResolutionScenarios.SeedAsync(db.Providers, withDefault: false);

        var (provider, error) = await AiNodeProviderResolver.ResolveAsync(db.Providers, tag, mode, null);

        Assert.Null(provider);
        Assert.NotNull(error);
        Assert.Contains("no default provider", error, StringComparison.OrdinalIgnoreCase);
        if (tag is not null)
            Assert.Contains(tag, error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_applicable_override_naming_no_provider_fails_naming_its_id(bool withDefault)
    {
        using var db = new TestDb();
        await AiNodeProviderResolutionScenarios.SeedAsync(db.Providers, withDefault: withDefault);
        var missingId = Guid.NewGuid();

        var (provider, error) = await AiNodeProviderResolver.ResolveAsync(
            db.Providers, "Nightly", RemoteAiProviderOverrideMode.OverrideDefault, missingId);

        Assert.Null(provider);
        Assert.NotNull(error);
        Assert.Contains(missingId.ToString(), error);
    }
}
