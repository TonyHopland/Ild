using ILD.Data;

namespace ILD.Tests;

public class AiToolCatalogTests
{
    [Fact]
    public void GetSupportedToolsForProviderType_returns_default_tools_for_opencode()
    {
        var tools = AiToolCatalog.GetSupportedToolsForProviderType("opencode");
        Assert.Equal(4, tools.Count);
        Assert.Contains(tools, t => t.Key == AiToolCatalog.Read);
        Assert.Contains(tools, t => t.Key == AiToolCatalog.Write);
        Assert.Contains(tools, t => t.Key == AiToolCatalog.Execute);
        Assert.Contains(tools, t => t.Key == AiToolCatalog.Ild);
    }

    [Fact]
    public void GetSupportedToolsForProviderType_returns_default_tools_for_pi()
    {
        var tools = AiToolCatalog.GetSupportedToolsForProviderType("pi");
        Assert.Equal(4, tools.Count);
    }

    [Fact]
    public void GetSupportedToolsForProviderType_returns_default_tools_for_opencode_with_whitespace()
    {
        var tools = AiToolCatalog.GetSupportedToolsForProviderType("  OpenCode  ");
        Assert.Equal(4, tools.Count);
    }

    [Fact]
    public void GetSupportedToolsForProviderType_returns_empty_for_unknown_provider()
    {
        var tools = AiToolCatalog.GetSupportedToolsForProviderType("unknown");
        Assert.Empty(tools);
    }

    [Fact]
    public void GetSupportedToolsForProviderType_returns_empty_for_null()
    {
        var tools = AiToolCatalog.GetSupportedToolsForProviderType(null);
        Assert.Empty(tools);
    }

    [Fact]
    public void GetSupportedToolsForProviderType_returns_empty_for_empty_string()
    {
        var tools = AiToolCatalog.GetSupportedToolsForProviderType("");
        Assert.Empty(tools);
    }

    [Fact]
    public void GetDefaultToolKeysForProviderType_returns_all_keys_for_opencode()
    {
        var keys = AiToolCatalog.GetDefaultToolKeysForProviderType("opencode");
        Assert.Equal(4, keys.Count);
        Assert.Contains(AiToolCatalog.Read, keys);
        Assert.Contains(AiToolCatalog.Write, keys);
        Assert.Contains(AiToolCatalog.Execute, keys);
        Assert.Contains(AiToolCatalog.Ild, keys);
    }

    [Fact]
    public void GetDefaultToolKeysForProviderType_returns_empty_for_unknown_provider()
    {
        var keys = AiToolCatalog.GetDefaultToolKeysForProviderType("unknown");
        Assert.Empty(keys);
    }

    [Fact]
    public void NormalizeSelectedToolKeys_returns_valid_selected_keys()
    {
        var result = AiToolCatalog.NormalizeSelectedToolKeys(
            "opencode",
            new[] { "read", "execute" });

        Assert.Equal(2, result.Count);
        Assert.Contains("read", result);
        Assert.Contains("execute", result);
    }

    [Fact]
    public void NormalizeSelectedToolKeys_filters_out_unsupported_keys()
    {
        var result = AiToolCatalog.NormalizeSelectedToolKeys(
            "opencode",
            new[] { "read", "unsupported-tool" });

        Assert.Single(result);
        Assert.Equal("read", result[0]);
    }

    [Fact]
    public void NormalizeSelectedToolKeys_falls_back_to_defaults_when_empty()
    {
        var result = AiToolCatalog.NormalizeSelectedToolKeys("opencode", Array.Empty<string>());

        Assert.Equal(4, result.Count);
        Assert.Contains(AiToolCatalog.Read, result);
    }

    [Fact]
    public void NormalizeSelectedToolKeys_falls_back_to_defaults_when_null()
    {
        var result = AiToolCatalog.NormalizeSelectedToolKeys("opencode", null);

        Assert.Equal(4, result.Count);
    }

    [Fact]
    public void NormalizeSelectedToolKeys_filters_blank_and_whitespace_keys()
    {
        string?[] keys = ["", "   ", null];
        var result = AiToolCatalog.NormalizeSelectedToolKeys(
            "opencode",
            keys);

        // Falls back to defaults since all keys are blank/null
        Assert.Equal(4, result.Count);
    }

    [Fact]
    public void NormalizeSelectedToolKeys_is_case_insensitive()
    {
        var result = AiToolCatalog.NormalizeSelectedToolKeys(
            "opencode",
            new[] { "READ", "Execute" });

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void NormalizeSelectedToolKeys_returns_empty_for_unknown_provider()
    {
        var result = AiToolCatalog.NormalizeSelectedToolKeys(
            "unknown",
            new[] { "read", "write" });

        Assert.Empty(result);
    }

    [Fact]
    public void NormalizeSelectedToolKeys_deduplicates_keys()
    {
        var result = AiToolCatalog.NormalizeSelectedToolKeys(
            "opencode",
            new[] { "read", "read", "READ" });

        Assert.Single(result);
    }
}
