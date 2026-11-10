using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ILD.Core.Services.RunHosts.Isolated;

namespace ILD.Tests;

/// <summary>
/// The forwards and hosts scripts run by a real shell with a real socat: a TLS client that checks the host name
/// reaches a server whose certificate is for the forward's name, through a proxy that only sees <c>CONNECT name:port</c>.
/// </summary>
public sealed class ExecRunHostForwardsRealShellTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ild-forwards-real-").FullName;

    public void Dispose()
    {
        var state = Path.Combine(_dir, "state");
        if (Directory.Exists(state))
            foreach (var pid in Directory.GetFiles(state, "*.pid"))
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

    private void Bash(string script, params string[] arguments)
    {
        var psi = new ProcessStartInfo("bash") { RedirectStandardInput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-c", script, "bash" }.Concat(arguments)) psi.ArgumentList.Add(a);
        psi.Environment["ILD_FORWARDS_DIR"] = Path.Combine(_dir, "state");
        psi.Environment["ILD_HOSTS_FILE"] = Path.Combine(_dir, "hosts");
        using var process = Process.Start(psi)!;
        process.StandardInput.Close();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error.Result);
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    [Fact]
    public async Task A_tls_client_that_checks_the_host_name_reaches_the_server_through_the_proxy_by_the_forwards_name()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && Has("setsid") && Has("socat"), "needs Linux with bash, setsid and socat");
        var ct = TestContext.Current.CancellationToken;
        var destinationPort = FreePort();
        var localPort = FreePort();

        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=postgres", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("postgres");
        request.CertificateExtensions.Add(names.Build());
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);

        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        using var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        var connects = new List<string>();
        var serving = ServeAsync(server, certificate, ct);
        var proxying = ProxyAsync(proxy, ((IPEndPoint)server.LocalEndpoint).Port, connects, ct);

        File.WriteAllText(Path.Combine(_dir, "hosts"), "127.0.0.1 localhost\n");
        var address = ForwardAddresses.Allocate(["postgres"])["postgres"];
        Bash(ExecRunHost.HostsScript, address, "postgres");
        Bash(ExecRunHost.ForwardsScript, "127.0.0.1", ((IPEndPoint)proxy.LocalEndpoint).Port.ToString(), localPort.ToString(), "postgres", destinationPort.ToString(), address);

        // What a client sees: the name resolves through the hosts file to the address, and the port is the destination's.
        var resolved = File.ReadLines(Path.Combine(_dir, "hosts")).Single(l => l.EndsWith(" postgres", StringComparison.Ordinal)).Split(' ')[0];
        Assert.Equal(address, resolved);

        Assert.Equal("hello", await ConnectAsync(resolved, destinationPort, "postgres", ct));
        Assert.Equal(["postgres:" + destinationPort], connects);
        // The same server under another name fails verification, so the check above does decide something.
        await Assert.ThrowsAsync<System.Security.Authentication.AuthenticationException>(() => ConnectAsync(resolved, destinationPort, "other", ct));
        // The local port form still reaches it.
        Assert.Equal("hello", await ConnectAsync("127.0.0.1", localPort, "postgres", ct));

        proxy.Stop();
        server.Stop();
        await Task.WhenAll(serving.ContinueWith(_ => { }, CancellationToken.None), proxying.ContinueWith(_ => { }, CancellationToken.None));
    }

    private static async Task<string> ConnectAsync(string address, int port, string targetHost, CancellationToken ct)
    {
        using var client = new TcpClient();
        for (var attempt = 0; ; attempt++)
        {
            try { await client.ConnectAsync(IPAddress.Parse(address), port, ct); break; }
            catch (SocketException) when (attempt < 100) { await Task.Delay(50, ct); }
        }
        // A self-signed certificate is its own root; the host name is what is under test, so only a mismatch fails.
        using var tls = new SslStream(client.GetStream(), false, (_, _, _, errors) => (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0);
        await tls.AuthenticateAsClientAsync(targetHost);
        var buffer = new byte[5];
        await tls.ReadExactlyAsync(buffer, ct);
        return Encoding.ASCII.GetString(buffer);
    }

    private static async Task ServeAsync(TcpListener server, X509Certificate2 certificate, CancellationToken ct)
    {
        while (true)
        {
            using var client = await server.AcceptTcpClientAsync(ct);
            try
            {
                using var tls = new SslStream(client.GetStream());
                await tls.AuthenticateAsServerAsync(certificate);
                await tls.WriteAsync("hello"u8.ToArray(), ct);
                await tls.FlushAsync(ct);
                await tls.ReadExactlyAsync(new byte[1], ct); // waits for the client to go
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or System.Security.Authentication.AuthenticationException) { }
        }
    }

    private static async Task ProxyAsync(TcpListener proxy, int serverPort, List<string> connects, CancellationToken ct)
    {
        while (true)
        {
            var client = await proxy.AcceptTcpClientAsync(ct);
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var head = new StringBuilder();
                    var one = new byte[1];
                    while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(one, ct) == 1) head.Append((char)one[0]);
                    lock (connects) connects.Add(head.ToString().Split(' ')[1]);
                    using var upstream = new TcpClient();
                    await upstream.ConnectAsync(IPAddress.Loopback, serverPort, ct);
                    await stream.WriteAsync("HTTP/1.0 200 Connection established\r\n\r\n"u8.ToArray(), ct);
                    var up = upstream.GetStream();
                    try { await Task.WhenAny(stream.CopyToAsync(up, ct), up.CopyToAsync(stream, ct)); }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
                }
            }, ct);
        }
    }
}
