using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ILD.Core.Services.RunHosts.Isolated;

namespace ILD.Tests;

/// <summary>
/// The egress relay script run by a real shell with a real socat: a client that sends no proxy credential reaches the
/// proxy through the relay with the launch's credential on its request.
/// </summary>
public sealed class ExecRunHostEgressRelayRealShellTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ild-egress-relay-").FullName;

    public void Dispose()
    {
        foreach (var pid in Directory.GetFiles(_dir, "*.pid"))
            try { Process.GetProcessById(int.Parse(File.ReadAllText(pid).Trim())).Kill(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException) { }
        Directory.Delete(_dir, recursive: true);
    }

    private static bool Has(string tool)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("bash", ["-c", $"command -v {tool}"]) { RedirectStandardOutput = true })!;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>Runs the relay script as a launch's wrapper does and returns the proxy the launch is left pointed at.</summary>
    private string ProxyAfterRelay(string proxyUrl)
    {
        var psi = new ProcessStartInfo("sh") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-c", ExecRunHost.EgressRelayScript + "\nprintf '%s %s' \"$HTTP_PROXY\" \"$https_proxy\"" }) psi.ArgumentList.Add(a);
        psi.Environment["HTTP_PROXY"] = proxyUrl;
        psi.Environment["https_proxy"] = proxyUrl;
        psi.Environment["ILD_EGRESS_RELAY_DIR"] = _dir;
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error.Result);
        var proxies = output.Result.Split(' ');
        Assert.Equal(proxies[0], proxies[1]);
        return proxies[0];
    }

    [Fact]
    public async Task A_client_that_sends_no_credential_reaches_the_proxy_with_the_launchs_through_a_relay_that_the_next_launch_reuses()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && Has("setsid") && Has("socat"), "needs Linux with bash, setsid and socat");
        var ct = TestContext.Current.CancellationToken;
        using var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        var heads = new List<string>();
        var proxying = AnswerAsync(proxy, heads, ct);
        var upstream = $"http://ild:to-k_en.sig@127.0.0.1:{((IPEndPoint)proxy.LocalEndpoint).Port}";

        var relay = new Uri(ProxyAfterRelay(upstream));

        Assert.Equal("127.0.0.1", relay.Host);
        Assert.Equal(string.Empty, relay.UserInfo);
        Assert.Equal("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok", await RequestAsync(relay.Port, "GET http://site.example/ HTTP/1.1\r\nHost: site.example\r\n\r\n", ct));
        Assert.Equal(
            $"GET http://site.example/ HTTP/1.1\r\nProxy-Authorization: Basic {Convert.ToBase64String(Encoding.ASCII.GetBytes("ild:to-k_en.sig"))}\r\nHost: site.example\r\n\r\n",
            heads.Single(h => h.StartsWith("GET", StringComparison.Ordinal)));

        Assert.Equal(relay, new Uri(ProxyAfterRelay(upstream)));
        Assert.Single(Directory.GetFiles(_dir, "*.pid"));

        // Another launch's credential gets a relay of its own.
        Assert.NotEqual(relay, new Uri(ProxyAfterRelay(upstream.Replace("to-k_en.sig", "other.sig", StringComparison.Ordinal))));

        proxy.Stop();
        await proxying.ContinueWith(_ => { }, CancellationToken.None);
    }

    [Fact]
    public void A_proxy_url_without_ilds_credential_is_left_alone()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "needs Linux with a shell");

        Assert.Equal("http://proxy.example:3128", ProxyAfterRelay("http://proxy.example:3128"));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    private static async Task<string> RequestAsync(int port, string request, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, ct);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        return await reader.ReadToEndAsync(ct);
    }

    private static async Task AnswerAsync(TcpListener proxy, List<string> heads, CancellationToken ct)
    {
        while (true)
        {
            using var client = await proxy.AcceptTcpClientAsync(ct);
            var stream = client.GetStream();
            var head = new StringBuilder();
            var one = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(one, ct) == 1) head.Append((char)one[0]);
            lock (heads) heads.Add(head.ToString());
            if (head.Length > 0) await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray(), ct);
        }
    }
}
