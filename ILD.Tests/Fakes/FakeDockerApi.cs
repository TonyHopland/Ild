using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ILD.Tests.Fakes;

/// <summary>
/// A Docker-compatible API on a unix socket: the volumes, networks and containers a run host makes, lists and removes, and the
/// exec endpoints with a hijacked stream and scripted commands. A hand-written HTTP/1.1 server, because the exec start request
/// carries a body and asks for an upgrade, which Kestrel refuses. The real client talks to it.
/// </summary>
internal sealed class FakeDockerApi : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Socket _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Task> _connections = [];
    private readonly Dictionary<string, FakeVolume> _volumes = new();
    private readonly Dictionary<string, FakeNetwork> _networks = new();
    private readonly Dictionary<string, FakeContainer> _containers = new();
    private readonly Dictionary<string, FakeDockerExec> _execs = new();
    private int _nextNetwork = 10;
    private int _nextExec;

    private FakeDockerApi(string path, Socket listener)
    {
        SocketPath = path;
        _listener = listener;
    }

    public string SocketPath { get; }

    /// <summary>Every create, as <c>kind/name</c>, in order.</summary>
    public List<string> Created { get; } = [];

    /// <summary>The bodies of the network connect calls, in order.</summary>
    public List<JsonNode> Connects { get; } = [];

    /// <summary>Every delete a client asked for, as <c>kind/name</c>, in order.</summary>
    public List<string> Deleted { get; } = [];

    public List<FakeDockerExec> Execs { get; } = [];

    /// <summary>Every request, as <c>METHOD path</c>, in order.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>What a command does; every command ends at once with code 0 until this is set.</summary>
    public Func<FakeDockerExec, Task> OnExec { get; set; } = exec => exec.ExitAsync(0);

    /// <summary>How many times a container reads as <c>created</c> before it is running.</summary>
    public int ContainerReadsBeforeRunning { get; set; }

    /// <summary>Whether the runner image is on the machine; a create without it answers 404.</summary>
    public bool ImagePresent { get; set; } = true;

    /// <summary>What <c>/info</c> answers; null answers 404.</summary>
    public JsonNode? Info { get; set; }

    /// <summary>What Podman's <c>/libpod/info</c> answers; null answers 404.</summary>
    public JsonNode? LibpodInfo { get; set; }

    /// <summary>Whether the engine refuses every request, as one that has gone away would.</summary>
    public bool Down { get; set; }

    public static FakeDockerApi Start()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fd-{Guid.NewGuid():N}.sock");
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(64);
        var api = new FakeDockerApi(path, listener);
        _ = Task.Run(api.AcceptAsync);
        return api;
    }

    public IReadOnlyList<string> Volumes() { lock (_gate) return _volumes.Keys.Order().ToList(); }

    public IReadOnlyList<string> Networks() { lock (_gate) return _networks.Keys.Order().ToList(); }

    public IReadOnlyList<string> Containers() { lock (_gate) return _containers.Keys.Order().ToList(); }

    public JsonObject? Container(string name) { lock (_gate) return _containers.GetValueOrDefault(name)?.Config; }

    public JsonObject? Network(string name) { lock (_gate) return _networks.GetValueOrDefault(name)?.Config; }

    /// <summary>ILD's own container, which the host connects to the network of each run.</summary>
    public void SeedIldContainer(string name)
    {
        lock (_gate) _containers[name] = new FakeContainer(name, new JsonObject(), new Dictionary<string, string>()) { Running = true };
    }

    /// <summary>Stops a container as if the engine or the machine had: it stays, not running.</summary>
    public void Stop(string name)
    {
        lock (_gate)
        {
            var container = _containers[name];
            container.Running = false;
            container.Started = false;
        }
    }

    /// <summary>The networks a container is connected to.</summary>
    public IReadOnlyList<string> NetworksOf(string container)
    {
        lock (_gate) return _containers.TryGetValue(container, out var c) ? c.Networks.Order().ToList() : [];
    }

    /// <summary>Puts the objects of a run in the engine as if a run host had made them.</summary>
    public void Seed(string kind, string name, string instance, Guid run, string managedBy = "ild")
    {
        var labels = new Dictionary<string, string>
        {
            ["app.kubernetes.io/managed-by"] = managedBy,
            ["ild/instance"] = instance,
            ["ild/run"] = run.ToString("N"),
        };
        lock (_gate)
        {
            switch (kind)
            {
                case "volumes": _volumes[name] = new FakeVolume(name, labels); break;
                case "networks": _networks[name] = new FakeNetwork(name, NextSubnet(), labels, new JsonObject()); break;
                default: _containers[name] = new FakeContainer(name, new JsonObject(), labels) { Running = true }; break;
            }
        }
    }

    /// <summary>Puts a sign-in container in the engine as if a run host had made it: no run label, the sign-in role.</summary>
    public void SeedSignIn(string name, string instance)
    {
        var labels = new Dictionary<string, string>
        {
            ["app.kubernetes.io/managed-by"] = "ild",
            ["ild/instance"] = instance,
            ["ild/role"] = "signin",
        };
        lock (_gate) _containers[name] = new FakeContainer(name, new JsonObject(), labels) { Running = true };
    }

    private int NextSubnet() => _nextNetwork++;

    // -- Server ------------------------------------------------------------------

    private async Task AcceptAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            Socket client;
            try { client = await _listener.AcceptAsync(_stopping.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            var connection = Task.Run(() => ServeAsync(client));
            lock (_gate) _connections.Add(connection);
        }
    }

    private async Task ServeAsync(Socket socket)
    {
        using var network = new NetworkStream(socket, ownsSocket: true);
        var reader = new BufferedStream(network, 16 * 1024);
        try
        {
            while (true)
            {
                var request = await ReadRequestAsync(reader);
                if (request is null) return;
                lock (_gate) Requests.Add($"{request.Method} {request.Path}");

                if (Down)
                {
                    await RespondAsync(network, 500, new { message = "the engine is down" });
                    return;
                }
                if (request.Method == "POST" && request.Path.StartsWith("/v1.41/exec/", StringComparison.Ordinal) && request.Path.Contains("/start"))
                {
                    await HijackAsync(network, reader, socket, request);
                    return;
                }
                var (status, body) = Route(request);
                await RespondAsync(network, status, body);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
    }

    private sealed record Request(string Method, string Path, string Query, string Body)
    {
        public string Param(string name)
            => Query.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
                .Where(p => p[0] == name).Select(p => Uri.UnescapeDataString(p.ElementAtOrDefault(1) ?? string.Empty)).FirstOrDefault() ?? string.Empty;
    }

    private static async Task<Request?> ReadRequestAsync(Stream reader)
    {
        var head = new StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await reader.ReadAsync(one) == 0) return null;
            head.Append((char)one[0]);
        }
        var lines = head.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var first = lines[0].Split(' ');
        var length = lines.Skip(1).Select(l => l.Split(':', 2)).Where(p => p[0].Equals("content-length", StringComparison.OrdinalIgnoreCase))
            .Select(p => int.Parse(p[1])).FirstOrDefault();
        var body = new byte[length];
        await reader.ReadExactlyAsync(body);
        var target = first[1].Split('?', 2);
        return new Request(first[0], target[0], target.ElementAtOrDefault(1) ?? string.Empty, Encoding.UTF8.GetString(body));
    }

    private static async Task RespondAsync(Stream stream, int status, object? body)
    {
        var payload = body switch
        {
            null => [],
            PlainText p => Encoding.UTF8.GetBytes(p.Text),
            string s => Encoding.UTF8.GetBytes(s),
            _ => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body)),
        };
        var contentType = body is PlainText ? "text/plain; charset=us-ascii" : "application/json";
        var head = $"HTTP/1.1 {status} X\r\nContent-Type: {contentType}\r\nContent-Length: {payload.Length}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }

    private static (int, object?) Failure(int status, string message) => (status, new { message });

    /// <summary>A body sent as <c>text/plain</c>, not JSON.</summary>
    private sealed record PlainText(string Text);

    // -- Routes ------------------------------------------------------------------

    private (int Status, object? Body) Route(Request request)
    {
        lock (_gate)
        {
            var path = request.Path;
            // Like Podman: its own API only under a version prefix.
            if (path == "/v1.41/libpod/info")
                return LibpodInfo is null ? Failure(404, "page not found") : (200, LibpodInfo.ToJsonString());
            if (!path.StartsWith("/v1.41/", StringComparison.Ordinal)) return Failure(404, "page not found");
            var parts = path["/v1.41/".Length..].Split('/');

            return (request.Method, parts) switch
            {
                ("GET", ["info"]) => Info is null ? Failure(404, "page not found") : (200, Info.ToJsonString()),

                ("POST", ["volumes", "create"]) => CreateVolume(request),
                ("GET", ["volumes"]) => (200, new { Volumes = _volumes.Values.Where(v => Matches(request, v.Labels)).Select(v => new { v.Name, v.Labels }).ToList() }),
                ("DELETE", ["volumes", var name]) => DeleteVolume(name),

                ("POST", ["networks", "create"]) => CreateNetwork(request),
                ("GET", ["networks"]) => (200, _networks.Values.Where(n => Matches(request, n.Labels)).Select(n => new { n.Name, n.Labels }).ToList()),
                ("GET", ["networks", var name]) => InspectNetwork(name),
                ("DELETE", ["networks", var name]) => DeleteNetwork(name),
                ("POST", ["networks", var name, "connect"]) => Connect(name, request, connect: true),
                ("POST", ["networks", var name, "disconnect"]) => Connect(name, request, connect: false),

                ("POST", ["containers", "create"]) => CreateContainer(request),
                ("GET", ["containers", "json"]) => (200, _containers.Values.Where(c => Matches(request, c.Labels)).Select(c => new { Names = new[] { "/" + c.Name }, c.Labels }).ToList()),
                ("GET", ["containers", var name, "json"]) => InspectContainer(name),
                ("POST", ["containers", var name, "start"]) => StartContainer(name),
                ("DELETE", ["containers", var name]) => DeleteContainer(name),
                ("POST", ["containers", var name, "exec"]) => CreateExec(name, request),

                ("GET", ["exec", var id, "json"]) => InspectExec(id),
                ("POST", ["exec", var id, "resize"]) => Resize(id, request),
                _ => Failure(404, $"no route for {request.Method} {path}"),
            };
        }
    }

    private static bool Matches(Request request, IReadOnlyDictionary<string, string> labels)
    {
        var filters = JsonNode.Parse(request.Param("filters") is { Length: > 0 } f ? f : "{}")!;
        return (filters["label"]?.AsArray() ?? []).All(l =>
        {
            var pair = l!.GetValue<string>().Split('=', 2);
            return labels.TryGetValue(pair[0], out var value) && value == pair[1];
        });
    }

    private static Dictionary<string, string> LabelsOf(JsonNode? node)
        => (node as JsonObject)?.ToDictionary(p => p.Key, p => p.Value!.GetValue<string>()) ?? [];

    private (int, object?) CreateVolume(Request request)
    {
        var body = JsonNode.Parse(request.Body)!;
        var name = body["Name"]!.GetValue<string>();
        if (!_volumes.ContainsKey(name))
        {
            _volumes[name] = new FakeVolume(name, LabelsOf(body["Labels"]));
            Created.Add($"volumes/{name}");
        }
        return (201, new { Name = name });
    }

    private (int, object?) DeleteVolume(string name)
    {
        if (!_volumes.ContainsKey(name)) return Failure(404, $"no such volume: {name}");
        if (_containers.Values.Any(c => c.Config["HostConfig"]?["Binds"]?.AsArray().Any(b => b!.GetValue<string>().StartsWith(name + ":", StringComparison.Ordinal)) == true))
            return Failure(409, $"volume {name} is in use");
        _volumes.Remove(name);
        Deleted.Add($"volumes/{name}");
        return (204, null);
    }

    private (int, object?) CreateNetwork(Request request)
    {
        var body = JsonNode.Parse(request.Body)!.AsObject();
        var name = body["Name"]!.GetValue<string>();
        if (_networks.ContainsKey(name)) return Failure(409, $"network with name {name} already exists");
        _networks[name] = new FakeNetwork(name, NextSubnet(), LabelsOf(body["Labels"]), body);
        Created.Add($"networks/{name}");
        return (201, new { Id = name });
    }

    private (int, object?) InspectNetwork(string name)
    {
        if (!_networks.TryGetValue(name, out var network)) return Failure(404, $"network {name} not found");
        return (200, new { network.Name, IPAM = new { Config = new[] { new { Subnet = $"10.89.{network.Index}.0/24", Gateway = $"10.89.{network.Index}.1" } } } });
    }

    private (int, object?) DeleteNetwork(string name)
    {
        if (!_networks.ContainsKey(name)) return Failure(404, $"network {name} not found");
        if (_containers.Values.Any(c => c.Networks.Contains(name))) return Failure(403, $"network {name} has active endpoints");
        _networks.Remove(name);
        Deleted.Add($"networks/{name}");
        return (204, null);
    }

    private (int, object?) Connect(string network, Request request, bool connect)
    {
        var container = JsonNode.Parse(request.Body)!["Container"]!.GetValue<string>();
        if (!_networks.ContainsKey(network)) return Failure(404, $"network {network} not found");
        if (!_containers.TryGetValue(container, out var found)) return Failure(404, $"no such container: {container}");
        if (connect) { found.Networks.Add(network); Connects.Add(JsonNode.Parse(request.Body)!); }
        else found.Networks.Remove(network);
        // Podman answers both with a plain-text "OK", not an empty body.
        return (200, new PlainText("OK"));
    }

    private (int, object?) CreateContainer(Request request)
    {
        var name = Uri.UnescapeDataString(request.Param("name"));
        if (!ImagePresent) return Failure(404, "No such image: " + JsonNode.Parse(request.Body)!["Image"]);
        if (_containers.ContainsKey(name)) return Failure(409, $"container name {name} is already in use");
        var config = JsonNode.Parse(request.Body)!.AsObject();
        // Podman refuses tmpfs options it does not know, uid= and gid= among them; Docker passes them to the kernel.
        if (LibpodInfo is not null && config["HostConfig"]?["Tmpfs"] is JsonObject tmpfs)
            foreach (var option in tmpfs.SelectMany(t => t.Value!.GetValue<string>().Split(',')))
                if (option.StartsWith("uid=", StringComparison.Ordinal) || option.StartsWith("gid=", StringComparison.Ordinal))
                    return Failure(500, $"container create: unknown mount option \"{option}\": invalid mount option");
        var container = new FakeContainer(name, config, LabelsOf(config["Labels"]));
        var mode = config["HostConfig"]?["NetworkMode"]?.GetValue<string>();
        if (mode is not null)
        {
            if (!_networks.ContainsKey(mode)) return Failure(404, $"network {mode} not found");
            container.Networks.Add(mode);
        }
        _containers[name] = container;
        Created.Add($"containers/{name}");
        return (201, new { Id = name });
    }

    private (int, object?) StartContainer(string name)
    {
        if (!_containers.TryGetValue(name, out var container)) return Failure(404, $"no such container: {name}");
        if (container.Running) return (304, null);
        container.Started = true;
        return (204, null);
    }

    private (int, object?) InspectContainer(string name)
    {
        if (!_containers.TryGetValue(name, out var container)) return Failure(404, $"no such container: {name}");
        if (container is { Started: true, Running: false } && container.Reads++ >= ContainerReadsBeforeRunning)
            container.Running = true;
        var networks = container.Networks.ToDictionary(
            n => n,
            n => new { IPAddress = container.Config.Count == 0 ? $"10.89.{_networks[n].Index}.5" : $"10.89.{_networks[n].Index}.2" });
        return (200, new
        {
            Name = "/" + name,
            State = new { Running = container.Running, Status = container.Running ? "running" : "created", ExitCode = 0, Error = "" },
            NetworkSettings = new { Networks = networks },
            Config = new { Labels = container.Labels },
            HostConfig = container.Config["HostConfig"]?.DeepClone(),
        });
    }

    private (int, object?) DeleteContainer(string name)
    {
        if (!_containers.Remove(name)) return Failure(404, $"no such container: {name}");
        Deleted.Add($"containers/{name}");
        return (204, null);
    }

    private (int, object?) CreateExec(string container, Request request)
    {
        if (!_containers.ContainsKey(container)) return Failure(404, $"no such container: {container}");
        var body = JsonNode.Parse(request.Body)!;
        var id = $"exec-{++_nextExec}";
        var exec = new FakeDockerExec(
            container, body["Cmd"]!.AsArray().Select(c => c!.GetValue<string>()).ToList(),
            body["AttachStdin"]!.GetValue<bool>(), body["Tty"]!.GetValue<bool>());
        _execs[id] = exec;
        Execs.Add(exec);
        return (201, new { Id = id });
    }

    private (int, object?) InspectExec(string id)
    {
        if (!_execs.TryGetValue(id, out var exec)) return Failure(404, $"no such exec instance: {id}");
        return (200, new { Running = !exec.HasExited, ExitCode = exec.ExitCode });
    }

    private (int, object?) Resize(string id, Request request)
    {
        if (!_execs.TryGetValue(id, out var exec)) return Failure(404, $"no such exec instance: {id}");
        exec.Resizes.Add($"{request.Param("w")}x{request.Param("h")}");
        return (201, null);
    }

    // -- Exec stream -------------------------------------------------------------

    private async Task HijackAsync(NetworkStream network, Stream reader, Socket socket, Request request)
    {
        var id = request.Path.Split('/')[3];
        FakeDockerExec? exec;
        lock (_gate) _execs.TryGetValue(id, out exec);
        if (exec is null)
        {
            await RespondAsync(network, 404, new { message = $"no such exec instance: {id}" });
            return;
        }

        await network.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 UPGRADED\r\nContent-Type: application/vnd.docker.raw-stream\r\nConnection: Upgrade\r\nUpgrade: tcp\r\n\r\n"));
        exec.Attach(network, reader, socket);
        try { await OnExec(exec); }
        finally { exec.End(); }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _listener.Dispose();
        Task[] connections;
        lock (_gate) connections = [.. _connections];
        foreach (var exec in Execs.ToList()) exec.End();
        await Task.WhenAny(Task.WhenAll(connections), Task.Delay(TimeSpan.FromSeconds(2)));
        if (File.Exists(SocketPath)) File.Delete(SocketPath);
    }

    private sealed record FakeVolume(string Name, Dictionary<string, string> Labels);

    private sealed record FakeNetwork(string Name, int Index, Dictionary<string, string> Labels, JsonObject Config);

    private sealed class FakeContainer(string name, JsonObject config, Dictionary<string, string> labels)
    {
        public string Name { get; } = name;
        public JsonObject Config { get; } = config;
        public Dictionary<string, string> Labels { get; } = labels;
        public HashSet<string> Networks { get; } = [];
        public bool Started { get; set; }
        public bool Running { get; set; }
        public int Reads { get; set; }
    }
}

