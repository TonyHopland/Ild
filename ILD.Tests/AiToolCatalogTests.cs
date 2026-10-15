using ILD.Data;

namespace ILD.Tests;

public class AiToolCatalogTests
{
    [Fact]
    public void GetSupportedToolsForProviderType_returns_default_tools_for_opencode()
    {
        var tools = DeclaredTraits.For("opencode").SupportedTools;
        Assert.Equal(4, tools.Count);
        Assert.Contains(tools, t => t.Key == AiToolCatalog.Read);
        Assert.Contains(tools, t => t.Key == AiToolCatalog.Write);
        Assert.Contains(tools, t => t.Key == AiToolCatalog.Execute);
        Assert.Contains(tools, t => t.Key == AiToolCatalog.Ild);
    }

    [Fact]
    public void GetSupportedToolsForProviderType_returns_default_tools_for_pi()
    {
        var tools = DeclaredTraits.For("pi").SupportedTools;
        Assert.Equal(4, tools.Count);
    }

    [Fact]
    public void GetSupportedToolsForProviderType_returns_default_tools_for_opencode_with_whitespace()
    {
        var tools = DeclaredTraits.For("  OpenCode  ").SupportedTools;
        Assert.Equal(4, tools.Count);
    }

    [Fact]
    public void GetSupportedToolsForProviderType_returns_empty_for_unknown_provider()
    {
        var tools = DeclaredTraits.For("unknown").SupportedTools;
        Assert.Empty(tools);
    }

    [Fact]
    public void GetSupportedToolsForProviderType_returns_empty_for_null()
    {
        var tools = DeclaredTraits.For(null).SupportedTools;
        Assert.Empty(tools);
    }

    [Fact]
    public void GetSupportedToolsForProviderType_returns_empty_for_empty_string()
    {
        var tools = DeclaredTraits.For("").SupportedTools;
        Assert.Empty(tools);
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

    [Fact]
    public void GetDefaultToolKeysForProviderType_returns_empty_for_unknown_provider()
    {
        var keys = AiToolCatalog.GetDefaultToolKeys(DeclaredTraits.For("unknown"));
        Assert.Empty(keys);
    }

    [Fact]
    public void NormalizeSelectedToolKeys_returns_valid_selected_keys()
    {
        var result = AiToolCatalog.NormalizeSelectedToolKeys(
            DeclaredTraits.For("opencode"),
            new[] { "read", "execute" });

        Assert.Equal(2, result.Count);
        Assert.Contains("read", result);
        Assert.Contains("execute", result);
    }

    [Fact]
    public void NormalizeSelectedToolKeys_filters_out_unsupported_keys()
    {
        var result = AiToolCatalog.NormalizeSelectedToolKeys(
            DeclaredTraits.For("opencode"),
            new[] { "read", "unsupported-tool" });

        Assert.Single(result);
        Assert.Equal("read", result[0]);
    }

    [Fact]
    public void NormalizeSelectedToolKeys_falls_back_to_defaults_when_empty()
    {
        var result = AiToolCatalog.NormalizeSelectedToolKeys(DeclaredTraits.For("opencode"), Array.Empty<string>());

        Assert.Equal(4, result.Count);
        Assert.Contains(AiToolCatalog.Read, result);
    }

    [Fact]
    public void NormalizeSelectedToolKeys_falls_back_to_defaults_when_null()
    {
        var result = AiToolCatalog.NormalizeSelectedToolKeys(DeclaredTraits.For("opencode"), null);

        Assert.Equal(4, result.Count);
    }

    [Fact]
    public void NormalizeSelectedToolKeys_filters_blank_and_whitespace_keys()
    {
        string?[] keys = ["", "   ", null];
        var result = AiToolCatalog.NormalizeSelectedToolKeys(
            DeclaredTraits.For("opencode"),
            keys);

        // Falls back to defaults since all keys are blank/null
        Assert.Equal(4, result.Count);
    }

    [Fact]
    public void NormalizeSelectedToolKeys_is_case_insensitive()
    {
        var result = AiToolCatalog.NormalizeSelectedToolKeys(
            DeclaredTraits.For("opencode"),
            new[] { "READ", "Execute" });

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void NormalizeSelectedToolKeys_returns_empty_for_unknown_provider()
    {
        var result = AiToolCatalog.NormalizeSelectedToolKeys(
            DeclaredTraits.For("unknown"),
            new[] { "read", "write" });

        Assert.Empty(result);
    }

    [Fact]
    public void NormalizeSelectedToolKeys_deduplicates_keys()
    {
        var result = AiToolCatalog.NormalizeSelectedToolKeys(
            DeclaredTraits.For("opencode"),
            new[] { "read", "read", "READ" });

        Assert.Single(result);
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
