using System.Net.Sockets;
using System.Text;

namespace ILD.Core.Services.Implementations.Network;

/// <summary>
/// The two directions of one plain-HTTP connection through <see cref="EgressProxy"/>,
/// which carries exactly one request.
///
/// <para>
/// Toward the upstream only the first request's body passes — framed by its
/// Content-Length or chunked encoding — so a second request the client sends
/// anyway, still in absolute form and carrying its proxy credentials, never
/// reaches the upstream unjudged. Toward the client the final response head is
/// rewritten to say <c>Connection: close</c>, without its hop-by-hop headers, so
/// the client opens a new connection for its next request. The body after it is
/// relayed as it arrives, untouched.
/// </para>
///
/// <para>
/// A <c>101 Switching Protocols</c> to a request that asked for an upgrade
/// leaves the connection as the upstream answered it: both directions relay raw
/// from there on.
/// </para>
/// </summary>
internal sealed class PlainHttpExchange
{
    private readonly EgressProxy.ProxyRequest _request;
    private readonly ArraySegment<byte> _head;
    private readonly RequestBody _body;
    private readonly TaskCompletionSource<bool> _switched = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PlainHttpExchange(EgressProxy.ProxyRequest request, ArraySegment<byte> head)
    {
        _request = request;
        _head = head;
        _body = new RequestBody(request.ChunkedBody, request.ContentLength);
    }

    /// <summary>
    /// Client to upstream: the body read along with the head, then the rest of
    /// it, and nothing after it unless the upstream switched protocols.
    /// </summary>
    public async Task CopyRequestAsync(NetworkStream client, NetworkStream upstream, CancellationToken ct)
    {
        var buffer = new byte[81920];
        ReadOnlyMemory<byte> pending = _head.AsMemory(_request.BodyStart);
        while (true)
        {
            var bodyBytes = _body.Take(pending.Span);
            if (bodyBytes > 0)
                await upstream.WriteAsync(pending[..bodyBytes], ct).ConfigureAwait(false);
            pending = pending[bodyBytes..];
            if (_body.Complete) break;

            var read = await client.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) return;
            pending = buffer.AsMemory(0, read);
        }

        if (await _switched.Task.WaitAsync(ct).ConfigureAwait(false))
        {
            if (!pending.IsEmpty)
                await upstream.WriteAsync(pending, ct).ConfigureAwait(false);
            await client.CopyToAsync(upstream, ct).ConfigureAwait(false);
            return;
        }

