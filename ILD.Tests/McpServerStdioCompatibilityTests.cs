using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace ILD.Tests;

/// <summary>
/// What an MCP client sees of the real <c>ild-mcp-server.dll</c> over stdio, for
/// every protocol version a client may still ask for: the handshake, the tool
/// list with each tool's input schema (pinned in
/// <c>Fixtures/mcp-tool-input-schemas.json</c>, captured from the server), and
/// the content a tools/call returns for an answer and for a refusal from the
/// ILD API, which a loopback stand-in plays here.
/// </summary>
public sealed class McpServerStdioCompatibilityTests : IDisposable
{
    private const string GuideBody = "Loop authoring guide: nodes, edges and rules.";
    private const string RefusalBody = """{"error":"Unauthorized","message":"Invalid or expired session"}""";

    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(120);

    private readonly CancellationTokenSource _cts = new(Guard);
    private readonly TcpListener _api = new(IPAddress.Loopback, 0);
    private readonly StringBuilder _stderr = new();
    private Process? _server;
    private int _nextId;

    public McpServerStdioCompatibilityTests()
    {
        _api.Start();
        _ = ServeApiAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _api.Stop();
        if (_server is not null)
        {
            try { _server.Kill(entireProcessTree: true); } catch { /* already gone */ }
            _server.Dispose();
        }
        _cts.Dispose();
    }

    [Theory]
    [InlineData("2024-11-05")]
    [InlineData("2025-03-26")]
    [InlineData("2025-06-18")]
    public async Task A_client_on_this_protocol_version_gets_the_same_tools_and_answers(string protocolVersion)
    {
        var ct = _cts.Token;
        StartServer();

        var init = await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = protocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "ild-tests", ["version"] = "1.0.0" },
        }, ct);
        Assert.Equal(protocolVersion, (string?)init["protocolVersion"]);
        Assert.NotNull(init["capabilities"]?["tools"]);
        await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, ct);

        var tools = await ListToolsAsync(ct);
        Assert.Equal(
            McpServerToolReflection.Names().OrderBy(n => n, StringComparer.Ordinal),
            tools.Keys.OrderBy(n => n, StringComparer.Ordinal));

        var expected = JsonNode.Parse(RepositoryFiles.ReadAllText(
            Path.Combine("ILD.Tests", "Fixtures", "mcp-tool-input-schemas.json")))!.AsObject();
        Assert.Equal(
            expected.Select(p => p.Key).OrderBy(n => n, StringComparer.Ordinal),
            tools.Keys.OrderBy(n => n, StringComparer.Ordinal));
        foreach (var (name, schema) in expected)
        {
            Assert.True(
                JsonNode.DeepEquals(schema, tools[name]["inputSchema"]),
                $"{name}: input schema changed.\nexpected: {schema?.ToJsonString()}\nactual:   {tools[name]["inputSchema"]?.ToJsonString()}");
        }

        var answer = await RequestAsync("tools/call", new JsonObject
        {
            ["name"] = "get_loop_authoring_guide",
            ["arguments"] = new JsonObject(),
        }, ct);
        Assert.NotEqual(true, (bool?)answer["isError"]);
        var answerText = Assert.Single(answer["content"]!.AsArray())!;
        Assert.Equal("text", (string?)answerText["type"]);
        Assert.Equal(GuideBody, (string?)answerText["text"]);

        var refusal = await RequestAsync("tools/call", new JsonObject
        {
            ["name"] = "get_current_loop",
            ["arguments"] = new JsonObject(),
        }, ct);
        Assert.Equal(true, (bool?)refusal["isError"]);
        var refusalText = (string?)Assert.Single(refusal["content"]!.AsArray())!["text"];
        Assert.Contains("401", refusalText);
        Assert.Contains("Invalid or expired session", refusalText);
        Assert.Contains($"{ApiUrl}/api/v1/agent/current-loop", refusalText);
    }

    private string ApiUrl => $"http://127.0.0.1:{((IPEndPoint)_api.LocalEndpoint).Port}";

    private void StartServer()
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(McpServerToolReflection.RunnableServerDll);
        foreach (var name in psi.Environment.Keys.ToArray())
        {
            if (name.StartsWith("ILD_", StringComparison.Ordinal)
                || name.EndsWith("_PROXY", StringComparison.OrdinalIgnoreCase))
                psi.Environment.Remove(name);
        }
        psi.Environment["ILD_API_URL"] = ApiUrl;

        _server = Process.Start(psi) ?? throw new InvalidOperationException("could not start dotnet");
        _server.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_stderr) _stderr.AppendLine(e.Data);
        };
        _server.BeginErrorReadLine();
    }

    private async Task<Dictionary<string, JsonObject>> ListToolsAsync(CancellationToken ct)
    {
        var tools = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var parameters = new JsonObject();
            if (cursor is not null) parameters["cursor"] = cursor;
            var page = await RequestAsync("tools/list", parameters, ct);
            foreach (var tool in page["tools"]!.AsArray())
                tools.Add((string)tool!["name"]!, tool.AsObject());
            cursor = (string?)page["nextCursor"];
        }
        while (cursor is not null);
        return tools;
    }

    private async Task<JsonObject> RequestAsync(string method, JsonObject parameters, CancellationToken ct)
    {
        var id = ++_nextId;
        await SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        }, ct);

        while (true)
        {
            var line = await _server!.StandardOutput.ReadLineAsync(ct);
            Assert.True(line is not null, $"the server closed stdout before answering {method}.\nstderr:\n{Stderr()}");
            if (string.IsNullOrWhiteSpace(line)) continue;

            var message = JsonNode.Parse(line)!.AsObject();
            if (message["id"] is null || message["method"] is not null || (int)message["id"]! != id)
                continue;

            Assert.True(message["error"] is null, $"{method} failed: {message["error"]?.ToJsonString()}");
            return message["result"]!.AsObject();
        }
    }

    private async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        var stdin = _server!.StandardInput.BaseStream;
        await stdin.WriteAsync(Encoding.UTF8.GetBytes(message.ToJsonString() + "\n"), ct);
        await stdin.FlushAsync(ct);
    }

    private string Stderr()
    {
        lock (_stderr) return _stderr.ToString();
    }

    private async Task ServeApiAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var connection = await _api.AcceptTcpClientAsync(ct);
                _ = AnswerAsync(connection, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private static async Task AnswerAsync(TcpClient connection, CancellationToken ct)
    {
        using (connection)
        {
            try
            {
                var stream = connection.GetStream();
                var head = new StringBuilder();
                var buffer = new byte[4096];
                while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(buffer, ct);
                    if (read == 0) return;
                    head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                var requestLine = head.ToString().Split("\r\n")[0].Split(' ');
                var (status, body) = (requestLine[0], requestLine[1]) switch
                {
                    ("GET", "/api/v1/agent/loop-authoring-guide") => ("200 OK", GuideBody),
                    _ => ("401 Unauthorized", RefusalBody),
                };

                var payload = Encoding.UTF8.GetBytes(body);
                var response = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response, ct);
                await stream.WriteAsync(payload, ct);
                await stream.FlushAsync(ct);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }
}
