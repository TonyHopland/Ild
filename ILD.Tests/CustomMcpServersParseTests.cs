using ILD.Core.Services.Implementations.Adapters;

namespace ILD.Tests;

/// <summary>
/// Covers the tolerant parser behind the per-provider "Custom MCP servers (JSON)"
/// field: it must accept both CLI command shapes, and must fail open (never
/// throw, skip what it can't understand) so a malformed value can't take down an
/// AI node run.
/// </summary>
public class CustomMcpServersParseTests
{
    [Fact]
    public void Parse_reads_map_of_servers_with_array_command()
    {
        const string json = """
        {
          "chrome-devtools": {
            "command": ["npx", "-y", "chrome-devtools-mcp@latest", "--headless"]
          }
        }
        """;

        var server = Assert.Single(CustomMcpServers.Parse(json));
        Assert.Equal("chrome-devtools", server.Name);
        Assert.Equal(new[] { "npx", "-y", "chrome-devtools-mcp@latest", "--headless" }, server.Command);
        Assert.Empty(server.Args);
        Assert.Empty(server.Env);
    }

    [Fact]
    public void Parse_accepts_string_command_with_args_and_env()
    {
        const string json = """
        {
          "my-server": {
            "command": "node",
            "args": ["server.js", "--port", "9000"],
            "env": { "TOKEN": "abc", "PORT": 9000, "DEBUG": true }
          }
        }
        """;

        var server = Assert.Single(CustomMcpServers.Parse(json));
        Assert.Equal(new[] { "node" }, server.Command);
        Assert.Equal(new[] { "server.js", "--port", "9000" }, server.Args);
        Assert.Equal("abc", server.Env["TOKEN"]);
        // Non-string scalar env values are coerced to their raw text.
        Assert.Equal("9000", server.Env["PORT"]);
        Assert.Equal("true", server.Env["DEBUG"]);
    }

    [Fact]
    public void Parse_reserves_the_ild_name()
    {
        // A custom server literally named "ild" must be dropped so it can never
        // clobber the injected ILD MCP entry.
        const string json = """
        { "ild": { "command": "evil" }, "keep": { "command": "ok" } }
        """;

        var server = Assert.Single(CustomMcpServers.Parse(json));
        Assert.Equal("keep", server.Name);
    }

    [Fact]
    public void Parse_skips_servers_without_a_command_but_keeps_valid_ones()
    {
        const string json = """
        {
          "broken": { "args": ["x"] },
          "empty-command": { "command": [] },
          "good": { "command": ["run"] }
        }
        """;

        var server = Assert.Single(CustomMcpServers.Parse(json));
        Assert.Equal("good", server.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not valid json")]
    [InlineData("[\"array-not-object\"]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    public void Parse_fails_open_to_empty_on_blank_or_malformed_input(string? json)
    {
        Assert.Empty(CustomMcpServers.Parse(json));
    }
}
