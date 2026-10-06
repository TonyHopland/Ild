using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using ILD.Api.Services;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Data.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace ILD.Tests;

/// <summary>
/// Integration tests for <see cref="InteractiveProviderSessionService"/>.
/// Exercises the full path: real PTY allocation via Porta.Pty, real bytes
/// flowing through a paired in-memory WebSocket. Skipped on Windows because
/// the test relies on /bin/cat as a known-good interactive binary.
/// </summary>
public class InteractiveProviderSessionServiceTests
{
    private static bool IsUnix => !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [Fact]
    public async Task BridgesBytesBetweenWebSocketAndPty()
    {
        if (!IsUnix) return; // PTY backend on Windows requires conhost; not the focus here.

        var (server, client) = WebSocketPair.Create();
        var service = new InteractiveProviderSessionService(NullLogger<InteractiveProviderSessionService>.Instance);

        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "test",
            Type = "cat",
            BaseUrl = "http://localhost",
            Model = "n/a",
            Config = "{\"binaryPath\":\"/bin/cat\"}",
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = service.RunAsync(server, provider, initialCols: 80, initialRows: 24, cancellationToken: cts.Token);

        // /bin/cat in a PTY echoes input back when line buffering is enabled.
        var input = Encoding.UTF8.GetBytes("hello\n");
        await client.SendAsync(input, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);

        var output = await ReadUntilAsync(client, "hello", TimeSpan.FromSeconds(5));
        Assert.Contains("hello", output);

        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await runTask;
    }

    [Fact]
    public async Task ClosesGracefullyWhenBinaryMissing()
    {
        if (!IsUnix) return;

        var (server, client) = WebSocketPair.Create();
        var service = new InteractiveProviderSessionService(NullLogger<InteractiveProviderSessionService>.Instance);

        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "test",
            Type = "missing",
            BaseUrl = "http://localhost",
            Model = "n/a",
            Config = "{\"binaryPath\":\"/no/such/binary/here\"}",
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = service.RunAsync(server, provider, initialCols: 80, initialRows: 24, cancellationToken: cts.Token);

        // A bogus binaryPath no longer reaches the PTY: the pre-flight check
        // resolves it as missing and sends an actionable error before closing.
        var buffer = new byte[1024];
        var received = new StringBuilder();
        WebSocketReceiveResult result;
        do
        {
            result = await client.ReceiveAsync(buffer, cts.Token);
            if (result.Count > 0)
                received.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        }
        while (result.MessageType != WebSocketMessageType.Close);

        await runTask;
        Assert.Contains("was not found", received.ToString());
    }

    [Fact]
    public async Task Launches_the_cli_with_exactly_the_saved_extra_arguments_and_says_so_first()
    {
        if (!IsUnix) return;

        // printf prints each argument after the format on its own <<line>>, so the
        // output shows the argv the terminal launched, token by token.
        var extraArgs = "'<<%s>>\\n' --effort high \"be brief\" '$(x);|&>' `y`";
        var (text, _) = await RunToCloseAsync("/usr/bin/printf", extraArgs);

        var lines = Lines(text);
        var cliLines = lines.Where(l => l.StartsWith("<<", StringComparison.Ordinal)).ToList();
        Assert.Equal(["<<--effort>>", "<<high>>", "<<be brief>>", "<<$(x);|&>>>", "<<`y`>>"], cliLines);

        var launched = Assert.Single(lines, l => l.Contains("Launched with: ", StringComparison.Ordinal));
        var shown = launched[(launched.IndexOf("Launched with: ", StringComparison.Ordinal) + "Launched with: ".Length)..];
        var reparsed = ExtraCliArgs.Tokenize(shown);
        Assert.Null(reparsed.Error);
        Assert.Equal(["<<%s>>\\n", "--effort", "high", "be brief", "$(x);|&>", "`y`"], reparsed.Tokens);
        Assert.True(lines.IndexOf(launched) < lines.IndexOf(cliLines[0]), "the launch line comes before the CLI's output");
    }

