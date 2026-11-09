using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using k8s;
using k8s.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ILD.Tests.Fakes;

/// <summary>
/// A Kubernetes API on loopback: the REST objects a run host creates, lists and deletes, their owner-reference garbage
/// collection, and the exec websocket (channel protocol v5) with scripted commands. The real client talks to it.
/// </summary>
internal sealed class FakeKubernetesApi : IAsyncDisposable
{
    public const string Namespace = "runs";

    private readonly object _gate = new();
    private readonly WebApplication _app;
    private readonly Dictionary<(string Kind, string Name), object> _objects = new();
    private int _podReadsBeforeReady;

    private FakeKubernetesApi(WebApplication app, bool offerV5)
    {
        _app = app;
        _offerV5 = offerV5;
    }

    private readonly bool _offerV5;

    public string Address { get; private set; } = string.Empty;

    /// <summary>Every create, as <c>kind/name</c>, in order.</summary>
    public List<string> Created { get; } = [];

    /// <summary>Every delete a client asked for, as <c>kind/name</c>, in order; the garbage collection that follows is not in it.</summary>
    public List<string> Deleted { get; } = [];

    public List<FakeExec> Execs { get; } = [];

    /// <summary>What a command does; every command ends at once with code 0 until this is set.</summary>
    public Func<FakeExec, Task> OnExec { get; set; } = exec => exec.ExitAsync(0);

    private readonly HashSet<string> _checkedOut = [];

    public List<V1StorageClass> StorageClasses { get; } = [];

    /// <summary>How many times a pod reads as Pending before it is Running.</summary>
    public int PodReadsBeforeReady { get => _podReadsBeforeReady; set => _podReadsBeforeReady = value; }

    public string PodIp { get; set; } = "10.42.0.7";

    public IKubernetes Client() => new k8s.Kubernetes(new KubernetesClientConfiguration { Host = Address });