        // Read to the client's EOF rather than stop reading: closing a socket with
        // unread data resets it, which can cut off a response not yet read.
        while (await client.ReadAsync(buffer, ct).ConfigureAwait(false) > 0) { }
    }

    /// <summary>
    /// Upstream to client: interim heads unchanged, the final head rewritten to
    /// close, then the body as it arrives. An upstream that sends no valid head
    /// gets the client a <c>502</c> and ends the relay.
    /// </summary>
    public async Task CopyResponseAsync(NetworkStream upstream, NetworkStream client, CancellationToken ct)
    {
        try
        {
            var buffer = new byte[EgressProxy.MaxHeadBytes];
            var filled = 0;
            while (true)
            {
                int headLength;
                while ((headLength = buffer.AsSpan(0, filled).IndexOf("\r\n\r\n"u8)) < 0)
                {
                    var read = filled < buffer.Length ? await upstream.ReadAsync(buffer.AsMemory(filled), ct).ConfigureAwait(false) : 0;
                    if (read == 0) throw await BadGatewayAsync(client, ct).ConfigureAwait(false);
                    filled += read;
                }
                headLength += 4;

                var status = StatusCode(buffer.AsSpan(0, headLength));
                // A 101 the request never asked for would hand the upstream raw
                // relay of whatever the client sends next, credentials included.
                if (status is null || (status == 101 && !_request.AsksUpgrade))
                    throw await BadGatewayAsync(client, ct).ConfigureAwait(false);

                if (status is >= 100 and < 200 and not 101)
                {
                    await client.WriteAsync(buffer.AsMemory(0, headLength), ct).ConfigureAwait(false);
                    buffer.AsSpan(headLength, filled - headLength).CopyTo(buffer);
                    filled -= headLength;
                    continue;
                }

                if (status == 101)
                {
                    await client.WriteAsync(buffer.AsMemory(0, filled), ct).ConfigureAwait(false);
                    _switched.TrySetResult(true);
                }
                else
                {
                    await client.WriteAsync(RewriteFinalHead(buffer.AsSpan(0, headLength)), ct).ConfigureAwait(false);
                    if (filled > headLength)
                        await client.WriteAsync(buffer.AsMemory(headLength, filled - headLength), ct).ConfigureAwait(false);
                    _switched.TrySetResult(false);
                }
                await upstream.CopyToAsync(client, ct).ConfigureAwait(false);
                return;
            }
        }
        finally
        {
            _switched.TrySetResult(false);
        }
    }

    /// <summary>Answers the client 502 and returns the error that ends the relay.</summary>
    private async Task<InvalidDataException> BadGatewayAsync(NetworkStream client, CancellationToken ct)
    {
        await EgressProxy.WriteResponseAsync(client, "502 Bad Gateway",
            $"The egress proxy got no valid response head from {_request.Host}:{_request.Port}.", ct).ConfigureAwait(false);
        return new InvalidDataException($"No valid response head from {_request.Host}:{_request.Port}");
    }

    /// <summary>The status code of an <c>HTTP/1.x NNN[ reason]</c> status line, or null for anything else.</summary>
    private static int? StatusCode(ReadOnlySpan<byte> head)
    {
        var line = head[..head.IndexOf("\r\n"u8)];
        if (line.Length < 12 || !line.StartsWith("HTTP/1."u8) || !char.IsAsciiDigit((char)line[7]) || line[8] != ' ')
            return null;
        if (line.Length > 12 && line[12] != ' ') return null;

        var code = 0;
        foreach (var digit in line.Slice(9, 3))
        {
            if (!char.IsAsciiDigit((char)digit)) return null;
            code = code * 10 + (digit - '0');
        }
        return code;
    }

    /// <summary>
    /// The head minus Connection, Keep-Alive, Proxy-Connection and every header a
    /// Connection header names (RFC 9110 §7.6.1), plus <c>Connection: close</c>.
    /// Latin-1 both ways keeps every other line byte for byte.
    /// </summary>
    private static byte[] RewriteFinalHead(ReadOnlySpan<byte> head)
    {
        var lines = Encoding.Latin1.GetString(head[..^4]).Split("\r\n");
        var fields = new List<(string Name, string Text)>();
        foreach (var line in lines.Skip(1))
        {
            if (line.Length > 0 && line[0] is ' ' or '\t' && fields.Count > 0)
            {
                fields[^1] = (fields[^1].Name, fields[^1].Text + "\r\n" + line);
                continue;
            }
            var colon = line.IndexOf(':');
            fields.Add((colon > 0 ? line[..colon].Trim() : string.Empty, line));
        }

        var hopByHop = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Connection", "Keep-Alive", "Proxy-Connection" };
        foreach (var (_, text) in fields.Where(f => f.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase)))
            hopByHop.UnionWith(text[(text.IndexOf(':') + 1)..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

        var rewritten = new StringBuilder(lines[0]).Append("\r\n");
        foreach (var (name, text) in fields)
            if (!hopByHop.Contains(name))
                rewritten.Append(text).Append("\r\n");
        rewritten.Append("Connection: close\r\nProxy-Connection: close\r\n\r\n");
        return Encoding.Latin1.GetBytes(rewritten.ToString());
    }

    /// <summary>
    /// Tracks where a request body ends without holding any of it: a countdown
    /// for Content-Length, a byte-at-a-time walk of the chunked encoding
    /// (RFC 9112 §7.1) otherwise. Malformed chunked framing throws.
    /// </summary>
    private sealed class RequestBody
    {
        private const int MaxSizeDigits = 16;

        private readonly bool _chunked;
        private long _remainingLength;
        private Chunk _state = Chunk.SizeStart;
        private ulong _chunkSize;
        private int _sizeDigits;

        public RequestBody(bool chunked, long contentLength)
        {
            _chunked = chunked;
            _remainingLength = contentLength;
            if (!chunked && contentLength == 0) _state = Chunk.Done;
        }

        public bool Complete => _state == Chunk.Done;

        /// <summary>How many leading bytes of <paramref name="data"/> belong to the body.</summary>
        public int Take(ReadOnlySpan<byte> data)
        {
            if (Complete) return 0;
            if (!_chunked)
            {
                var taken = (int)Math.Min(_remainingLength, data.Length);
                _remainingLength -= taken;
                if (_remainingLength == 0) _state = Chunk.Done;
                return taken;
            }

            var i = 0;
            while (i < data.Length && _state != Chunk.Done)
            {
                if (_state == Chunk.Data)
                {
                    var run = (int)Math.Min(_chunkSize, (ulong)(data.Length - i));
                    i += run;
                    _chunkSize -= (ulong)run;
                    if (_chunkSize == 0) _state = Chunk.DataCr;
                    continue;
                }
                Step(data[i++]);
            }
            return i;
        }

        private void Step(byte b)
        {
            _state = (_state, b) switch
            {
                (Chunk.SizeStart or Chunk.Size, _) when HexValue(b) is { } digit => AddSizeDigit(digit),
                (Chunk.Size or Chunk.Extension, (byte)'\r') => Chunk.SizeLf,
                (Chunk.Size or Chunk.Extension, not (byte)'\n') => Chunk.Extension,
                (Chunk.SizeLf, (byte)'\n') => _chunkSize == 0 ? Chunk.TrailerStart : Chunk.Data,
                (Chunk.DataCr, (byte)'\r') => Chunk.DataLf,
                (Chunk.DataLf, (byte)'\n') => Chunk.SizeStart,
                (Chunk.TrailerStart, (byte)'\r') => Chunk.EndLf,
                (Chunk.TrailerStart or Chunk.Trailer, (byte)'\r') => Chunk.TrailerLf,
                (Chunk.TrailerStart or Chunk.Trailer, not (byte)'\n') => Chunk.Trailer,
                (Chunk.TrailerLf, (byte)'\n') => Chunk.TrailerStart,
                (Chunk.EndLf, (byte)'\n') => Chunk.Done,
                _ => throw new InvalidDataException("Malformed chunked request body"),
            };
        }

        private Chunk AddSizeDigit(int digit)
        {
            if (_state == Chunk.SizeStart)
            {
                _chunkSize = 0;
                _sizeDigits = 0;
            }
            if (++_sizeDigits > MaxSizeDigits) throw new InvalidDataException("Chunk size too long");
            _chunkSize = (_chunkSize << 4) | (uint)digit;
            return Chunk.Size;
        }

        private static int? HexValue(byte b) => b switch
        {
            >= (byte)'0' and <= (byte)'9' => b - '0',
            >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
            >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
            _ => null,
        };

        private enum Chunk { SizeStart, Size, Extension, SizeLf, Data, DataCr, DataLf, TrailerStart, Trailer, TrailerLf, EndLf, Done }
    }
}
