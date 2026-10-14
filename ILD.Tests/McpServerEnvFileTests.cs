using ILD.McpServer;

namespace ILD.Tests;

/// <summary>
/// The ILD MCP server reads its API URL, token and run or chat id from the JSON
/// file <c>ILD_MCP_ENV_FILE</c> names, so an agent CLI that cannot pass a
/// server environment (codex) is handed only the file's path. Without a usable
/// file it reads the environment variables, as every other launch does.
/// </summary>
public sealed class McpServerEnvFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ild-mcp-env-file-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static readonly Dictionary<string, string> FromEnvironment = new()
    {
        ["ILD_API_URL"] = "http://env.example:5000",
        ["ILD_API_TOKEN"] = "env-token",
        ["ILD_LOOP_RUN_ID"] = "env-run",
        ["ILD_CHAT_SESSION_ID"] = "env-chat",
    };

    [Fact]
    public void A_readable_file_supplies_the_settings()
    {
        var file = Write("""
            {
              "ILD_API_URL": "http://file.example:5000",
              "ILD_API_TOKEN": "file-token",
              "ILD_LOOP_RUN_ID": "file-run",
              "ILD_CHAT_SESSION_ID": "file-chat"
            }
            """);
        var log = new StringWriter();

        var options = IldClientOptions.Resolve(Environment(file), log);

        Assert.Equal(new IldClientOptions("http://file.example:5000", "file-token", "file-run", "file-chat"), options);
        Assert.Empty(log.ToString());
    }

    [Fact]
    public void Without_a_file_the_environment_supplies_the_settings_as_before()
    {
        var log = new StringWriter();

        Assert.Equal(
            new IldClientOptions("http://env.example:5000", "env-token", "env-run", "env-chat"),
            IldClientOptions.Resolve(Environment(file: null), log));
        Assert.Equal(
            new IldClientOptions("http://localhost:5000", "", null, null),
            IldClientOptions.Resolve(_ => null, log));
        Assert.Empty(log.ToString());
    }

    public static TheoryData<string> UnusableFiles => new()
    {
        "missing",
        "{ not json",
        "[\"ILD_API_URL\"]",
        "\"just a string\"",
    };

    [Theory]
    [MemberData(nameof(UnusableFiles))]
    public void An_unusable_file_falls_back_to_the_environment_and_says_so(string content)
    {
        var file = content == "missing" ? Path.Combine(_dir, "missing.json") : Write(content);
        var log = new StringWriter();

        var options = IldClientOptions.Resolve(Environment(file), log);

        Assert.Equal(new IldClientOptions("http://env.example:5000", "env-token", "env-run", "env-chat"), options);
        Assert.False(string.IsNullOrWhiteSpace(log.ToString()));
    }

    private Func<string, string?> Environment(string? file)
        => name => name == "ILD_MCP_ENV_FILE" ? file : FromEnvironment.GetValueOrDefault(name);

    private string Write(string content)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }
}