    [Fact]
    public async Task Shows_what_a_cli_that_exits_at_once_wrote_and_its_exit_code_then_closes_normally()
    {
        if (!IsUnix) return;

        var (text, closeStatus) = await RunToCloseAsync("/bin/sh", "-c \"echo boom >&2; exit 3\"");

        // The CLI's own line, not the launch line that quotes its arguments.
        var boom = Regex.Match(text, @"^boom\r?$", RegexOptions.Multiline);
        Assert.True(boom.Success, $"the CLI's error output was lost: {text}");
        Assert.Matches(new Regex(@"exit[^\n]*\b3\b", RegexOptions.IgnoreCase), text[boom.Index..]);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, closeStatus);
    }

    [Fact]
    public async Task Launches_with_no_extra_arguments_and_no_launch_line_when_none_are_saved()
    {
        if (!IsUnix) return;

        var (text, _) = await RunToCloseAsync("/bin/echo", extraArgs: null);

        Assert.DoesNotContain("Launched with", text);
    }

    [Fact]
    public async Task Ignores_saved_extra_arguments_that_do_not_split_and_says_so()
    {
        if (!IsUnix) return;

        var (text, _) = await RunToCloseAsync("/bin/echo", "marker-token \"unbalanced");

        var lines = Lines(text);
        Assert.Contains(lines, l => l.Contains("ignored", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("Launched with", text);
        Assert.DoesNotContain(lines, l => l.Contains("marker-token", StringComparison.Ordinal)
            && !l.Contains("ignored", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Opens the provider terminal on <paramref name="binaryPath"/> with the saved
    /// <paramref name="extraArgs"/>, and reads everything it sends until it closes.
    /// </summary>
    private static async Task<(string Text, WebSocketCloseStatus? CloseStatus)> RunToCloseAsync(
        string binaryPath, string? extraArgs)
    {
        var (server, client) = WebSocketPair.Create();
        var service = new InteractiveProviderSessionService(NullLogger<InteractiveProviderSessionService>.Instance);
        var config = new Dictionary<string, string> { ["binaryPath"] = binaryPath };
        if (extraArgs is not null) config["extraArgs"] = extraArgs;
        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "test",
            Type = "cat",
            BaseUrl = "http://localhost",
            Model = "n/a",
            Config = System.Text.Json.JsonSerializer.Serialize(config),
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = service.RunAsync(server, provider, initialCols: 200, initialRows: 24, cancellationToken: cts.Token);

        var received = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await client.ReceiveAsync(buffer, cts.Token);
            if (result.MessageType != WebSocketMessageType.Close)
                received.Write(buffer, 0, result.Count);
        }
        while (result.MessageType != WebSocketMessageType.Close);

        await runTask;
        return (Encoding.UTF8.GetString(received.ToArray()), result.CloseStatus);
    }

    private static List<string> Lines(string text)
        => text.Replace("\r", "").Split('\n').ToList();

    [Fact]
    public void ResolveLaunchCommand_prefers_explicit_binaryPath()
    {
        var provider = new AiProvider { Type = "pi", Config = "{\"binaryPath\":\"/custom/pi\"}" };
        Assert.Equal("/custom/pi", InteractiveProviderSessionService.ResolveLaunchCommand(provider, "/data"));
    }

    [Fact]
    public void ResolveLaunchCommand_falls_back_to_agent_command_when_not_installed()
    {
        // claude-code ships as `claude`; with no /data install it resolves to the
        // bare command (which only works if separately on PATH).
        var dataRoot = Path.Combine(Path.GetTempPath(), "ild-iss-" + Guid.NewGuid().ToString("N"));
        var provider = new AiProvider { Type = "claude-code", Config = null };

        Assert.Equal("claude", InteractiveProviderSessionService.ResolveLaunchCommand(provider, dataRoot));
    }

    [Fact]
    public void ResolveLaunchCommand_prefers_the_data_install_when_present()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "ild-iss-" + Guid.NewGuid().ToString("N"));
        try
        {
            var agent = ManagedAgentCatalog.Pi;
            var versionId = "v1";
            var versionDir = ManagedAgentInstall.VersionDir(dataRoot, agent, versionId);
            var binary = ManagedAgentInstall.BinaryIn(versionDir, agent);
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
            File.WriteAllText(binary, "#!/bin/sh\n");
            Directory.CreateDirectory(ManagedAgentInstall.AgentRoot(dataRoot, agent));
            File.WriteAllText(ManagedAgentInstall.PointerFile(dataRoot, agent), versionId);

            var provider = new AiProvider { Type = "pi", Config = null };
            var resolved = InteractiveProviderSessionService.ResolveLaunchCommand(provider, dataRoot);

            Assert.Equal(binary, resolved);
        }
        finally
        {
            try { Directory.Delete(dataRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ResolveLaunchCommand_uses_type_for_unknown_provider()
    {
        var provider = new AiProvider { Type = "cat", Config = null };
        Assert.Equal("cat", InteractiveProviderSessionService.ResolveLaunchCommand(provider, "/data"));
    }

    [Fact]
    public void BuildUnavailableMessage_points_managed_agent_to_install_button()
    {
        var provider = new AiProvider { Type = "pi" };
        var message = InteractiveProviderSessionService.BuildUnavailableMessage(provider, "pi");

        Assert.Contains("Pi isn't installed", message);
        Assert.Contains("AI Provider page", message);
    }

    [Fact]
    public void BuildUnavailableMessage_flags_misconfigured_binaryPath_for_other_types()
    {
        var provider = new AiProvider { Type = "cat" };
        var message = InteractiveProviderSessionService.BuildUnavailableMessage(provider, "/no/such/bin");

        Assert.Contains("/no/such/bin", message);
        Assert.Contains("binaryPath", message);
    }

    private static async Task<string> ReadUntilAsync(WebSocket socket, string needle, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var sb = new StringBuilder();
        var buffer = new byte[4096];
        while (!cts.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer, cts.Token);
            }
            catch (OperationCanceledException) { break; }
            if (result.MessageType == WebSocketMessageType.Close) break;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (sb.ToString().Contains(needle, StringComparison.Ordinal)) break;
        }
        return sb.ToString();
    }
}
