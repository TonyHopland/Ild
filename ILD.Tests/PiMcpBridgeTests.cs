using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ILD.Core.Services.Implementations.Adapters;

namespace ILD.Tests;

/// <summary>
/// The pi extension bridge (<c>ild-mcp-bridge.js</c>, embedded in ILD.Core) runs
/// inside pi and exposes the ILD MCP server's tools through <c>pi.registerTool</c>.
/// These tests run it under node against a fake pi and either a fake MCP server
/// or the real <c>ild-mcp-server.dll</c>.
///
/// The harness calls <c>registerIldMcpTools(pi, { command, args, env, toolPrefix,
/// truncate, startupTimeoutMs })</c>, where <c>truncate</c> carries the four
/// names <c>ild.ts</c> imports from pi — <c>{ truncateHead, formatSize,
/// DEFAULT_MAX_BYTES, DEFAULT_MAX_LINES }</c> — with a 5-line limit so a short
/// text exercises truncation.
///
/// Every run asserts an empty stdout: in pi's json mode stdout is the event
/// stream, so anything the bridge prints there corrupts the run.
/// </summary>
public sealed class PiMcpBridgeTests : IDisposable
{
    private const string Png =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ild-pi-bridge-" + Guid.NewGuid().ToString("N"));

    public PiMcpBridgeTests()
    {
        Directory.CreateDirectory(_dir);

        var assembly = typeof(PiAdapter).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .SingleOrDefault(n => n.EndsWith("ild-mcp-bridge.js", StringComparison.Ordinal));
        Assert.True(resource is not null, "ild-mcp-bridge.js is not embedded in ILD.Core");

        using (var stream = assembly.GetManifestResourceStream(resource!)!)
        using (var file = File.Create(Path.Combine(_dir, "ild-mcp-bridge.js")))
            stream.CopyTo(file);

        File.WriteAllText(Path.Combine(_dir, "package.json"), """{ "type": "module" }""");
        File.WriteAllText(Path.Combine(_dir, "harness.mjs"), HarnessScript);
        File.WriteAllText(Path.Combine(_dir, "fake-mcp-server.mjs"), FakeServerScript);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Registers_exactly_the_real_mcp_servers_tools()
    {
        var dll = McpServerToolReflection.RunnableServerDll;

        var run = Run(new JsonObject
        {
            ["server"] = new JsonObject
            {
                ["command"] = "dotnet",
                ["args"] = new JsonArray(dll),
                ["env"] = new JsonObject { ["ILD_API_URL"] = "http://127.0.0.1:9" },
            },
            ["startupTimeoutMs"] = 60000,
            ["shutdown"] = true,
        }, TimeSpan.FromSeconds(120));

        var registered = RegisteredNames(run).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(registered);
        Assert.Equal(
            McpServerToolReflection.Names().Select(n => "ild_" + n).OrderBy(n => n, StringComparer.Ordinal),
            registered);
        Assert.Equal(
            IldMcpToolNames.Read(dll).Select(n => "ild_" + n).OrderBy(n => n, StringComparer.Ordinal),
            registered);
    }

    [Fact]
    public void Registers_every_listed_tool_with_its_description_and_input_schema()
    {
        var run = Run(FakeServer("normal"));

        var registered = run.Result["registered"]!.AsArray();
        // The fake server pages its tools/list, so the last tool only arrives
        // through nextCursor.
        Assert.Contains("ild_error", RegisteredNames(run));
        Assert.Contains("ild_echo", RegisteredNames(run));

        var echo = registered.Single(t => (string?)t!["name"] == "ild_echo")!;
        Assert.Equal("Echo Title", (string?)echo["label"]);
        Assert.Equal("Echoes its arguments.", (string?)echo["description"]);
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{ "type": "object", "properties": { "x": { "type": "string" } }, "required": ["x"] }"""),
            echo["parameters"]));

        var env = registered.Single(t => (string?)t!["name"] == "ild_env")!;
        Assert.Equal("env", (string?)env["label"]);

        Assert.True((int)run.Result["shutdownHandlers"]! >= 1, "no session_shutdown handler was registered");
    }

    [Fact]
    public void Execute_sends_the_arguments_to_tools_call_and_returns_text()
    {
        var content = CallOne("ild_echo", new JsonObject { ["x"] = "hi" });

        var item = Assert.Single(content);
        Assert.Equal("text", (string?)item["type"]);
        Assert.Equal("""{"x":"hi"}""", (string?)item["text"]);
    }

    [Fact]
    public void The_server_is_started_with_the_given_environment()
    {
        var content = CallOne("ild_env");

        Assert.Equal("run-123", (string?)Assert.Single(content)["text"]);
    }

    [Fact]
    public void Image_content_reaches_pi_as_an_image()
    {
        var content = CallOne("ild_image");

        var item = Assert.Single(content);
        Assert.Equal("image", (string?)item["type"]);
        Assert.Equal(Png, (string?)item["data"]);
        Assert.Equal("image/png", (string?)item["mimeType"]);
    }

    [Fact]
    public void Long_text_is_truncated_with_a_notice()
    {
        var content = CallOne("ild_long_text");

        Assert.All(content, item => Assert.Equal("text", (string?)item["type"]));
        var text = string.Join("\n", content.Select(item => (string?)item["text"]));
        Assert.Contains("L01", text);
        Assert.Contains("L05", text);
        Assert.DoesNotContain("L06", text);
        Assert.Contains("truncat", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_text_resource_becomes_text_headed_by_its_uri()
    {
        var content = CallOne("ild_text_resource");

        var text = (string?)Assert.Single(content, item => (string?)item["type"] == "text")["text"];
        Assert.Contains("ild://workitems/7/notes.md", text);
        Assert.Contains("remember the milk", text);
    }

    [Fact]
    public void An_image_blob_resource_becomes_an_image()
    {
        var content = CallOne("ild_image_blob");

        var image = Assert.Single(content, item => (string?)item["type"] == "image");
        Assert.Equal(Png, (string?)image["data"]);
        Assert.Equal("image/png", (string?)image["mimeType"]);
    }

    [Fact]
    public void A_binary_blob_resource_becomes_a_note_with_uri_mime_and_size()
    {
        var content = CallOne("ild_binary_blob");

        Assert.DoesNotContain(content, item => (string?)item["type"] == "image");
        var text = string.Join("\n", content.Select(item => (string?)item["text"]));
        Assert.Contains("ild://workitems/7/archive.zip", text);
        Assert.Contains("application/zip", text);
        // "AAAAAAAA" decodes to 6 bytes.
        Assert.Matches(@"\b6\s?(B|bytes)\b", text);
        Assert.DoesNotContain("AAAAAAAA", text);
    }

    [Fact]
    public void A_resource_link_becomes_a_link_line()
    {
        var content = CallOne("ild_resource_link");

        var text = string.Join("\n", content.Select(item => (string?)item["text"]));
        Assert.Contains("[Resource link: ild://workitems/8]", text);
    }

    [Fact]
    public void Audio_becomes_a_note_rather_than_raw_data()
    {
        var content = CallOne("ild_audio");

        Assert.All(content, item => Assert.Equal("text", (string?)item["type"]));
        var text = string.Join("\n", content.Select(item => (string?)item["text"]));
        Assert.Contains("audio", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UklGRg==", text);
    }

    [Fact]
    public void An_isError_result_throws_an_error_carrying_the_text()
    {
        var call = Calls(Run(FakeServer("normal", calls: new JsonArray(Call("ild_error"))))).Single();

        Assert.Null(call["content"]);
        Assert.True((bool)call["isError"]!, "execute rejected with something that is not an Error");
        Assert.Contains("work item 99 not found", (string?)call["error"]);
    }

    [Fact]
    public void A_server_that_never_replies_registers_nothing_and_lets_pi_run()
    {
        var run = Run(FakeServer("never-reply", startupTimeoutMs: 1000));

        AssertStartedWithoutTools(run);
    }

    [Fact]
    public void A_server_that_exits_at_startup_registers_nothing_and_lets_pi_run()
    {
        // The startup timeout is far beyond the guard, so only noticing the exit
        // lets the harness finish in time.
        var run = Run(FakeServer("exit-at-startup", startupTimeoutMs: 600000));

        AssertStartedWithoutTools(run);
    }

    [Fact]
    public void A_server_that_cannot_be_spawned_registers_nothing_and_lets_pi_run()
    {
        var run = Run(new JsonObject
        {
            ["server"] = new JsonObject
            {
                ["command"] = Path.Combine(_dir, "no-such-server"),
                ["args"] = new JsonArray(),
                ["env"] = new JsonObject(),
            },
            ["startupTimeoutMs"] = 600000,
        });

        AssertStartedWithoutTools(run);
    }

    [Fact]
    public void A_server_that_exits_mid_call_fails_the_call()
    {
        var run = Run(FakeServer("exit-mid-call", calls: new JsonArray(Call("ild_echo", new JsonObject { ["x"] = "hi" }))));

        var call = Calls(run).Single();
        Assert.Null(call["content"]);
        Assert.False(string.IsNullOrEmpty((string?)call["error"]), "the pending call was never rejected");
    }

    [Fact]
    public void Session_shutdown_kills_the_server_and_pi_exits_on_its_own()
    {
        var pidFile = Path.Combine(_dir, "server.pid");

        var run = Run(FakeServer("normal", pidFile: pidFile, shutdown: true));

        Assert.NotEmpty(RegisteredNames(run));
        Assert.True((bool)run.Result["killed"]!, "the MCP server was still running after session_shutdown");
    }

    [Fact]
    public void A_slow_call_keeps_pi_alive_until_the_result_arrives()
    {
        // Nothing but the pending call holds node's event loop here: the harness
        // registers no shutdown and sets no timers. A bridge that leaves the idle
        // server unref'd while a call is in flight lets node exit before the
        // answer lands (exit 13, no result).
        var run = Run(FakeServer("normal", calls: new JsonArray(Call("ild_slow")), shutdown: false));

        var call = Calls(run).Single();
        Assert.Equal("slow done", (string?)Assert.Single(call["content"]!.AsArray())!["text"]);
    }

    private JsonObject FakeServer(
        string mode,
        JsonArray? calls = null,
        int startupTimeoutMs = 30000,
        string? pidFile = null,
        bool shutdown = true)
    {
        var args = new JsonArray(Path.Combine(_dir, "fake-mcp-server.mjs"), mode);
        if (pidFile is not null) args.Add(pidFile);

        var spec = new JsonObject
        {
            ["server"] = new JsonObject
            {
                ["command"] = "node",
                ["args"] = args,
                ["env"] = new JsonObject { ["ILD_LOOP_RUN_ID"] = "run-123" },
            },
            ["startupTimeoutMs"] = startupTimeoutMs,
            ["calls"] = calls ?? new JsonArray(),
            ["shutdown"] = shutdown,
        };
        if (pidFile is not null) spec["pidFile"] = pidFile;
        return spec;
    }

    private static JsonObject Call(string tool, JsonObject? parameters = null)
        => new() { ["tool"] = tool, ["params"] = parameters ?? new JsonObject() };

    private JsonArray CallOne(string tool, JsonObject? parameters = null)
    {
        var call = Calls(Run(FakeServer("normal", calls: new JsonArray(Call(tool, parameters))))).Single();
        Assert.True(call["content"] is JsonArray, $"{tool} failed: {call["error"]}");
        return call["content"]!.AsArray();
    }

    private static IReadOnlyList<JsonObject> Calls(BridgeRun run)
        => run.Result["calls"]!.AsArray().Select(c => c!.AsObject()).ToArray();

    private static IReadOnlyList<string> RegisteredNames(BridgeRun run)
        => run.Result["registered"]!.AsArray().Select(t => (string)t!["name"]!).ToArray();

    private static void AssertStartedWithoutTools(BridgeRun run)
    {
        Assert.Null((string?)run.Result["threw"]);
        Assert.Empty(RegisteredNames(run));
        Assert.False(string.IsNullOrWhiteSpace(run.Stderr), "the startup failure was not reported on stderr");
    }

    private BridgeRun Run(JsonObject spec, TimeSpan? guard = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var resultFile = Path.Combine(_dir, $"result-{id}.json");
        var specFile = Path.Combine(_dir, $"spec-{id}.json");
        spec["resultFile"] = resultFile;
        File.WriteAllText(specFile, spec.ToJsonString());

        var psi = new ProcessStartInfo("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _dir,
        };
        psi.ArgumentList.Add(Path.Combine(_dir, "harness.mjs"));
        psi.ArgumentList.Add(specFile);

        // node must be on PATH; a missing node fails here rather than skipping.
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start node");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        var limit = guard ?? Guard;
        if (!process.WaitForExit(limit))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Assert.Fail($"the harness did not exit within {limit.TotalSeconds}s. stderr: {stderr.Result}");
        }
        process.WaitForExit();

        var run = new BridgeRun(process.ExitCode, stdout.Result, stderr.Result,
            File.Exists(resultFile) ? JsonNode.Parse(File.ReadAllText(resultFile))!.AsObject() : null);

        Assert.True(run.ExitCode == 0 && run.ResultOrNull is not null,
            $"the harness exited {run.ExitCode} without finishing. stderr: {run.Stderr}");
        Assert.Equal(string.Empty, run.Stdout);
        return run;
    }

    private sealed record BridgeRun(int ExitCode, string Stdout, string Stderr, JsonObject? ResultOrNull)
    {
        public JsonObject Result => ResultOrNull!;
    }

    private const string HarnessScript = """
        import { readFileSync, writeFileSync } from "node:fs";
        import { registerIldMcpTools } from "./ild-mcp-bridge.js";

        const spec = JSON.parse(readFileSync(process.argv[2], "utf8"));

        const registered = [];
        const shutdownHandlers = [];
        const pi = {
          registerTool(definition) { registered.push(definition); },
          on(event, handler) { if (event === "session_shutdown") shutdownHandlers.push(handler); },
        };

        // Stand-ins for pi's exported truncation utilities, with a 5-line limit.
        const DEFAULT_MAX_LINES = 5;
        const DEFAULT_MAX_BYTES = 50 * 1024;
        function truncateHead(content, options = {}) {
          const maxLines = options.maxLines ?? DEFAULT_MAX_LINES;
          const maxBytes = options.maxBytes ?? DEFAULT_MAX_BYTES;
          const lines = content.split("\n");
          const totalBytes = Buffer.byteLength(content, "utf-8");
          const base = { totalLines: lines.length, totalBytes, lastLinePartial: false, firstLineExceedsLimit: false, maxLines, maxBytes };
          if (lines.length <= maxLines && totalBytes <= maxBytes)
            return { ...base, content, truncated: false, truncatedBy: null, outputLines: lines.length, outputBytes: totalBytes };
          const kept = lines.slice(0, maxLines).join("\n");
          return { ...base, content: kept, truncated: true, truncatedBy: "lines", outputLines: maxLines, outputBytes: Buffer.byteLength(kept, "utf-8") };
        }
        const formatSize = (bytes) => (bytes < 1024 ? `${bytes}B` : `${(bytes / 1024).toFixed(1)}KB`);

        const result = { registered: [], calls: [], threw: null, killed: null, shutdownHandlers: 0 };
        try {
          await registerIldMcpTools(pi, {
            ...spec.server,
            toolPrefix: "ild_",
            truncate: { truncateHead, formatSize, DEFAULT_MAX_BYTES, DEFAULT_MAX_LINES },
            startupTimeoutMs: spec.startupTimeoutMs,
          });
        } catch (err) {
          result.threw = String(err?.message ?? err);
        }
        result.registered = registered.map((t) => ({ name: t.name, label: t.label, description: t.description, parameters: t.parameters }));
        result.shutdownHandlers = shutdownHandlers.length;

        for (const call of spec.calls ?? []) {
          const tool = registered.find((t) => t.name === call.tool);
          if (!tool) { result.calls.push({ tool: call.tool, error: "not registered", isError: false }); continue; }
          try {
            const out = await tool.execute(`call-${result.calls.length}`, call.params ?? {}, new AbortController().signal, () => {}, {});
            result.calls.push({ tool: call.tool, content: out.content });
          } catch (err) {
            result.calls.push({ tool: call.tool, error: String(err?.message ?? err), isError: err instanceof Error });
          }
        }

        if (spec.shutdown) {
          // Twice: the handler has to be idempotent.
          for (const handler of shutdownHandlers) await handler({ type: "session_shutdown" }, {});
          for (const handler of shutdownHandlers) await handler({ type: "session_shutdown" }, {});
        }

        if (spec.pidFile) {
          const pid = Number(readFileSync(spec.pidFile, "utf8"));
          result.killed = false;
          for (let i = 0; i < 100 && !result.killed; i++) {
            try { process.kill(pid, 0); await new Promise((r) => setTimeout(r, 50)); }
            catch { result.killed = true; }
          }
        }

        writeFileSync(spec.resultFile, JSON.stringify(result));
        """;

    private const string FakeServerScript = """
        import { writeFileSync } from "node:fs";

        const [, , mode, pidFile] = process.argv;
        if (pidFile) writeFileSync(pidFile, String(process.pid));
        if (mode === "exit-at-startup") process.exit(3);

        const PNG = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";
        const empty = { type: "object", properties: {} };
        const tools = [
          { name: "echo", title: "Echo Title", description: "Echoes its arguments.", inputSchema: { type: "object", properties: { x: { type: "string" } }, required: ["x"] } },
          { name: "env", description: "Reports the run id it was started with.", inputSchema: empty },
          { name: "slow", description: "Answers after half a second.", inputSchema: empty },
          { name: "image", description: "Returns an image.", inputSchema: empty },
          { name: "long_text", description: "Returns 20 lines.", inputSchema: empty },
          { name: "text_resource", description: "Returns a text resource.", inputSchema: empty },
          { name: "image_blob", description: "Returns an image blob resource.", inputSchema: empty },
          { name: "binary_blob", description: "Returns a binary blob resource.", inputSchema: empty },
          { name: "resource_link", description: "Returns a resource link.", inputSchema: empty },
          { name: "audio", description: "Returns audio.", inputSchema: empty },
          { name: "error", description: "Fails.", inputSchema: empty },
        ];
        const results = {
          image: { content: [{ type: "image", data: PNG, mimeType: "image/png" }] },
          long_text: { content: [{ type: "text", text: Array.from({ length: 20 }, (_, i) => `L${String(i + 1).padStart(2, "0")}`).join("\n") }] },
          text_resource: { content: [{ type: "resource", resource: { uri: "ild://workitems/7/notes.md", mimeType: "text/markdown", text: "# Notes\nremember the milk" } }] },
          image_blob: { content: [{ type: "resource", resource: { uri: "ild://workitems/7/shot.png", mimeType: "image/png", blob: PNG } }] },
          binary_blob: { content: [{ type: "resource", resource: { uri: "ild://workitems/7/archive.zip", mimeType: "application/zip", blob: "AAAAAAAA" } }] },
          resource_link: { content: [{ type: "resource_link", uri: "ild://workitems/8", name: "wi-8" }] },
          audio: { content: [{ type: "audio", data: "UklGRg==", mimeType: "audio/wav" }] },
          error: { isError: true, content: [{ type: "text", text: "work item 99 not found" }] },
        };

        const send = (message) => process.stdout.write(JSON.stringify({ jsonrpc: "2.0", ...message }) + "\n");
        const text = (id, value) => send({ id, result: { content: [{ type: "text", text: value }] } });

        function handle(message) {
          if (mode === "never-reply" || message.id === undefined) return;
          const { id, method, params } = message;
          if (method === "initialize") {
            return send({ id, result: { protocolVersion: params?.protocolVersion ?? "2025-06-18", capabilities: { tools: {} }, serverInfo: { name: "fake", version: "1.0.0" } } });
          }
          if (method === "tools/list") {
            const half = Math.ceil(tools.length / 2);
            return send({ id, result: params?.cursor === "page-2" ? { tools: tools.slice(half) } : { tools: tools.slice(0, half), nextCursor: "page-2" } });
          }
          if (method === "tools/call") {
            if (mode === "exit-mid-call") process.exit(1);
            const name = params.name;
            if (name === "echo") return text(id, JSON.stringify(params.arguments ?? {}));
            if (name === "env") return text(id, process.env.ILD_LOOP_RUN_ID ?? "<unset>");
            if (name === "slow") return setTimeout(() => text(id, "slow done"), 500);
            return send({ id, result: results[name] });
          }
          send({ id, error: { code: -32601, message: `no such method ${method}` } });
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
