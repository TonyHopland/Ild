using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using ILD.Api.Services;
using ILD.Core.Services.Implementations.PackageFeeds;
using Microsoft.Extensions.Logging.Abstractions;

namespace ILD.Tests;

/// <summary>
/// A worktree terminal gets the feeds of the run's repository the way the run's own
/// processes do, and its credential file lives exactly as long as the session. A
/// real shell in a real PTY reports what it was started with.
/// </summary>
public sealed class InteractiveShellSessionPackageFeedTests : IDisposable
{
    private readonly string _worktree = Directory.CreateTempSubdirectory("ild-shell-feeds-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_worktree, recursive: true); } catch { /* best effort */ }
    }

    // The typed command is echoed back first, with its format string where the
    // value goes; what the shell printed is the last match. END<done> only
    // appears once the shell has run the whole line.
    private const string Report =
        "printf 'CFG<%s>\\n' \"${NPM_CONFIG_USERCONFIG-unset}\"; "
        + "printf 'NUGET<%s>\\n' \"${VSS_NUGET_EXTERNAL_FEED_ENDPOINTS-unset}\"; "
        + "[ -f \"$NPM_CONFIG_USERCONFIG\" ] && printf 'FILE<%s>\\n' present; printf 'END<%s>\\n' done\n";

    private async Task<string> RunShellAsync(IReadOnlyList<PackageFeedCredential> feeds)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var (server, client) = WebSocketPair.Create();
        var session = new InteractiveShellSessionService(NullLogger<InteractiveShellSessionService>.Instance)
            .RunAsync(server, _worktree, Guid.NewGuid().ToString("N"), 120, 30, feeds, cts.Token);

        // The session closes as soon as the shell exits, which can drop output the
        // shell printed just before; so exit only once the report has arrived.
        await client.SendAsync(Encoding.UTF8.GetBytes(Report), WebSocketMessageType.Binary, endOfMessage: true, cts.Token);
        var output = new StringBuilder();
        var buffer = new byte[4096];
        var exitSent = false;
        while (true)
        {
            var frame = await client.ReceiveAsync(buffer, cts.Token);
            if (frame.MessageType == WebSocketMessageType.Close) break;
            output.Append(Encoding.UTF8.GetString(buffer, 0, frame.Count));
            if (!exitSent && output.ToString().Contains("END<done>", StringComparison.Ordinal))
            {
                exitSent = true;
                await client.SendAsync("exit\n"u8.ToArray(), WebSocketMessageType.Binary, endOfMessage: true, cts.Token);
            }
        }
        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
        await session.WaitAsync(cts.Token);
        return output.ToString();
    }

    private static string Reported(string output, string key)
        => Regex.Matches(output, $@"{key}<([^>\r\n]*)>").Last().Groups[1].Value;

    [Fact]
    public async Task The_shell_gets_the_feeds_and_their_file_goes_with_the_session()
    {
        if (!OperatingSystem.IsLinux()) return;
        var feeds = new[]
        {
            PackageFeedCredentialFilesTests.Feed("company", "https://example-org.pkgs.visualstudio.com/_packaging/company", "shellPAT-44dd"),
        };

        var output = await RunShellAsync(feeds);

        var path = Reported(output, "CFG");
        Assert.True(path.StartsWith('/'), $"the shell got no npm user config: {output}");
        Assert.False(path.StartsWith(_worktree + Path.DirectorySeparatorChar, StringComparison.Ordinal), "the PAT file is inside the worktree");
        Assert.Equal("present", Reported(output, "FILE"));
        Assert.Contains("https://pkgs.dev.azure.com/example-org/_packaging/company/nuget/v3/index.json", Reported(output, "NUGET"));
        Assert.False(File.Exists(path), "the terminal's PAT file outlived the session");
    }

    [Fact]
    public async Task Without_feeds_the_shell_sees_no_feed_variable()
    {
        if (!OperatingSystem.IsLinux()) return;

        var output = await RunShellAsync([]);

        Assert.Equal("unset", Reported(output, "CFG"));
        Assert.Equal("unset", Reported(output, "NUGET"));
    }
}