    /// <param name="offerV5">False for a cluster that does not speak the v5 channel protocol: the exec is accepted with none.</param>
    public static async Task<FakeKubernetesApi> StartAsync(bool offerV5 = true)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var api = new FakeKubernetesApi(app, offerV5);
        api.Map(app);
        await app.StartAsync();
        api.Address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return api;
    }

    public T? Get<T>(string kind, string name) where T : class
    {
        lock (_gate) return _objects.TryGetValue((kind, name), out var o) ? o as T : null;
    }

    public IReadOnlyList<string> Names(string kind)
    {
        lock (_gate) return _objects.Keys.Where(k => k.Kind == kind).Select(k => k.Name).OrderBy(n => n).ToList();
    }

    /// <summary>Puts an object of a run in the cluster as if a run host had made it.</summary>
    public void Seed(string kind, string name, string instance, Guid run, string? managedBy = "ild", string? podPhase = null)
    {
        var meta = new V1ObjectMeta
        {
            Name = name,
            NamespaceProperty = Namespace,
            Labels = new Dictionary<string, string>
            {
                ["app.kubernetes.io/managed-by"] = managedBy ?? "other",
                ["ild/instance"] = instance,
                ["ild/run"] = run.ToString("N"),
            },
        };
        lock (_gate)
        {
            _objects[(kind, name)] = kind switch
            {
                "secrets" => new V1Secret { Metadata = meta },
                "pods" => new V1Pod { Metadata = meta, Status = podPhase is null ? null : new V1PodStatus { Phase = podPhase } },
                "persistentvolumeclaims" => new V1PersistentVolumeClaim { Metadata = meta },
                _ => new V1NetworkPolicy { Metadata = meta },
            };
        }
    }

    /// <summary>Puts a toolchain installer pod of an instance in the cluster, as if a host had made it and then crashed.</summary>
    public void SeedTools(string name, string instance)
    {
        lock (_gate)
            _objects[("pods", name)] = new V1Pod
            {
                Metadata = new V1ObjectMeta
                {
                    Name = name,
                    NamespaceProperty = Namespace,
                    Labels = new Dictionary<string, string>
                    {
                        ["app.kubernetes.io/managed-by"] = "ild",
                        ["ild/instance"] = instance,
                        ["ild/role"] = "tools",
                    },
                },
            };
    }

    private void Map(WebApplication app)
    {
        app.Use(async (http, next) =>
        {
            try { await next(http); }
            catch (Exception ex) when (!http.Response.HasStarted)
            {
                http.Response.StatusCode = 500;
                await http.Response.WriteAsync(ex.ToString());
            }
        });
        app.UseWebSockets();
        foreach (var (kind, prefix) in new[]
        {
            ("secrets", "/api/v1"), ("pods", "/api/v1"), ("persistentvolumeclaims", "/api/v1"),
            ("networkpolicies", "/apis/networking.k8s.io/v1"),
        })
        {
            var root = $"{prefix}/namespaces/{{ns}}/{kind}";
            app.MapPost(root, (Func<HttpContext, Task<IResult>>)(http => Create(http, kind)));
            app.MapGet(root, (HttpContext http) => List(http, kind));
            app.MapGet(root + "/{name}", (string name) => Read(kind, name));
            app.MapDelete(root + "/{name}", async (HttpContext http, string name) => await Delete(http, kind, name));
        }
        app.MapGet("/apis/storage.k8s.io/v1/storageclasses", () => Json(new V1StorageClassList { Items = StorageClasses }));
        app.MapGet("/api/v1/namespaces/{ns}/pods/{name}/exec", async (HttpContext http, string name) => await Exec(http, name));
    }

    private static bool Flag(HttpContext http, string name) => http.Request.Query[name].ToString() is "1" or "true";

    private static IResult Json(object body, int status = 200)
        => Results.Text(KubernetesJson.Serialize(body), "application/json", Encoding.UTF8, status);

    private static IResult Failure(int code, string reason, string message)
        => Results.Text(JsonSerializer.Serialize(new { kind = "Status", apiVersion = "v1", status = "Failure", code, reason, message }), "application/json", Encoding.UTF8, code);

    private async Task<IResult> Create(HttpContext http, string kind)
    {
        using var reader = new StreamReader(http.Request.Body);
        var body = await reader.ReadToEndAsync();
        object created = kind switch
        {
            "secrets" => KubernetesJson.Deserialize<V1Secret>(body),
            "pods" => KubernetesJson.Deserialize<V1Pod>(body),
            "persistentvolumeclaims" => KubernetesJson.Deserialize<V1PersistentVolumeClaim>(body),
            _ => KubernetesJson.Deserialize<V1NetworkPolicy>(body),
        };
        var meta = ((IMetadata<V1ObjectMeta>)created).Metadata;
        lock (_gate)
        {
            if (_objects.ContainsKey((kind, meta.Name)))
                return Failure(409, "AlreadyExists", $"{kind} \"{meta.Name}\" already exists");
            meta.Uid = Guid.NewGuid().ToString();
            _objects[(kind, meta.Name)] = created;
            Created.Add($"{kind}/{meta.Name}");
        }
        return Json(created, 201);
    }

    private IResult Read(string kind, string name)
    {
        lock (_gate)
        {
            if (!_objects.TryGetValue((kind, name), out var found))
                return Failure(404, "NotFound", $"{kind} \"{name}\" not found");
            if (found is V1Pod pod && pod.Status is null)
            {
                var ready = _podReadsBeforeReady-- <= 0;
                pod.Status = ready
                    ? new V1PodStatus { Phase = "Running", PodIP = PodIp, ContainerStatuses = [new V1ContainerStatus { Name = "runner", Ready = true, Image = "i", ImageID = "i" }] }
                    : new V1PodStatus { Phase = "Pending", Conditions = [new V1PodCondition { Type = "PodScheduled", Status = "False", Message = "0/3 nodes are available" }] };
                var snapshot = pod.Status;
                if (!ready) pod.Status = null;
                return Json(new V1Pod { Metadata = pod.Metadata, Spec = pod.Spec, Status = snapshot });
            }
            return Json(found);
        }
    }

    private IResult List(HttpContext http, string kind)
    {
        var selector = http.Request.Query["labelSelector"].ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToList();
        lock (_gate)
        {
            var matching = _objects
                .Where(o => o.Key.Kind == kind)
                .Select(o => o.Value)
                .Where(o => selector.All(s => ((IMetadata<V1ObjectMeta>)o).Metadata.Labels is { } l && l.TryGetValue(s[0], out var v) && v == s[1]))
                .ToList();
            return kind switch
            {
                "secrets" => Json(new V1SecretList { Items = matching.Cast<V1Secret>().ToList() }),
                "pods" => Json(new V1PodList { Items = matching.Cast<V1Pod>().ToList() }),
                "persistentvolumeclaims" => Json(new V1PersistentVolumeClaimList { Items = matching.Cast<V1PersistentVolumeClaim>().ToList() }),
                _ => Json(new V1NetworkPolicyList { Items = matching.Cast<V1NetworkPolicy>().ToList() }),
            };
        }
    }

    private async Task<IResult> Delete(HttpContext http, string kind, string name)
    {
        using var reader = new StreamReader(http.Request.Body);
        var graceless = (await reader.ReadToEndAsync()).Replace(" ", "").Contains("\"gracePeriodSeconds\":0", StringComparison.Ordinal);
        object removed;
        lock (_gate)
        {
            // A pod that is terminating on a node that never confirms goes only when it is deleted with a grace period of 0.
            if (_objects.TryGetValue((kind, name), out var terminating) && terminating is V1Pod { Metadata.DeletionTimestamp: not null } && !graceless)
            {
                Deleted.Add($"{kind}/{name}");
                return Json(terminating);
            }
            if (!_objects.Remove((kind, name), out removed!))
                return Failure(404, "NotFound", $"{kind} \"{name}\" not found");
            Deleted.Add($"{kind}/{name}");
            // Garbage collection: whatever names the deleted object as its owner goes with it.
            foreach (var key in _objects.Keys.ToList())
                if (((IMetadata<V1ObjectMeta>)_objects[key]).Metadata.OwnerReferences?.Any(r => r.Name == name && r.Kind.ToLowerInvariant() + "s" == kind) == true)
                    _objects.Remove(key);
        }
        // The API answers a delete with the object it is deleting, or with a status for the kinds that have none to show.
        return kind is "pods" or "persistentvolumeclaims"
            ? Json(removed)
            : Results.Text("{\"kind\":\"Status\",\"apiVersion\":\"v1\",\"status\":\"Success\"}", "application/json");
    }

    private async Task Exec(HttpContext http, string pod)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = 400;
            return;
        }
        lock (_gate)
        {
            if (!_objects.TryGetValue(("pods", pod), out var found))
            {
                http.Response.StatusCode = 404;
                return;
            }
            // The API refuses a command in a pod that has ended.
            if (((V1Pod)found).Status?.Phase is "Failed" or "Succeeded")
            {
                http.Response.StatusCode = 409;
                return;
            }
        }

        var offered = http.WebSockets.WebSocketRequestedProtocols;
        var socket = await http.WebSockets.AcceptWebSocketAsync(_offerV5 ? offered.FirstOrDefault(p => p == "v5.channel.k8s.io") ?? offered.First() : null);
        var exec = new FakeExec(pod, http.Request.Query["command"].Select(c => c!).ToList(), Flag(http, "stdin"), Flag(http, "tty"), socket);
        exec.Protocol = socket.SubProtocol ?? string.Empty;
        lock (_gate) Execs.Add(exec);
        var receive = exec.ReceiveAsync();
        try
        {
            // A real pod has a checkout once a clone has run in it, and not before.
            if (exec.Command.Any(c => c.Contains("$1/.git", StringComparison.Ordinal)))
                await exec.ExitAsync(_checkedOut.Contains(pod) ? 0 : 1);
            else
            {
                await OnExec(exec);
                if (exec.LastExitCode == 0 && exec.Command.Any(c => c.Contains("git clone", StringComparison.Ordinal)))
                    lock (_gate) _checkedOut.Add(pod);
            }
        }
        finally
        {
            await exec.CloseAsync();
            await receive;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>One command exec'd in a fake pod, from the pod's side of the websocket.</summary>
internal sealed class FakeExec(string pod, IReadOnlyList<string> command, bool wantsStdin, bool tty, WebSocket socket)
{
    private readonly MemoryStream _stdin = new();
    private readonly TaskCompletionSource _stdinClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _send = new(1, 1);

    public string Pod { get; } = pod;
    public IReadOnlyList<string> Command { get; } = command;
    public bool WantsStdin { get; } = wantsStdin;
    public bool Tty { get; } = tty;

    public string Protocol { get; set; } = string.Empty;

    public string Joined => string.Join(' ', Command);

    public List<string> Resizes { get; } = [];

    /// <summary>Everything the client sent on stdin until it closed it.</summary>
    public async Task<byte[]> ReadStdinToEndAsync()
    {
        await _stdinClosed.Task;
        return _stdin.ToArray();
    }

    public string StdinSoFar() => Encoding.UTF8.GetString(_stdin.ToArray());

    public Task WriteStdoutAsync(string text) => SendAsync(1, Encoding.UTF8.GetBytes(text));

    public Task WriteStderrAsync(string text) => SendAsync(2, Encoding.UTF8.GetBytes(text));

    /// <summary>The code the command exited with; null while it has not.</summary>
    public int? LastExitCode { get; private set; }

    public async Task ExitAsync(int code)
    {
        LastExitCode = code;
        var status = code == 0
            ? "{\"status\":\"Success\"}"
            : JsonSerializer.Serialize(new
            {
                status = "Failure",
                reason = "NonZeroExitCode",
                details = new { causes = new[] { new { reason = "ExitCode", message = code.ToString() } } },
            });
        await SendAsync(3, Encoding.UTF8.GetBytes(status));
    }

    /// <summary>Ends the connection with no status: the stream is dropped.</summary>
    public Task DropAsync() => CloseAsync();

    public async Task CloseAsync()
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        catch (WebSocketException) { }
    }

    internal async Task ReceiveAsync()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var received = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (received.MessageType == WebSocketMessageType.Close) break;
                if (received.Count == 0) continue;
                switch (buffer[0])
                {
                    case 0:
                        lock (_stdin) _stdin.Write(buffer, 1, received.Count - 1);
                        break;
                    case 255 when received.Count > 1 && buffer[1] == 0:
                        _stdinClosed.TrySetResult();
                        break;
                    case 4:
                        Resizes.Add(Encoding.UTF8.GetString(buffer, 1, received.Count - 1));
                        break;
                }
            }
        }
        catch (WebSocketException) { }
        finally
        {
            _stdinClosed.TrySetResult();
        }
    }

    private async Task SendAsync(byte channel, byte[] payload)
    {
        var frame = new byte[payload.Length + 1];
        frame[0] = channel;
        payload.CopyTo(frame, 1);
        await _send.WaitAsync();
        try { await socket.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None); }
        finally { _send.Release(); }
    }
}
