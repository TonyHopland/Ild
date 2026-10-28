using ILD.Data;

namespace ILD.Tests;

public class AiToolCatalogTests
{
    [Theory]
    [InlineData("opencode")]
    [InlineData("pi")]
    [InlineData("  OpenCode  ")]
    public void GetSupportedToolsForProviderType_returns_default_tools_for_the_default_agents(string providerType)
    {
        var tools = DeclaredTraits.For(providerType).SupportedTools;
        Assert.Equal(4, tools.Count);
        Assert.Contains(tools, t => t.Key == AiToolCatalog.Read);
        Assert.Contains(tools, t => t.Key == AiToolCatalog.Write);
        Assert.Contains(tools, t => t.Key == AiToolCatalog.Execute);
        Assert.Contains(tools, t => t.Key == AiToolCatalog.Ild);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData(null)]
    [InlineData("")]
    public void An_unknown_provider_has_no_tools(string? providerType)
    {
        Assert.Empty(DeclaredTraits.For(providerType).SupportedTools);
        Assert.Empty(AiToolCatalog.GetDefaultToolKeys(DeclaredTraits.For(providerType)));
        Assert.Empty(AiToolCatalog.NormalizeSelectedToolKeys(DeclaredTraits.For(providerType), new[] { "read", "write" }));
    }

    [Fact]
    public void GetDefaultToolKeysForProviderType_returns_all_keys_for_opencode()
    {
        var keys = AiToolCatalog.GetDefaultToolKeys(DeclaredTraits.For("opencode"));
        Assert.Equal(4, keys.Count);
        Assert.Contains(AiToolCatalog.Read, keys);
        Assert.Contains(AiToolCatalog.Write, keys);
        Assert.Contains(AiToolCatalog.Execute, keys);
        Assert.Contains(AiToolCatalog.Ild, keys);
    }

    public static TheoryData<string[], string[]> Selections => new()
    {
        { ["read", "execute"], ["read", "execute"] },
        { ["read", "unsupported-tool"], ["read"] },
        { ["READ", "Execute"], ["READ", "Execute"] },
        { ["read", "read", "READ"], ["read"] },
    };

    [Theory]
    [MemberData(nameof(Selections))]
    public void NormalizeSelectedToolKeys_keeps_supported_keys_case_insensitively_without_duplicates(string[] selected, string[] expectedKeys)
    {
        var result = AiToolCatalog.NormalizeSelectedToolKeys(DeclaredTraits.For("opencode"), selected);

        Assert.Equal(expectedKeys, result);
    }

    /// <summary>Copilot and Codex: their only tool is ild, so an explicit list without it turns ILD off.</summary>
    public static TheoryData<string> IldOnlyProviderTypes => new() { "copilot", "codex", " Codex " };

    [Theory]
    [MemberData(nameof(IldOnlyProviderTypes))]
    public void GetSupportedToolsForProviderType_offers_the_ild_only_agents_only_ild_on_by_default(string providerType)
    {
        var tool = Assert.Single(DeclaredTraits.For(providerType).SupportedTools);

        Assert.Equal(AiToolCatalog.Ild, tool.Key);
        Assert.True(tool.DefaultEnabled);
        Assert.Equal(new[] { AiToolCatalog.Ild }, AiToolCatalog.GetDefaultToolKeys(DeclaredTraits.For(providerType)));
    }

    [Theory]
    [MemberData(nameof(IldOnlyProviderTypes))]
    public void NormalizeSelectedToolKeys_for_the_ild_only_agents_treats_an_omitted_selection_as_ild_on(string providerType)
    {
        Assert.Equal(new[] { AiToolCatalog.Ild }, AiToolCatalog.NormalizeSelectedToolKeys(DeclaredTraits.For(providerType), null));
    }

    [Theory]
    [MemberData(nameof(IldOnlyProviderTypes))]
    public void NormalizeSelectedToolKeys_for_the_ild_only_agents_treats_an_explicit_empty_selection_as_ild_off(string providerType)
    {
        Assert.Empty(AiToolCatalog.NormalizeSelectedToolKeys(DeclaredTraits.For(providerType), Array.Empty<string>()));
    }

    [Theory]
    [MemberData(nameof(IldOnlyProviderTypes))]
    public void NormalizeSelectedToolKeys_for_the_ild_only_agents_treats_a_fully_filtered_selection_as_ild_off(string providerType)
    {
        Assert.Empty(AiToolCatalog.NormalizeSelectedToolKeys(DeclaredTraits.For(providerType), new[] { "read" }));
        Assert.Empty(AiToolCatalog.NormalizeSelectedToolKeys(DeclaredTraits.For(providerType), new[] { "read", "write", "execute" }));
    }

    [Theory]
    [MemberData(nameof(IldOnlyProviderTypes))]
    public void NormalizeSelectedToolKeys_for_the_ild_only_agents_keeps_ild_and_drops_unsupported_keys(string providerType)
    {
        Assert.Equal(new[] { AiToolCatalog.Ild }, AiToolCatalog.NormalizeSelectedToolKeys(DeclaredTraits.For(providerType), new[] { "ild" }));
        Assert.Equal(new[] { AiToolCatalog.Ild }, AiToolCatalog.NormalizeSelectedToolKeys(DeclaredTraits.For(providerType), new[] { "ild", "read" }));
    }

    [Theory]
    [InlineData("copilot", false)]
    [InlineData("codex", false)]
    [InlineData("CODEX", false)]
    [InlineData("claude-code", true)]
    [InlineData("opencode", true)]
    [InlineData("pi", true)]
    public void Only_agents_whose_tools_can_be_turned_off_support_a_call_with_no_tools(string providerType, bool supported)
    {
        Assert.Equal(supported, DeclaredTraits.For(providerType).SupportsNoTools);
    }

    [Theory]
    [InlineData("opencode")]
    [InlineData("pi")]
    [InlineData("claude-code")]
    public void NormalizeSelectedToolKeys_for_the_default_agents_still_treats_empty_and_filtered_as_defaults(string providerType)
    {
        var defaults = AiToolCatalog.GetDefaultToolKeys(DeclaredTraits.For(providerType));
        Assert.Equal(4, defaults.Count);

        Assert.Equal(defaults, AiToolCatalog.NormalizeSelectedToolKeys(DeclaredTraits.For(providerType), null));
        Assert.Equal(defaults, AiToolCatalog.NormalizeSelectedToolKeys(DeclaredTraits.For(providerType), Array.Empty<string>()));
        Assert.Equal(defaults, AiToolCatalog.NormalizeSelectedToolKeys(DeclaredTraits.For(providerType), new[] { "unsupported-tool" }));
        Assert.Equal(defaults, AiToolCatalog.NormalizeSelectedToolKeys(DeclaredTraits.For(providerType), new string?[] { "", "   ", null }));
    }

    [Fact]
    public void LoopAuthoringGuide_toolAllowlist_rule_names_the_copilot_exception()
    {
        var line = Assert.Single(
            LoopAuthoringGuide.Text.Split('\n'),
            l => l.TrimStart().StartsWith("- toolAllowlist:", StringComparison.Ordinal));

        Assert.Contains("Copilot", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"ild\"", line);
    }
}
