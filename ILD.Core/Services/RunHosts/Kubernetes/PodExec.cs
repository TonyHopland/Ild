using System.Text;
using System.Text.Json;
using ILD.Core.Services.RunHosts.Isolated;
using k8s;
using k8s.Models;

namespace ILD.Core.Services.RunHosts.Kubernetes;

/// <summary>
/// One stream of the Kubernetes exec API: a command running in a pod's container, with its stdin, stdout, stderr and
/// the status the API reports when it ends. The v5 channel protocol is used because it can close stdin, which a command
/// that reads its input to the end (<c>cat</c>, a CLI taking its prompt on stdin) needs.
/// </summary>
internal sealed class PodExec : IExecStream
{
    private const string Protocol = "v5.channel.k8s.io";

    private readonly StreamDemuxer _demuxer;
    private readonly Stream? _stdin;
    private int _disposed;

    private PodExec(StreamDemuxer demuxer, Stream? stdin, Stream stdout, Stream? stderr, Stream error)
    {
        _demuxer = demuxer;
        _stdin = stdin;
        Stdout = stdout;
        Stderr = stderr;
        Status = ReadStatusAsync(error);
    }

    public Stream Stdout { get; }

    /// <summary>Null for a terminal, whose stderr is mixed into stdout.</summary>
    public Stream? Stderr { get; }

    /// <summary>The exit code of the command; null when the stream ended without one, which means it was dropped.</summary>
    public Task<int?> Status { get; }

    public static async Task<PodExec> OpenAsync(
        IKubernetes client, string namespaceName, string pod, IReadOnlyList<string> command, bool stdin, bool tty, CancellationToken ct)
    {
        // Not MuxedStreamNamespacedPodExecAsync: it ignores its arguments and always asks for v4 with all three streams.
        var socket = await client.WebSocketNamespacedPodExecAsync(
            pod, namespaceName, command, container: null, stderr: !tty, stdin: stdin, stdout: true, tty: tty,
            webSocketSubProtocol: Protocol, cancellationToken: ct);
        var demuxer = new StreamDemuxer(socket, StreamType.RemoteCommand, ownsSocket: true, supportsClose: socket.SubProtocol == Protocol);
        // The streams before the loop starts: what arrives for a stream nobody asked for yet is dropped, and a loop that ends first leaves it open.
        var exec = new PodExec(
            demuxer,
            stdin ? demuxer.GetStream(null, ChannelIndex.StdIn) : null,
            demuxer.GetStream(ChannelIndex.StdOut, null),
            tty ? null : demuxer.GetStream(ChannelIndex.StdErr, null),
            demuxer.GetStream(ChannelIndex.Error, null));
        demuxer.Start();
        return exec;
    }

    public async Task WriteStdinAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (_stdin is null) throw new InvalidOperationException("the command was started without stdin");
        await _stdin.WriteAsync(data, ct);
        await _stdin.FlushAsync(ct);
    }

    public async Task CloseStdinAsync(CancellationToken ct = default)
    {
        if (_stdin is null) return;
        if (!_demuxer.SupportsClose)
            throw new InvalidOperationException($"the cluster did not negotiate {Protocol}, so the command's stdin cannot be closed; Kubernetes 1.30 or newer is needed");
        await _demuxer.CloseChannel(ChannelIndex.StdIn, ct);
    }

    public Task ResizeAsync(int cols, int rows, CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { Width = cols, Height = rows });
        return _demuxer.Write(ChannelIndex.Resize, payload, 0, payload.Length, ct);
    }

    internal static async Task<int?> ReadStatusAsync(Stream error)
    {
        try
        {
            using var reader = new StreamReader(error, Encoding.UTF8);
            var json = await reader.ReadToEndAsync();
            return json.Length == 0 ? null : ExitCodeOf(json);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.WebSockets.WebSocketException or OperationCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The exit code the API's status of a finished exec carries.</summary>
    internal static int? ExitCodeOf(string statusJson)
    {
        var status = KubernetesJson.Deserialize<V1Status>(statusJson);
        if (status.Status == "Success") return 0;
        var cause = status.Details?.Causes?.FirstOrDefault(c => c.Reason == "ExitCode");
        return int.TryParse(cause?.Message, out var code) ? code : null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _demuxer.Dispose();
    }
}
