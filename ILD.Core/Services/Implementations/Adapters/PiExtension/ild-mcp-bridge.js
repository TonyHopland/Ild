// Exposes the ILD MCP server's tools to pi. Imported by the ild.ts extension
// PiAdapter generates per run. Plain ESM on node built-ins only, because pi's
// extension loader resolves no third-party packages.
//
// Nothing here may write to stdout: in pi's json mode stdout is the event stream.

import { spawn } from "node:child_process";

const PROTOCOL_VERSION = "2025-06-18";

// The server's own calls to the ILD API give up after 100 s (HttpClient's
// default), so a tool call still unanswered after this is a wedged server.
const DEFAULT_CALL_TIMEOUT_MS = 120_000;

// The server answers initialize and tools/list in about half a second from cold,
// so this only bounds how long a stalled server holds up pi's start.
const DEFAULT_STARTUP_TIMEOUT_MS = 10_000;

/**
 * Start the MCP server, list its tools and register each with pi as
 * `toolPrefix + name`. When the server cannot be started or does not answer
 * within `startupTimeoutMs`, registers nothing and reports on stderr, so pi
 * still runs without ILD tools. A tool call with no answer within
 * `callTimeoutMs` fails.
 *
 * `truncate` carries pi's `{ truncateHead, formatSize, DEFAULT_MAX_BYTES,
 * DEFAULT_MAX_LINES }`, which only the extension itself can import.
 */
export async function registerIldMcpTools(
  pi,
  {
    command,
    args = [],
    env = {},
    toolPrefix = "",
    truncate,
    startupTimeoutMs = DEFAULT_STARTUP_TIMEOUT_MS,
    callTimeoutMs = DEFAULT_CALL_TIMEOUT_MS,
  },
) {
  const server = new McpServer(command, args, env);
  pi.on("session_shutdown", () => server.close());

  let tools;
  try {
    tools = await withTimeout(
      server.start(),
      startupTimeoutMs,
      `no reply within ${startupTimeoutMs}ms`,
    );
  } catch (err) {
    process.stderr.write(
      `[ild] ILD MCP server unavailable, no ILD tools registered: ${err?.message ?? err}\n`,
    );
    server.close();
    return;
  }

  if (!server.alive) {
    process.stderr.write("[ild] ILD MCP server exited during startup, no ILD tools registered\n");
    server.close();
    return;
  }

  for (const tool of tools) {
    pi.registerTool({
      name: toolPrefix + tool.name,
      label: tool.title ?? tool.name,
      description: tool.description ?? "",
      parameters: tool.inputSchema ?? { type: "object", properties: {} },
      async execute(_toolCallId, params, signal) {
        const result = await server.request(
          "tools/call",
          { name: tool.name, arguments: params ?? {} },
          { signal, timeoutMs: callTimeoutMs },
        );
        const content = result?.content ?? [];
        if (result?.isError) throw new Error(textOf(content) || `${tool.name} failed`);
        return { content: toPiContent(content, truncate), details: {} };
      },
    });
  }
}

/** A minimal MCP client over the server's stdio: newline-delimited JSON-RPC 2.0. */
class McpServer {
  #command;
  #args;
  #env;
  #child = null;
  #pending = new Map();
  #nextId = 1;
  #buffer = "";
  #failure = null;
  #closed = false;

  constructor(command, args, env) {
    this.#command = command;
    this.#args = args;
    this.#env = env;
  }

