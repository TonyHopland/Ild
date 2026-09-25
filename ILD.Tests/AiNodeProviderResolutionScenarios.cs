using ILD.Core.Services.Remote;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;

namespace ILD.Tests;

/// <summary>
/// One table of AI node provider-resolution cases, run through both
/// AINodeExecutor (which provider it runs on) and RemoteWorkItemCoordinator
/// (which provider its resume gate peeks). Sharing the table is what holds the
/// two to the same answer: a case they disagree on strands or flaps a run.
///
/// Providers: Alpha holds tag "QA", Dflt is the default, Bravo is the work
/// item's override target. In a node config, {Alpha}/{Dflt}/{Missing} stand
/// for Alpha's id, the default's id and an id no provider has.
/// </summary>
public static class AiNodeProviderResolutionScenarios
{
    public const string Alpha = "Alpha";
    public const string Dflt = "Dflt";
    public const string Bravo = "Bravo";

    public static TheoryData<string, RemoteAiProviderOverrideMode, bool, string> Cases => new()
    {
        { @"{""aiProviderTag"":""qa""}", RemoteAiProviderOverrideMode.None, true, Alpha },
        { @"{""aiProviderTag"":""  QA  ""}", RemoteAiProviderOverrideMode.None, true, Alpha },
        { @"{""aiProviderTag"":""Nobody""}", RemoteAiProviderOverrideMode.None, true, Dflt },
        { @"{""aiProviderTag"":""   ""}", RemoteAiProviderOverrideMode.None, true, Dflt },
        { @"{}", RemoteAiProviderOverrideMode.None, true, Dflt },
        // The legacy per-node provider id is ignored, whether or not it exists.
        { @"{""aiProviderId"":""{Alpha}""}", RemoteAiProviderOverrideMode.None, true, Dflt },
        { @"{""aiProviderId"":""{Missing}""}", RemoteAiProviderOverrideMode.None, true, Dflt },
        { @"{""aiProviderId"":""{Dflt}"",""aiProviderTag"":""QA""}", RemoteAiProviderOverrideMode.None, true, Alpha },
        { @"{""aiProviderTag"":""QA""}", RemoteAiProviderOverrideMode.OverrideAll, true, Bravo },
        { @"{}", RemoteAiProviderOverrideMode.OverrideAll, true, Bravo },
        // OverrideDefault leaves a node alone only when its tag matched a provider.
        { @"{""aiProviderTag"":""QA""}", RemoteAiProviderOverrideMode.OverrideDefault, true, Alpha },
        { @"{""aiProviderTag"":""Nobody""}", RemoteAiProviderOverrideMode.OverrideDefault, true, Bravo },
        { @"{}", RemoteAiProviderOverrideMode.OverrideDefault, true, Bravo },
        { @"{""aiProviderId"":""{Alpha}""}", RemoteAiProviderOverrideMode.OverrideDefault, true, Bravo },
        // An override mode without a target is a no-op.
        { @"{""aiProviderTag"":""QA""}", RemoteAiProviderOverrideMode.OverrideAll, false, Alpha },
        { @"{}", RemoteAiProviderOverrideMode.OverrideAll, false, Dflt },
    };

    /// <summary>
    /// Cases seeded with <c>withDefault: false</c>, so no provider is the
    /// default: an override that applies must still win, and OverrideDefault
    /// counts an unset or unmatched tag as falling back to the missing default.
    /// </summary>
    public static TheoryData<string, RemoteAiProviderOverrideMode, bool, string> NoDefaultCases => new()
    {
        { @"{""aiProviderTag"":""Nobody""}", RemoteAiProviderOverrideMode.OverrideAll, true, Bravo },
        { @"{""aiProviderTag"":""   ""}", RemoteAiProviderOverrideMode.OverrideAll, true, Bravo },
        { @"{}", RemoteAiProviderOverrideMode.OverrideAll, true, Bravo },
        { @"{""aiProviderTag"":""QA""}", RemoteAiProviderOverrideMode.OverrideAll, true, Bravo },
        { @"{""aiProviderTag"":""Nobody""}", RemoteAiProviderOverrideMode.OverrideDefault, true, Bravo },
        { @"{""aiProviderTag"":""   ""}", RemoteAiProviderOverrideMode.OverrideDefault, true, Bravo },
        { @"{}", RemoteAiProviderOverrideMode.OverrideDefault, true, Bravo },
        { @"{""aiProviderTag"":""QA""}", RemoteAiProviderOverrideMode.OverrideDefault, true, Alpha },
        { @"{""aiProviderTag"":""QA""}", RemoteAiProviderOverrideMode.None, true, Alpha },
    };

    public sealed record Seeded(AiProvider Alpha, AiProvider Dflt, AiProvider Bravo)
    {
        public IReadOnlyList<AiProvider> All => [Alpha, Dflt, Bravo];

        public AiProvider ByName(string name) => All.Single(p => p.Name == name);

        public string Expand(string nodeConfig) => nodeConfig
            .Replace("{Alpha}", Alpha.Id.ToString())
            .Replace("{Dflt}", Dflt.Id.ToString())
            .Replace("{Missing}", Guid.NewGuid().ToString());
    }

    /// <summary>
    /// Seeds the three providers, each with one concurrency slot. With
    /// <paramref name="withDefault"/> false, Dflt exists but is not the default.
    /// </summary>
    public static async Task<Seeded> SeedAsync(IProviderStore store, string providerType = "stub", bool withDefault = true)
    {
        AiProvider Make(string name, bool isDefault) => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Type = providerType,
            Model = "m",
            IsDefault = isDefault,
            Parallelism = 1,
            CreatedAt = DateTime.UtcNow,
        };

        var alpha = Make(Alpha, isDefault: false);
        var dflt = Make(Dflt, isDefault: withDefault);
        var bravo = Make(Bravo, isDefault: false);
        await store.CreateAiProviderAsync(alpha, ["QA"]);
        await store.CreateAiProviderAsync(dflt);
        await store.CreateAiProviderAsync(bravo);
        return new Seeded(alpha, dflt, bravo);
    }
}
