using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ILD.Core.Services.RunHosts.Container;

/// <summary>The Docker-compatible API answered with a status the caller did not accept.</summary>
internal sealed class DockerApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// A client for the part of the Docker Engine API that Docker and Podman's compatibility service both serve (the
/// <c>v1.41</c> endpoints), over the engine's unix socket. Requests are plain HTTP over that socket; the exec stream is
/// a hijacked connection, which is why it is opened on a socket of its own.
/// </summary>
internal sealed class DockerApiClient : IDisposable
{
    private const string ApiVersion = "v1.41";

    private static readonly TimeSpan HandshakeLimit = TimeSpan.FromSeconds(45);

    private readonly string _socket;
    private readonly HttpClient _http;

    public DockerApiClient(string socket)
    {
        _socket = socket;
        _http = new HttpClient(new SocketsHttpHandler
        {
            // The socket is the only way to the engine; an HTTP proxy in the environment must never be asked to reach it.
            UseProxy = false,
            ConnectCallback = async (_, ct) => new NetworkStream(await ConnectAsync(ct), ownsSocket: true),
        })
        {
            BaseAddress = new Uri("http://docker/"),
            Timeout = TimeSpan.FromMinutes(5),
        };
    }

    public string Socket => _socket;

    private async Task<Socket> ConnectAsync(CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socket), ct);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // -- Requests ----------------------------------------------------------------

    /// <summary>The JSON the call answers with (null when it has no body). A status in <paramref name="tolerate"/> is not a failure and answers null.</summary>
    public async Task<JsonNode?> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, params HttpStatusCode[] tolerate)
    {
        using var request = new HttpRequestMessage(method, $"/{ApiVersion}{path}");
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            if (tolerate.Contains(response.StatusCode)) return null;
            throw new DockerApiException(response.StatusCode, $"{method} {path}: {MessageOf(text) ?? response.ReasonPhrase ?? response.StatusCode.ToString()}");
        }
        // Only a JSON answer is a body: Podman answers some calls (network connect and disconnect) with a plain-text "OK".
        return string.IsNullOrWhiteSpace(text) || !IsJson(response.Content.Headers.ContentType?.MediaType) ? null : JsonNode.Parse(text);
    }

    private static bool IsJson(string? mediaType)
        => mediaType is not null
           && (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
               || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));

    public Task<JsonNode?> GetAsync(string path, CancellationToken ct, params HttpStatusCode[] tolerate)
        => SendAsync(HttpMethod.Get, path, null, ct, tolerate);

    public Task<JsonNode?> PostAsync(string path, object? body, CancellationToken ct, params HttpStatusCode[] tolerate)
        => SendAsync(HttpMethod.Post, path, body, ct, tolerate);

    /// <summary>The engine's own info as <c>/info</c> answers it; the Podman-only <c>/libpod/info</c> when <paramref name="libpod"/>. The status says what the engine answered, and the body is null unless it was a JSON success.</summary>
    public async Task<(HttpStatusCode Status, JsonNode? Body)> InfoAsync(bool libpod, CancellationToken ct)
    {
        // Podman serves its own API under a version prefix only: an unversioned /libpod/info is a 404.
        using var request = new HttpRequestMessage(HttpMethod.Get, libpod ? $"/{ApiVersion}/libpod/info" : $"/{ApiVersion}/info");
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return (response.StatusCode, null);
        try { return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))); }
        catch (JsonException) { return (response.StatusCode, null); }
    }

    /// <summary>The query value of an object filter: <c>{"label":["a=b"]}</c>.</summary>
    public static string LabelFilter(params string[] labels)
        => Uri.EscapeDataString(JsonSerializer.Serialize(new Dictionary<string, string[]> { ["label"] = labels }));

    private static string? MessageOf(string text)
    {
        try { return JsonNode.Parse(text)?["message"]?.GetValue<string>(); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return string.IsNullOrWhiteSpace(text) ? null : text.Trim(); }
    }

    // -- Exec --------------------------------------------------------------------

    /// <summary>Make an exec in the container and return its id.</summary>
    public async Task<string> CreateExecAsync(string container, IReadOnlyList<string> command, bool stdin, bool tty, CancellationToken ct)
    {
        var created = await PostAsync(
            $"/containers/{Uri.EscapeDataString(container)}/exec",
            new { AttachStdin = stdin, AttachStdout = true, AttachStderr = !tty, Tty = tty, Cmd = command },
            ct);
        return created?["Id"]?.GetValue<string>() ?? throw new InvalidOperationException("the engine made an exec and did not name it");
    }

    /// <summary>
    /// Start the exec on a connection of its own and hand that connection back once the engine has switched it to the raw
    /// stream. The engine answers <c>101</c> (Docker) or <c>200</c> (Podman) and everything after the headers is the stream.
    /// </summary>
    public async Task<(Socket Socket, NetworkStream Network, Stream Reader)> StartExecAsync(string execId, bool tty, CancellationToken ct)
    {
        // Connecting, sending and reading the head are bounded; the stream handed back is not.
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshake.CancelAfter(HandshakeLimit);
        var socket = await ConnectAsync(handshake.Token);
        try
        {
            var network = new NetworkStream(socket, ownsSocket: false);
            var body = JsonSerializer.Serialize(new { Detach = false, Tty = tty });
            var head = $"POST /{ApiVersion}/exec/{Uri.EscapeDataString(execId)}/start HTTP/1.1\r\nHost: docker\r\nContent-Type: application/json\r\n"
                + $"Connection: Upgrade\r\nUpgrade: tcp\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
            await network.WriteAsync(Encoding.UTF8.GetBytes(head), handshake.Token);

            var reader = new BufferedStream(network, 16 * 1024);
            var (status, headers) = await ReadHeadAsync(reader, handshake.Token);
            if (status is not (101 or 200))
            {
                var text = headers.TryGetValue("content-length", out var length) && int.TryParse(length, out var count) && count is > 0 and < 65536
                    ? await ReadTextAsync(reader, count, handshake.Token)
                    : string.Empty;
                throw new DockerApiException((HttpStatusCode)status, $"starting the exec: {MessageOf(text) ?? status.ToString()}");
            }
            return (socket, network, reader);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<(int Status, Dictionary<string, string> Headers)> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one, ct) == 0)
                throw new IOException("the engine closed the connection before it answered");
            if (one[0] == '\n')
            {
                var text = line.ToString().TrimEnd('\r');
                line.Clear();
                if (text.Length == 0 && lines.Count > 0) break;
                if (text.Length > 0) lines.Add(text);
            }
            else
            {
                line.Append((char)one[0]);
            }
        }

        var parts = lines[0].Split(' ', 3);
        if (parts.Length < 2 || !int.TryParse(parts[1], out var status))
            throw new IOException($"the engine answered '{lines[0]}'");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in lines.Skip(1))
            if (header.IndexOf(':') is > 0 and var colon)
                headers[header[..colon].Trim()] = header[(colon + 1)..].Trim();
        return (status, headers);
    }

    private static async Task<string> ReadTextAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, ct);
        return Encoding.UTF8.GetString(buffer);
    }

    /// <summary>The exit code of a finished exec; null while it still runs.</summary>
    public async Task<int?> ExecExitCodeAsync(string execId, CancellationToken ct)
    {
        var state = await GetAsync($"/exec/{Uri.EscapeDataString(execId)}/json", ct);
        if (state?["Running"]?.GetValue<bool>() != false) return null;
        return state["ExitCode"]?.GetValue<int>();
    }

    public Task ResizeExecAsync(string execId, int cols, int rows, CancellationToken ct)
        => PostAsync($"/exec/{Uri.EscapeDataString(execId)}/resize?h={rows}&w={cols}", null, ct);

    public void Dispose() => _http.Dispose();
}