/// <summary>One command exec'd in a fake container, from the engine's side of the hijacked connection.</summary>
internal sealed class FakeDockerExec(string container, IReadOnlyList<string> command, bool wantsStdin, bool tty)
{
    private readonly MemoryStream _stdin = new();
    private readonly TaskCompletionSource _stdinClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _send = new(1, 1);
    private NetworkStream? _network;
    private Task? _receive;

    public string Container { get; } = container;
    public IReadOnlyList<string> Command { get; } = command;
    public bool WantsStdin { get; } = wantsStdin;
    public bool Tty { get; } = tty;
    public string Joined => string.Join(' ', Command);
    public List<string> Resizes { get; } = [];
    public bool HasExited { get; private set; }
    public int ExitCode { get; private set; }

    internal void Attach(NetworkStream network, Stream reader, Socket socket)
    {
        _network = network;
        _receive = Task.Run(async () =>
        {
            var buffer = new byte[16 * 1024];
            try
            {
                int read;
                while ((read = await reader.ReadAsync(buffer)) > 0)
                    lock (_stdin) _stdin.Write(buffer, 0, read);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
            finally { _stdinClosed.TrySetResult(); }
        });
    }

    /// <summary>Everything the client sent on stdin until it half-closed the connection.</summary>
    public async Task<byte[]> ReadStdinToEndAsync()
    {
        await _stdinClosed.Task;
        lock (_stdin) return _stdin.ToArray();
    }

    public string StdinSoFar()
    {
        lock (_stdin) return Encoding.UTF8.GetString(_stdin.ToArray());
    }

    public Task WriteStdoutAsync(string text) => SendAsync(1, Encoding.UTF8.GetBytes(text));

    public Task WriteStderrAsync(string text) => SendAsync(2, Encoding.UTF8.GetBytes(text));

    /// <summary>Reports the exit code to the engine's state, then ends the stream, in the order the real engine does.</summary>
    public Task ExitAsync(int code)
    {
        ExitCode = code;
        HasExited = true;
        return Task.CompletedTask;
    }

    /// <summary>Ends the connection with the command still running: the stream is dropped.</summary>
    public Task DropAsync() => Task.CompletedTask;

    internal void End()
    {
        try { _network?.Socket.Shutdown(SocketShutdown.Both); }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
    }

    private async Task SendAsync(byte stream, byte[] payload)
    {
        byte[] frame;
        if (Tty)
        {
            frame = payload;
        }
        else
        {
            frame = new byte[8 + payload.Length];
            frame[0] = stream;
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)payload.Length);
            payload.CopyTo(frame, 8);
        }
        await _send.WaitAsync();
        try
        {
            await _network!.WriteAsync(frame);
            await _network.FlushAsync();
        }
        finally { _send.Release(); }
    }
}
