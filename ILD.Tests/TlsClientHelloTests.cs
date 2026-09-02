using System.Text;
using ILD.Core.Services.Implementations.Network;

namespace ILD.Tests;

public class TlsClientHelloTests
{
    /// <summary>A minimal but well-formed TLS 1.2 ClientHello record.</summary>
    internal static byte[] Build(string? serverName, bool extraExtensionFirst = false)
    {
        var hello = new List<byte> { 0x03, 0x03 };
        hello.AddRange(new byte[32]);                 // random
        hello.Add(0);                                 // session id length
        hello.AddRange(new byte[] { 0x00, 0x04, 0x13, 0x01, 0x00, 0x2f }); // two cipher suites
        hello.AddRange(new byte[] { 0x01, 0x00 });    // null compression

        var extensions = new List<byte>();
        if (extraExtensionFirst)
            extensions.AddRange(new byte[] { 0x00, 0x0d, 0x00, 0x02, 0x04, 0x03 }); // signature_algorithms
        if (serverName is not null)
        {
            var name = Encoding.ASCII.GetBytes(serverName);
            var list = new List<byte> { 0x00 };
            list.AddRange(BigEndian16(name.Length));
            list.AddRange(name);
            var body = new List<byte>(BigEndian16(list.Count));
            body.AddRange(list);
            extensions.AddRange(new byte[] { 0x00, 0x00 });
            extensions.AddRange(BigEndian16(body.Count));
            extensions.AddRange(body);
        }
        hello.AddRange(BigEndian16(extensions.Count));
        hello.AddRange(extensions);

        var handshake = new List<byte> { 0x01, (byte)(hello.Count >> 16), (byte)(hello.Count >> 8), (byte)hello.Count };
        handshake.AddRange(hello);

        var record = new List<byte> { 0x16, 0x03, 0x01 };
        record.AddRange(BigEndian16(handshake.Count));
        record.AddRange(handshake);
        return record.ToArray();
    }

    private static byte[] BigEndian16(int value) => new[] { (byte)(value >> 8), (byte)value };

    [Fact]
    public void Reads_the_server_name_out_of_a_client_hello()
    {
        var record = Build("api.example.com", extraExtensionFirst: true);

        Assert.True(TlsClientHello.StartsHandshake(record));
        Assert.Equal(record.Length, TlsClientHello.RecordLength(record));
        Assert.Equal("api.example.com", TlsClientHello.ReadServerName(record));
    }

    [Fact]
    public void A_hello_without_sni_yields_no_name()
    {
        Assert.Null(TlsClientHello.ReadServerName(Build(null)));
    }

    [Fact]
    public void Plain_http_and_truncated_bytes_are_not_a_hello()
    {
        var http = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n\r\n");
        Assert.False(TlsClientHello.StartsHandshake(http));
        Assert.Null(TlsClientHello.ReadServerName(http));

        var truncated = Build("api.example.com")[..20];
        Assert.Null(TlsClientHello.ReadServerName(truncated));
        Assert.Null(TlsClientHello.RecordLength(new byte[] { 0x16, 0x03 }));
    }
}
