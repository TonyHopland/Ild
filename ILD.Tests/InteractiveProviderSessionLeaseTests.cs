using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using ILD.Api.Services;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace ILD.Tests;

/// <summary>
/// An interactive provider terminal holds its managed agent's in-use lease from
/// before the launch command is resolved until the session ends, on every exit.
/// </summary>
public class InteractiveProviderSessionLeaseTests
{
    private static bool IsUnix => !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static AiProvider Provider(string type, string binaryPath) => new()
    {
        Id = Guid.NewGuid(),
        Name = "test",
        Type = type,
        BaseUrl = "http://localhost",
        Model = "n/a",
        Config = "{\"binaryPath\":\"" + binaryPath + "\"}",
    };

    [Fact]
    public async Task A_running_terminal_holds_the_lease_and_closing_it_releases_it()
    {
        if (!IsUnix) Assert.Skip("PTY backend on Windows requires conhost");

        var usage = new ManagedAgentUsage();
        var (server, client) = WebSocketPair.Create();
        var service = new InteractiveProviderSessionService(NullLogger<InteractiveProviderSessionService>.Instance, usage: usage);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var run = service.RunAsync(server, Provider("pi", "/bin/cat"), 80, 24, cts.Token);
        await client.SendAsync(Encoding.UTF8.GetBytes("hello\n"), WebSocketMessageType.Binary, true, CancellationToken.None);
        var buffer = new byte[4096];
        var seen = new StringBuilder();
        while (!seen.ToString().Contains("hello", StringComparison.Ordinal))
        {
            var received = await client.ReceiveAsync(buffer, cts.Token);
            seen.Append(Encoding.UTF8.GetString(buffer, 0, received.Count));
        }

        Assert.True(usage.IsBusy("pi"));
        Assert.False(usage.IsBusy("opencode"));

        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await run;
        Assert.False(usage.IsBusy("pi"));
    }

    [Fact]
    public async Task A_terminal_that_cannot_launch_releases_the_lease()
    {
        if (!IsUnix) Assert.Skip("PTY sessions are Unix-only here");

        var usage = new ManagedAgentUsage();
        var (server, client) = WebSocketPair.Create();
        var service = new InteractiveProviderSessionService(NullLogger<InteractiveProviderSessionService>.Instance, usage: usage);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var run = service.RunAsync(server, Provider("pi", "/no/such/binary/here"), 80, 24, cts.Token);
        var buffer = new byte[1024];
        WebSocketReceiveResult result;
        do { result = await client.ReceiveAsync(buffer, cts.Token); }
        while (result.MessageType != WebSocketMessageType.Close);
        await run;

        Assert.False(usage.IsBusy("pi"));
    }

    [Fact]
    public async Task A_cancelled_terminal_releases_the_lease()
    {
        if (!IsUnix) Assert.Skip("PTY backend on Windows requires conhost");

        var usage = new ManagedAgentUsage();
        var (server, client) = WebSocketPair.Create();
        var service = new InteractiveProviderSessionService(NullLogger<InteractiveProviderSessionService>.Instance, usage: usage);
        using var cts = new CancellationTokenSource();

        var run = service.RunAsync(server, Provider("opencode", "/bin/cat"), 80, 24, cts.Token);
        await client.SendAsync(Encoding.UTF8.GetBytes("x\n"), WebSocketMessageType.Binary, true, CancellationToken.None);
        var buffer = new byte[4096];
        await client.ReceiveAsync(buffer, TestContext.Current.CancellationToken);
        Assert.True(usage.IsBusy("opencode"));

        cts.Cancel();
        try { await run.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken); }
        catch (OperationCanceledException) { }

        Assert.False(usage.IsBusy("opencode"));
    }
}
