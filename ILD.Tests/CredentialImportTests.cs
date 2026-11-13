using ILD.Api.Services;
using ILD.Core.Services.Implementations.ModelGateway;
using Microsoft.Extensions.Logging.Abstractions;

namespace ILD.Tests;

public sealed class CredentialImportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ild-import-").FullName;
    private string Throwaway => Path.Combine(_root, "throwaway");
    private string Store => Path.Combine(_root, "store");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static void Put(string home, string relative, string content)
    {
        var path = Path.Combine(home, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private Task<IReadOnlyList<string>> ImportAsync()
        => CredentialImport.ImportAsync(Throwaway, Store, NullLogger.Instance, TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_login_the_terminal_made_is_imported_into_the_store_readable_by_ild_alone()
    {
        Put(Throwaway, ".claude/.credentials.json", """{"claudeAiOauth":{"accessToken":"a","refreshToken":"r"}}""");
        Put(Throwaway, ".codex/auth.json", """{"tokens":{"access_token":"a"}}""");
        Put(Throwaway, ".copilot/config.json", "// managed\n{\"authTokens\":{\"https://github.com:me\":{\"token\":\"t\"}}}");

        var imported = await ImportAsync();

        Assert.Equal([".claude/.credentials.json", ".codex/auth.json", ".copilot/config.json"], imported);
        var stored = Path.Combine(Store, ".claude", ".credentials.json");
        Assert.Contains("refreshToken", File.ReadAllText(stored));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(stored));
    }

    [Fact]
    public async Task A_file_without_a_login_does_not_replace_the_one_in_the_store()
    {
        Put(Store, ".copilot/config.json", "{\"authTokens\":{\"https://github.com:me\":{\"token\":\"keep\"}}}");
        Put(Throwaway, ".copilot/config.json", "{\"firstLaunchAt\":\"now\"}");
        Put(Store, ".claude/.credentials.json", "keep");
        Put(Throwaway, ".claude/.credentials.json", "not json");

        Assert.Empty(await ImportAsync());

        Assert.Contains("keep", File.ReadAllText(Path.Combine(Store, ".copilot", "config.json")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Store, ".claude", ".credentials.json")));
    }

    [Fact]
    public async Task A_link_planted_where_a_login_belongs_is_not_followed()
    {
        var secret = Path.Combine(_root, "elsewhere.json");
        File.WriteAllText(secret, """{"claudeAiOauth":{"accessToken":"stolen"}}""");
        Directory.CreateDirectory(Path.Combine(Throwaway, ".claude"));
        File.CreateSymbolicLink(Path.Combine(Throwaway, ".claude", ".credentials.json"), secret);

        Assert.Empty(await ImportAsync());
        Assert.False(File.Exists(Path.Combine(Store, ".claude", ".credentials.json")));
    }

    [Fact]
    public async Task Nothing_is_imported_from_a_home_the_terminal_never_wrote_to()
    {
        Directory.CreateDirectory(Throwaway);

        Assert.Empty(await ImportAsync());
    }
    [Fact]
    public async Task A_reader_supplies_the_files_and_only_real_logins_are_kept()
    {
        var files = new Dictionary<string, string>
        {
            [".claude/.credentials.json"] = """{"claudeAiOauth":{"accessToken":"a"}}""",
            [".codex/auth.json"] = "{}",
            [".pi/agent/auth.json"] = "not json",
        };
        var asked = new List<string>();

        var imported = await CredentialImport.ImportAsync((path, _) =>
        {
            asked.Add(path);
            return Task.FromResult(files.GetValueOrDefault(path));
        }, Store, NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Equal([".claude/.credentials.json"], imported);
        Assert.Equal(5, asked.Count);
        Assert.True(File.Exists(Path.Combine(Store, ".claude", ".credentials.json")));
        Assert.False(File.Exists(Path.Combine(Store, ".codex", "auth.json")));
    }

    [Fact]
    public async Task A_reader_that_fails_on_one_file_does_not_stop_the_others()
    {
        var imported = await CredentialImport.ImportAsync(
            (path, _) => path == ".claude/.credentials.json"
                ? throw new InvalidOperationException("boom")
                : Task.FromResult<string?>(path == ".codex/auth.json" ? """{"OPENAI_API_KEY":"k"}""" : null),
            Store, NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Equal([".codex/auth.json"], imported);
    }
}

public class SetupTokenCaptureTests
{
    private const string Token = "sk-ant-oat01-AbCdEf0123456789_-AbCdEf0123456789_-AbCdEf0123456789_-AbCdEf0123456789_-AbCdEf0123456789-Z";

    private static void Feed(SetupTokenCapture capture, string text) => capture.Feed(System.Text.Encoding.UTF8.GetBytes(text));

    [Fact]
    public void The_token_is_taken_from_coloured_output_once_its_line_is_finished()
    {
        var capture = new SetupTokenCapture(160);

        Feed(capture, "\u001b[32m✓ Long-lived authentication token created\u001b[0m\r\n\r\nYour OAuth token:\r\n\r\n");
        Feed(capture, "\u001b[1m" + Token[..50]);
        Assert.Null(capture.Token);
        Feed(capture, Token[50..] + "\u001b[0m\r\n\r\nStore this token securely.\r\n");

        Assert.Equal(Token, capture.Token);
    }

    [Fact]
    public void A_token_wrapped_at_the_terminal_width_is_joined()
    {
        var capture = new SetupTokenCapture(60);

        Feed(capture, $"Your OAuth token:\r\n{Token[..60]}\r\n{Token[60..]}\r\n\r\nStore this token securely.\r\n");

        Assert.Equal(Token, capture.Token);
    }

    [Fact]
    public void Text_after_the_token_is_not_part_of_it()
    {
        var capture = new SetupTokenCapture(60);

        Feed(capture, $"{Token}\r\nStore this token securely.\r\n");

        Assert.Equal(Token, capture.Token);
    }

    [Fact]
    public void Output_without_a_token_finds_none()
    {
        var capture = new SetupTokenCapture(80);

        Feed(capture, "Opening your browser to sign in…\r\nsk-ant-oat01-short\r\n");

        Assert.Null(capture.Token);
    }

}
