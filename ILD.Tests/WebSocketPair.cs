using System.Net.WebSockets;
using System.Threading.Channels;

namespace ILD.Tests;

/// <summary>
/// In-process paired WebSockets for tests. Each pair member's <c>SendAsync</c>
/// becomes the other member's <c>ReceiveAsync</c>. Only the surface used by
/// <see cref="ILD.Api.Services.InteractiveProviderSessionService"/> is wired;
/// other WebSocket APIs throw if exercised.
/// </summary>
internal static class WebSocketPair
{
    public static (WebSocket Server, WebSocket Client) Create()
    {
        var s2c = Channel.CreateUnbounded<Frame>();
        var c2s = Channel.CreateUnbounded<Frame>();
        var server = new InMemoryWebSocket(reads: c2s.Reader, writes: s2c.Writer);
        var client = new InMemoryWebSocket(reads: s2c.Reader, writes: c2s.Writer);
        server.Peer = client;
        client.Peer = server;
        return (server, client);
    }

    private sealed record Frame(WebSocketMessageType Type, byte[] Data, bool EndOfMessage, bool Close);

    private sealed class InMemoryWebSocket : WebSocket
    {
        private readonly ChannelReader<Frame> _reads;
        private readonly ChannelWriter<Frame> _writes;
        private WebSocketState _state = WebSocketState.Open;
        private Frame? _pending;

        public InMemoryWebSocket(ChannelReader<Frame> reads, ChannelWriter<Frame> writes)
        {
            _reads = reads;
            _writes = writes;
        }

        public InMemoryWebSocket? Peer { get; set; }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort()
        {
            _state = WebSocketState.Aborted;
            _writes.TryComplete();
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            if (_state is WebSocketState.Closed or WebSocketState.Aborted) return Task.CompletedTask;
            _state = WebSocketState.Closed;
            _writes.TryWrite(new Frame(WebSocketMessageType.Close, Array.Empty<byte>(), true, true));
            _writes.TryComplete();
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override void Dispose() => Abort();

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            Frame frame;
            if (_pending is not null)
            {
                frame = _pending;
                _pending = null;
            }
            else
            {
                try
                {
                    frame = await _reads.ReadAsync(cancellationToken);
                }
                catch (ChannelClosedException)
                {
                    _state = WebSocketState.Closed;
                    return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
                }
            }

            if (frame.Close)
            {
                _state = WebSocketState.CloseReceived;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }

            var n = Math.Min(buffer.Count, frame.Data.Length);
            Array.Copy(frame.Data, 0, buffer.Array!, buffer.Offset, n);

            if (n < frame.Data.Length)
            {
                var remaining = new byte[frame.Data.Length - n];
                Array.Copy(frame.Data, n, remaining, 0, remaining.Length);
                _pending = frame with { Data = remaining };
                return new WebSocketReceiveResult(n, frame.Type, endOfMessage: false);
            }

            return new WebSocketReceiveResult(n, frame.Type, frame.EndOfMessage);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            if (_state is WebSocketState.Closed or WebSocketState.Aborted)
                return Task.CompletedTask;
            var copy = new byte[buffer.Count];
            Array.Copy(buffer.Array!, buffer.Offset, copy, 0, buffer.Count);
            _writes.TryWrite(new Frame(messageType, copy, endOfMessage, Close: false));
            return Task.CompletedTask;
        }
    }
}
