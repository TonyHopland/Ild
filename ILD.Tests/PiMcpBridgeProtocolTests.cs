using System.Diagnostics;
using System.Text.Json.Nodes;
using ILD.Core.Services.Implementations.Adapters;

namespace ILD.Tests;

/// <summary>
/// The parts of the MCP conversation <see cref="PiMcpBridgeTests"/> does not
/// reach: a server that pings the bridge before it will answer, and a call pi
/// aborts while the server is still working on it.
/// </summary>
public sealed class PiMcpBridgeProtocolTests : IDisposable
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ild-pi-bridge-protocol-" + Guid.NewGuid().ToString("N"));

    public PiMcpBridgeProtocolTests()
    {
        Directory.CreateDirectory(_dir);

        var assembly = typeof(PiAdapter).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith("ild-mcp-bridge.js", StringComparison.Ordinal));
        using (var stream = assembly.GetManifestResourceStream(resource)!)
        using (var file = File.Create(Path.Combine(_dir, "ild-mcp-bridge.js")))
            stream.CopyTo(file);

        File.WriteAllText(Path.Combine(_dir, "package.json"), """{ "type": "module" }""");
        File.WriteAllText(Path.Combine(_dir, "harness.mjs"), HarnessScript);
        File.WriteAllText(Path.Combine(_dir, "server.mjs"), ServerScript);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void A_ping_from_the_server_during_startup_is_answered()
    {
        // The server withholds its initialize reply until its ping is answered.
        var result = Run();

        Assert.Equal(new[] { "ild_hang" }, result["registered"]!.AsArray().Select(n => (string?)n).ToArray());
    }

    [Fact]
    public void Aborting_a_call_rejects_it_and_tells_the_server()
    {
        var result = Run();

        Assert.Contains("aborted", (string?)result["error"]);
        Assert.True((bool)result["serverSawCancel"]!, "the server was not sent notifications/cancelled");
    }

    private JsonObject Run()
    {
        var resultFile = Path.Combine(_dir, $"result-{Guid.NewGuid():N}.json");
        var psi = new ProcessStartInfo("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _dir,
        };
        psi.ArgumentList.Add(Path.Combine(_dir, "harness.mjs"));
        psi.ArgumentList.Add(Path.Combine(_dir, "server.mjs"));
        psi.ArgumentList.Add(Path.Combine(_dir, $"server-{Guid.NewGuid():N}.log"));
        psi.ArgumentList.Add(resultFile);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start node");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(Guard))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Assert.Fail($"the harness did not exit within {Guard.TotalSeconds}s. stderr: {stderr.Result}");
        }
        process.WaitForExit();

        Assert.True(process.ExitCode == 0 && File.Exists(resultFile),
            $"the harness exited {process.ExitCode} without finishing. stderr: {stderr.Result}");
        Assert.Equal(string.Empty, stdout.Result);
        return JsonNode.Parse(File.ReadAllText(resultFile))!.AsObject();
    }

    private const string HarnessScript = """
        import { existsSync, readFileSync, writeFileSync } from "node:fs";
        import { registerIldMcpTools } from "./ild-mcp-bridge.js";

        const [, , serverScript, logFile, resultFile] = process.argv;
        const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

        const registered = [];
        const shutdownHandlers = [];
        const pi = {
          registerTool(definition) { registered.push(definition); },
          on(event, handler) { if (event === "session_shutdown") shutdownHandlers.push(handler); },
        };
        const truncate = {
          truncateHead: (content) => ({ content, truncated: false }),
          formatSize: (bytes) => `${bytes}B`,
          DEFAULT_MAX_BYTES: 50 * 1024,
          DEFAULT_MAX_LINES: 2000,
        };

        await registerIldMcpTools(pi, { command: "node", args: [serverScript, logFile], toolPrefix: "ild_", truncate, startupTimeoutMs: 10000 });
        const result = { registered: registered.map((t) => t.name), error: null, serverSawCancel: false };

        const hang = registered.find((t) => t.name === "ild_hang");
        if (hang) {
          const controller = new AbortController();
          setTimeout(() => controller.abort(), 200);
          try { await hang.execute("call-0", {}, controller.signal, () => {}, {}); }
          catch (err) { result.error = String(err?.message ?? err); }

          const log = () => (existsSync(logFile) ? readFileSync(logFile, "utf8") : "");
          for (let i = 0; i < 100 && !result.serverSawCancel; i++) {
            result.serverSawCancel = log().includes("notifications/cancelled");
            if (!result.serverSawCancel) await sleep(50);
          }
        }

        for (const handler of shutdownHandlers) await handler({ type: "session_shutdown" }, {});
        writeFileSync(resultFile, JSON.stringify(result));
        """;

    private const string ServerScript = """
        import { appendFileSync } from "node:fs";

        const logFile = process.argv[2];
        const send = (message) => process.stdout.write(JSON.stringify({ jsonrpc: "2.0", ...message }) + "\n");
        let initializeId;

        function handle(message) {
          appendFileSync(logFile, JSON.stringify(message) + "\n");
          if (message.method === "initialize") {
            initializeId = message.id;
            return send({ id: "server-ping", method: "ping" });
          }
          if (message.id === "server-ping" && message.result) {
            return send({ id: initializeId, result: { protocolVersion: "2025-06-18", capabilities: { tools: {} }, serverInfo: { name: "fake", version: "1.0.0" } } });
          }
          if (message.method === "tools/list") {
            return send({ id: message.id, result: { tools: [{ name: "hang", description: "Never answers.", inputSchema: { type: "object", properties: {} } }] } });
          }
          // tools/call is never answered: only an abort ends it.
        }

        let buffer = "";
        process.stdin.setEncoding("utf8");
        process.stdin.on("data", (chunk) => {
          buffer += chunk;
          let newline;
          while ((newline = buffer.indexOf("\n")) >= 0) {
            const line = buffer.slice(0, newline).trim();
            buffer = buffer.slice(newline + 1);
            if (line) handle(JSON.parse(line));
          }
        });
        process.stdin.on("end", () => process.exit(0));
        """;
}
