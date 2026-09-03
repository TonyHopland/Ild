using System.Buffers.Binary;

namespace ILD.Core.Services.Implementations.Network;

/// <summary>
/// Reads the server name out of a TLS ClientHello without touching anything
/// else in the handshake — the proxy needs a hostname to judge and log a flow
/// that was redirected to it transparently, and SNI is the only place a TLS
/// connection carries one in the clear.
/// </summary>
public static class TlsClientHello
{
    private const byte HandshakeRecord = 0x16;
    private const byte ClientHelloType = 0x01;
    private const ushort ServerNameExtension = 0x0000;

    /// <summary>Whether the first byte of a connection announces a TLS handshake record.</summary>
    public static bool StartsHandshake(ReadOnlySpan<byte> data) => data.Length > 0 && data[0] == HandshakeRecord;

    /// <summary>
    /// The number of bytes the first TLS record spans (header included), or
    /// <c>null</c> when fewer than five bytes have arrived.
    /// </summary>
    public static int? RecordLength(ReadOnlySpan<byte> data)
        => data.Length < 5 ? null : 5 + BinaryPrimitives.ReadUInt16BigEndian(data.Slice(3, 2));

    /// <summary>
    /// The SNI host name of a complete ClientHello record, or <c>null</c> when the
    /// bytes are not a ClientHello or carry no server name.
    /// </summary>
    public static string? ReadServerName(ReadOnlySpan<byte> record)
    {
        try
        {
            if (record.Length < 5 || record[0] != HandshakeRecord) return null;
            var body = record.Slice(5, Math.Min(record.Length - 5, BinaryPrimitives.ReadUInt16BigEndian(record.Slice(3, 2))));

            if (body.Length < 4 || body[0] != ClientHelloType) return null;
            var helloLength = (body[1] << 16) | (body[2] << 8) | body[3];
            var hello = body.Slice(4, Math.Min(helloLength, body.Length - 4));

            var pos = 2 + 32;                       // version + random
            pos += 1 + hello[pos];                  // session id
            pos += 2 + BinaryPrimitives.ReadUInt16BigEndian(hello.Slice(pos, 2)); // cipher suites
            pos += 1 + hello[pos];                  // compression methods
            if (pos + 2 > hello.Length) return null;

            var extensionsLength = BinaryPrimitives.ReadUInt16BigEndian(hello.Slice(pos, 2));
            pos += 2;
            var end = Math.Min(pos + extensionsLength, hello.Length);

            while (pos + 4 <= end)
            {
                var type = BinaryPrimitives.ReadUInt16BigEndian(hello.Slice(pos, 2));
                var length = BinaryPrimitives.ReadUInt16BigEndian(hello.Slice(pos + 2, 2));
                pos += 4;
                if (type == ServerNameExtension)
                    return ReadServerNameList(hello.Slice(pos, Math.Min(length, end - pos)));
                pos += length;
            }
            return null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static string? ReadServerNameList(ReadOnlySpan<byte> extension)
    {
        var listLength = BinaryPrimitives.ReadUInt16BigEndian(extension.Slice(0, 2));
        var pos = 2;
        var end = Math.Min(2 + listLength, extension.Length);
        while (pos + 3 <= end)
        {
            var nameType = extension[pos];
            var nameLength = BinaryPrimitives.ReadUInt16BigEndian(extension.Slice(pos + 1, 2));
            pos += 3;
            if (nameType == 0 && pos + nameLength <= end)
                return System.Text.Encoding.ASCII.GetString(extension.Slice(pos, nameLength));
            pos += nameLength;
        }
        return null;
    }
}