  async start() {
    this.#spawn();
    await this.request("initialize", {
      protocolVersion: PROTOCOL_VERSION,
      capabilities: {},
      clientInfo: { name: "ild-pi-bridge", version: "1.0.0" },
    });
    this.#send({ jsonrpc: "2.0", method: "notifications/initialized" });

    const tools = [];
    let cursor;
    do {
      const page = await this.request("tools/list", cursor === undefined ? {} : { cursor });
      tools.push(...(page?.tools ?? []));
      cursor = page?.nextCursor;
    } while (cursor);

    // A server that answered and then exited must not leave pi with tools whose
    // every call fails, and its exit can land after the last answer: one more
    // round trip shows it is still there. Any answer, even an error, will do.
    await this.request("ping", {}).catch((err) => {
      if (this.#failure) throw err;
    });
    return tools;
  }

  get alive() {
    return this.#failure === null;
  }

  request(method, params, { signal, timeoutMs } = {}) {
    if (this.#failure) return Promise.reject(this.#failure);
    if (signal?.aborted) return Promise.reject(new Error(`${method} aborted`));

    const id = this.#nextId++;
    return new Promise((resolve, reject) => {
      let timer;
      const finish = () => {
        clearTimeout(timer);
        signal?.removeEventListener("abort", onAbort);
      };
      const cancel = (reason) => {
        finish();
        this.#settle(id);
        this.#send({
          jsonrpc: "2.0",
          method: "notifications/cancelled",
          params: { requestId: id, reason },
        });
        reject(new Error(`${method} ${reason}`));
      };
      const onAbort = () => cancel("aborted");
      signal?.addEventListener("abort", onAbort, { once: true });
      if (timeoutMs !== undefined) {
        // Unref'd: the pending request's own ref (#updateRef) is what holds pi open.
        timer = setTimeout(() => cancel(`timed out after ${timeoutMs}ms`), timeoutMs);
        timer.unref();
      }
      this.#pending.set(id, {
        resolve: (value) => {
          finish();
          resolve(value);
        },
        reject: (err) => {
          finish();
          reject(err);
        },
      });
      this.#updateRef();
      this.#send({ jsonrpc: "2.0", id, method, params });
    });
  }

  close() {
    if (this.#closed) return;
    this.#closed = true;
    this.#fail(new Error("ILD MCP server was shut down"));
    this.#child?.kill();
  }

  #spawn() {
    const child = spawn(this.#command, this.#args, {
      env: { ...process.env, ...this.#env },
      stdio: ["pipe", "pipe", "pipe"],
    });
    this.#child = child;
    child.on("error", (err) => this.#fail(err));
    child.on("exit", (code, signal) =>
      this.#fail(new Error(`ILD MCP server exited (${signal ?? `code ${code}`})`)),
    );
    child.stdin.on("error", (err) => this.#fail(err));
    child.stdout.setEncoding("utf8");
    child.stdout.on("data", (chunk) => this.#receive(chunk));
    child.stderr.on("data", (chunk) => process.stderr.write(chunk));
  }

  #receive(chunk) {
    this.#buffer += chunk;
    let newline;
    while ((newline = this.#buffer.indexOf("\n")) >= 0) {
      const line = this.#buffer.slice(0, newline).trim();
      this.#buffer = this.#buffer.slice(newline + 1);
      if (line) this.#dispatch(line);
    }
  }

  #dispatch(line) {
    let message;
    try {
      message = JSON.parse(line);
    } catch {
      process.stderr.write(`[ild] ignoring a non-JSON line from the ILD MCP server: ${line}\n`);
      return;
    }

    if (message.method !== undefined) {
      // A server-to-client request. This client declares no capabilities, so
      // ping is the only one it can answer.
      if (message.id !== undefined) {
        this.#send(
          message.method === "ping"
            ? { jsonrpc: "2.0", id: message.id, result: {} }
            : {
                jsonrpc: "2.0",
                id: message.id,
                error: { code: -32601, message: `Method not found: ${message.method}` },
              },
        );
      }
      return;
    }

    const entry = this.#settle(message.id);
    if (!entry) return;
    if (message.error)
      entry.reject(new Error(message.error.message ?? JSON.stringify(message.error)));
    else entry.resolve(message.result);
  }

  #settle(id) {
    const entry = this.#pending.get(id);
    this.#pending.delete(id);
    this.#updateRef();
    return entry;
  }

  #fail(err) {
    this.#failure ??= err;
    const pending = [...this.#pending.values()];
    this.#pending.clear();
    this.#updateRef();
    for (const entry of pending) entry.reject(this.#failure);
  }

  #send(message) {
    if (this.#failure) return;
    this.#child.stdin.write(JSON.stringify(message) + "\n");
  }

  // An idle server must not keep pi alive: json mode exits once node's event
  // loop drains. While a request is pending the server's stdout may be the only
  // live handle, so it is ref'd until the answer lands.
  #updateRef() {
    const child = this.#child;
    if (!child) return;
    if (this.#pending.size > 0) {
      child.ref();
      child.stdout?.ref();
    } else {
      child.unref();
      child.stdin?.unref();
      child.stdout?.unref();
      child.stderr?.unref();
    }
  }
}

function withTimeout(promise, ms, message) {
  let timer;
  const timeout = new Promise((_, reject) => {
    timer = setTimeout(() => reject(new Error(message)), ms);
  });
  return Promise.race([promise, timeout]).finally(() => clearTimeout(timer));
}

function textOf(content) {
  return content
    .filter((item) => item?.type === "text")
    .map((item) => item.text)
    .join("\n");
}

function toPiContent(content, truncate) {
  const texts = [];
  const images = [];
  for (const item of content) {
    const mapped = toPiItem(item, truncate.formatSize);
    if (mapped.type === "image") images.push(mapped);
    else texts.push(mapped.text);
  }
  // pi's output limit is for the whole result, so the text parts are truncated
  // together rather than each on its own.
  const items =
    texts.length > 0 ? [text(truncated(texts.join("\n\n"), truncate)), ...images] : images;
  return items.length > 0 ? items : [text("(no output)")];
}

function toPiItem(item, formatSize) {
  switch (item?.type) {
    case "text":
      return text(item.text ?? "");
    case "image":
      return { type: "image", data: item.data, mimeType: item.mimeType };
    case "resource":
      return resourceItem(item.resource ?? {}, formatSize);
    case "resource_link":
      return text(`[Resource link: ${item.uri}]`);
    case "audio":
      return text(
        `[Audio (${item.mimeType ?? "unknown type"}) omitted: pi cannot pass audio to the model]`,
      );
    default:
      return text(`[Unsupported MCP content type: ${item?.type}]`);
  }
}

function resourceItem(resource, formatSize) {
  if (typeof resource.text === "string") {
    return text(`[Resource: ${resource.uri}]\n${resource.text}`);
  }

  const mimeType = resource.mimeType ?? "application/octet-stream";
  if (mimeType.toLowerCase().startsWith("image/")) {
    return { type: "image", data: resource.blob, mimeType };
  }

  const size = Buffer.byteLength(resource.blob ?? "", "base64");
  return text(`[Binary resource: ${resource.uri} (${mimeType}, ${formatSize(size)}) not shown]`);
}

function truncated(value, { truncateHead, formatSize, DEFAULT_MAX_BYTES, DEFAULT_MAX_LINES }) {
  const result = truncateHead(value, { maxLines: DEFAULT_MAX_LINES, maxBytes: DEFAULT_MAX_BYTES });
  if (!result.truncated) return result.content;
  return (
    `${result.content}\n\n[Output truncated: ${result.outputLines} of ${result.totalLines} lines` +
    ` (${formatSize(result.outputBytes)} of ${formatSize(result.totalBytes)}).]`
  );
}

function text(value) {
  return { type: "text", text: value };
}
