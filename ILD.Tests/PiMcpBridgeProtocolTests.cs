using System.Diagnostics;
using System.Text.Json.Nodes;
using ILD.Core.Services.Implementations.Adapters;

namespace ILD.Tests;

/// <summary>
/// The parts of the MCP conversation <see cref="PiMcpBridgeTests"/> does not
/// reach: a server that pings the bridge before it will answer, a call pi aborts
/// or the server never answers, content whose MIME type is not lower case, a
/// result whose text parts are only too long together, and the default bound on
/// a stalled server holding up pi's start.
/// </summary>
public sealed class PiMcpBridgeProtocolTests : IDisposable
{
    private const string Png =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

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
        var (result, _) = Run(new JsonObject { ["tool"] = "none" });

        Assert.Equal(
            new[] { "ild_hang", "ild_upper_case_image", "ild_two_texts", "ild_one_long_line" },
            result["registered"]!.AsArray().Select(n => (string?)n).ToArray());
    }

    [Fact]
    public void Aborting_a_call_rejects_it_and_tells_the_server()
    {
        var (result, _) = Run(new JsonObject { ["tool"] = "hang", ["abortAfterMs"] = 200 });

        Assert.Contains("aborted", (string?)result["error"]);
        Assert.True((bool)result["serverSawCancel"]!, "the server was not sent notifications/cancelled");
    }

    [Fact]
    public void A_call_the_server_never_answers_times_out_and_tells_the_server()
    {
        var (result, _) = Run(new JsonObject { ["tool"] = "hang", ["callTimeoutMs"] = 300 });

        Assert.Contains("timed out", (string?)result["error"]);
        Assert.True((bool)result["serverSawCancel"]!, "the server was not sent notifications/cancelled");
    }

    [Fact]
    public void An_image_blob_with_an_upper_case_mime_type_reaches_pi_as_an_image()
    {
        var (result, _) = Run(new JsonObject { ["tool"] = "upper_case_image" });

        var image = Assert.Single(result["content"]!.AsArray())!;
        Assert.Equal("image", (string?)image["type"]);
        Assert.Equal(Png, (string?)image["data"]);
    }

    [Fact]
    public void Text_parts_are_truncated_together_as_one_result()
    {
        // Two parts of four lines each, against the harness's five-line limit: each
        // fits on its own, the whole result does not.
        var (result, _) = Run(new JsonObject { ["tool"] = "two_texts" });

        var text = (string?)Assert.Single(result["content"]!.AsArray())!["text"];
        Assert.Contains("A4", text);
        Assert.DoesNotContain("B2", text);
        Assert.Contains("truncat", text, StringComparison.OrdinalIgnoreCase);
    }

    // The byte the cut falls on, as the count of bytes of a four-byte character
    // that are left behind it: every way a character can be split, and one cut
    // that lands exactly between two, where nothing may be dropped.
    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 1)]
    [InlineData(2, 0)]
    [InlineData(3, 3)]
    public void A_result_that_is_one_long_line_reaches_the_model_instead_of_being_dropped(int pad, int splitBytes)
    {
        // Every ILD tool answers with single-line JSON, and pi's truncation never
        // returns a partial line: without a prefix of its own the model would be
        // handed the notice alone, which tells it nothing about what it asked for.
        var (result, _) = Run(new JsonObject { ["tool"] = "one_long_line", ["pad"] = pad });

        var text = (string?)Assert.Single(result["content"]!.AsArray())!["text"]!;
        var kept = text.Split("\n\n[Output truncated")[0];
        Assert.StartsWith("[{\"name\":\"" + new string('x', pad) + "𝄞", kept);
        Assert.DoesNotContain('�', kept); // never cut a character in two
        Assert.Equal(50 * 1024 - splitBytes, System.Text.Encoding.UTF8.GetByteCount(kept));
        Assert.Contains("truncat", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_server_that_exits_right_after_listing_its_tools_registers_nothing()
    {
        var (result, stderr) = Run(new JsonObject { ["tool"] = "none", ["serverMode"] = "exit-after-list" });

        Assert.Empty(result["registered"]!.AsArray());
        Assert.False(string.IsNullOrWhiteSpace(stderr), "the startup failure was not reported on stderr");
    }

    [Fact]
    public void A_stalled_server_holds_up_pis_start_for_ten_seconds_at_most_by_default()
    {
        var (result, stderr) = Run(new JsonObject { ["tool"] = "none", ["serverMode"] = "stall" });

        Assert.Empty(result["registered"]!.AsArray());
        Assert.Contains("no reply within 10000ms", stderr);
        Assert.InRange((double)result["startupMs"]!, 9000, 15000);
    }

    private (JsonObject Result, string Stderr) Run(JsonObject spec)
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
        psi.ArgumentList.Add(spec.ToJsonString());

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
        return (JsonNode.Parse(File.ReadAllText(resultFile))!.AsObject(), stderr.Result);
    }

    private const string HarnessScript = """
        import { existsSync, readFileSync, writeFileSync } from "node:fs";
        import { registerIldMcpTools } from "./ild-mcp-bridge.js";

        const [, , serverScript, logFile, resultFile, specJson] = process.argv;
        const spec = JSON.parse(specJson);
        const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

        const registered = [];
        const shutdownHandlers = [];
        const pi = {
          registerTool(definition) { registered.push(definition); },
          on(event, handler) { if (event === "session_shutdown") shutdownHandlers.push(handler); },
        };

        // Stand-ins for pi's exported truncation utilities, with a 5-line limit,
        // following pi's own rule that a partial line is never returned: when the
        // first line alone passes maxBytes it keeps nothing and says so.
        const DEFAULT_MAX_LINES = 5;
        const DEFAULT_MAX_BYTES = 50 * 1024;
        function truncateHead(content, options = {}) {
          const maxLines = options.maxLines ?? DEFAULT_MAX_LINES;
          const maxBytes = options.maxBytes ?? DEFAULT_MAX_BYTES;
          const lines = content.split("\n");
          const totalBytes = Buffer.byteLength(content, "utf-8");
          if (lines.length <= maxLines && totalBytes <= maxBytes)
            return { content, truncated: false, outputLines: lines.length, totalLines: lines.length, outputBytes: totalBytes, totalBytes, firstLineExceedsLimit: false, maxBytes };
          if (Buffer.byteLength(lines[0], "utf-8") > maxBytes)
            return { content: "", truncated: true, outputLines: 0, totalLines: lines.length, outputBytes: 0, totalBytes, firstLineExceedsLimit: true, maxBytes };
          const kept = lines.slice(0, maxLines).join("\n");
          return { content: kept, truncated: true, outputLines: maxLines, totalLines: lines.length, outputBytes: Buffer.byteLength(kept, "utf-8"), totalBytes, firstLineExceedsLimit: false, maxBytes };
        }
        const formatSize = (bytes) => `${bytes}B`;

        const started = Date.now();
        await registerIldMcpTools(pi, {
          command: "node",
          args: [serverScript, logFile, spec.serverMode ?? "normal", String(spec.pad ?? 0)],
          toolPrefix: "ild_",
          truncate: { truncateHead, formatSize, DEFAULT_MAX_BYTES, DEFAULT_MAX_LINES },
          startupTimeoutMs: spec.startupTimeoutMs,
          callTimeoutMs: spec.callTimeoutMs,
        });
        const result = {
          registered: registered.map((t) => t.name),
          startupMs: Date.now() - started,
          content: null,
          error: null,
          serverSawCancel: false,
        };

        const tool = registered.find((t) => t.name === `ild_${spec.tool}`);
        if (tool) {
          const controller = new AbortController();
          if (spec.abortAfterMs !== undefined) setTimeout(() => controller.abort(), spec.abortAfterMs);
          try { result.content = (await tool.execute("call-0", {}, controller.signal, () => {}, {})).content; }
          catch (err) { result.error = String(err?.message ?? err); }

          const log = () => (existsSync(logFile) ? readFileSync(logFile, "utf8") : "");
          for (let i = 0; spec.tool === "hang" && i < 100 && !result.serverSawCancel; i++) {
            result.serverSawCancel = log().includes("notifications/cancelled");
            if (!result.serverSawCancel) await sleep(50);
          }
        }

        for (const handler of shutdownHandlers) await handler({ type: "session_shutdown" }, {});
        writeFileSync(resultFile, JSON.stringify(result));
        """;

    private const string ServerScript = $$"""
        import { appendFileSync } from "node:fs";

        const [, , logFile, mode, pad] = process.argv;
        const send = (message) => process.stdout.write(JSON.stringify({ jsonrpc: "2.0", ...message }) + "\n");
        const empty = { type: "object", properties: {} };
        const lines = (prefix) => [1, 2, 3, 4].map((n) => `${prefix}${n}`).join("\n");
        let initializeId;

        function handle(message) {
          appendFileSync(logFile, JSON.stringify(message) + "\n");
          if (mode === "stall") return;
          if (message.method === "ping") return send({ id: message.id, result: {} });
          if (message.method === "initialize") {
            initializeId = message.id;
            return send({ id: "server-ping", method: "ping" });
          }
          if (message.id === "server-ping" && message.result) {
            return send({ id: initializeId, result: { protocolVersion: "2025-06-18", capabilities: { tools: {} }, serverInfo: { name: "fake", version: "1.0.0" } } });
          }
          if (message.method === "tools/list") {
            if (mode === "exit-after-list") setImmediate(() => process.exit(0));
            return send({ id: message.id, result: { tools: [
              { name: "hang", description: "Never answers.", inputSchema: empty },
              { name: "upper_case_image", description: "Returns an image blob typed IMAGE/PNG.", inputSchema: empty },
              { name: "two_texts", description: "Returns two four-line text parts.", inputSchema: empty },
              { name: "one_long_line", description: "Returns one line past the byte limit, as ILD tools do.", inputSchema: empty },
            ] } });
          }
          if (message.method === "tools/call" && message.params.name === "upper_case_image") {
            return send({ id: message.id, result: { content: [{ type: "resource", resource: { uri: "ild://shot", mimeType: "IMAGE/PNG", blob: "{{Png}}" } }] } });
          }
          if (message.method === "tools/call" && message.params.name === "one_long_line") {
            // Single-line JSON, like every ILD tool, and past the byte limit. The
            // characters are four bytes each and `pad` shifts where the cut falls
            // inside one: 10 + pad bytes of prefix, so the cut leaves (51190 - pad)
            // mod 4 bytes of a character behind.
            return send({ id: message.id, result: { content: [{ type: "text", text: `[{"name":"${"x".repeat(Number(pad))}${"𝄞".repeat(20000)}"}]` }] } });
          }
          if (message.method === "tools/call" && message.params.name === "two_texts") {
            return send({ id: message.id, result: { content: [{ type: "text", text: lines("A") }, { type: "text", text: lines("B") }] } });
          }
          // "hang" is never answered: only an abort or the call timeout ends it.
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
