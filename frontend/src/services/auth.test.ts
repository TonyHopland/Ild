// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { authService, loopRunService, networkService, workItemService } from "./auth";
import { WorkItemStatus } from "../types";

const okJsonResponse = (body: unknown): Response =>
  new Response(JSON.stringify(body), {
    status: 200,
    headers: { "Content-Type": "application/json" },
  });

let fetchSpy: ReturnType<typeof vi.spyOn>;

beforeEach(() => {
  fetchSpy = vi.spyOn(globalThis, "fetch").mockResolvedValue(okJsonResponse([]));
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe("loopRunService URL contract", () => {
  test.each([
    ["getById", "GET", ""],
    ["cancel", "POST", "/cancel"],
    ["pause", "POST", "/pause"],
    ["resume", "POST", "/resume"],
    ["halt", "POST", "/halt"],
  ] as const)("%s calls %s /api/v1/loopruns/:id%s", async (verb, method, suffix) => {
    fetchSpy.mockImplementation(async () => okJsonResponse({}));

    await loopRunService[verb]("abc-123");

    const [url, init] = fetchSpy.mock.calls[0];
    expect(url).toBe(`/api/v1/loopruns/abc-123${suffix}`);
    expect(init?.method).toBe(method);
  });

  test("resumeSteer POSTs the note to /api/v1/loopruns/:id/resume-steer", async () => {
    fetchSpy.mockResolvedValue(new Response("", { status: 200 }));
    await loopRunService.resumeSteer("abc-123", "focus on the bug");
    const [url, init] = fetchSpy.mock.calls[0];
    expect(url).toBe("/api/v1/loopruns/abc-123/resume-steer");
    expect(init?.method).toBe("POST");
    expect(JSON.parse(init?.body as string)).toEqual({ note: "focus on the bug" });
  });

  test("resumeSteer sends null note when omitted", async () => {
    fetchSpy.mockResolvedValue(new Response("", { status: 200 }));
    await loopRunService.resumeSteer("abc-123");
    const [, init] = fetchSpy.mock.calls[0];
    expect(JSON.parse(init?.body as string)).toEqual({ note: null });
  });

  test("getSessionPreview calls GET /api/v1/loopruns/:id/sessions/preview for the latest page", async () => {
    fetchSpy.mockResolvedValue(okJsonResponse({}));

    await loopRunService.getSessionPreview("abc-123", "OpenCode", "ses_1");

    const [url, init] = fetchSpy.mock.calls[0];
    expect(url).toBe(
      "/api/v1/loopruns/abc-123/sessions/preview?adapterName=OpenCode&sessionId=ses_1&tail=200",
    );
    expect(init?.method).toBe("GET");
  });

  test("getSessionPreview asks for the page that ends at the given offset", async () => {
    fetchSpy.mockResolvedValue(okJsonResponse({}));

    await loopRunService.getSessionPreview("abc-123", "OpenCode", "ses_1", 4096);

    const [url] = fetchSpy.mock.calls[0];
    const query = new URL(String(url), "http://host").searchParams;
    expect(String(url)).toMatch(/^\/api\/v1\/loopruns\/abc-123\/sessions\/preview\?/);
    expect(query.get("adapterName")).toBe("OpenCode");
    expect(query.get("sessionId")).toBe("ses_1");
    expect(query.get("tail")).toBe("200");
    expect(query.get("beforeOffset")).toBe("4096");
  });
});

describe("workItemService URL contract", () => {
  test.each([
    ["transition", () => workItemService.transition("wi-1", "InProgress"), "POST", "/transition"],
    ["getDependencies", () => workItemService.getDependencies("wi-1"), "GET", "/dependencies"],
  ] as const)("%s calls %s /api/v1/workitems/wi-1%s", async (_name, call, method, suffix) => {
    fetchSpy.mockImplementation(async () => okJsonResponse({}));

    await call();

    const [url, init] = fetchSpy.mock.calls[0];
    expect(url).toBe(`/api/v1/workitems/wi-1${suffix}`);
    expect(init?.method).toBe(method);
  });

  test("every answer names the run it answers in its body, even a reject with no reason", async () => {
    fetchSpy.mockImplementation(async () => new Response("", { status: 200 }));

    await workItemService.humanFeedbackInput("wi-1", "run-1", "Looks good");
    await workItemService.humanFeedbackReject("wi-1", "run-1");
    await workItemService.humanFeedbackEdge("wi-1", "run-1", "Needs work", "Have a look");

    const sent = fetchSpy.mock.calls.map((call: unknown[]) => {
      const [url, init] = call as [string, RequestInit | undefined];
      return [url, init?.method, JSON.parse(init?.body as string)];
    });
    expect(sent).toEqual([
      [
        "/api/v1/workitems/wi-1/human-feedback/input",
        "POST",
        { runId: "run-1", input: "Looks good" },
      ],
      [
        "/api/v1/workitems/wi-1/human-feedback/reject",
        "POST",
        expect.objectContaining({ runId: "run-1" }),
      ],
      [
        "/api/v1/workitems/wi-1/human-feedback/edge",
        "POST",
        { runId: "run-1", name: "Needs work", input: "Have a look" },
      ],
    ]);
  });

  test("addDependency calls POST /api/v1/workitems/:id/dependencies", async () => {
    fetchSpy.mockResolvedValue(new Response("", { status: 200 }));
    await workItemService.addDependency("wi-1", "wi-2");
    const [url, init] = fetchSpy.mock.calls[0];
    expect(url).toBe("/api/v1/workitems/wi-1/dependencies");
    expect(init?.method).toBe("POST");
  });

  test("removeDependency calls DELETE /api/v1/workitems/:id/dependencies/:dep", async () => {
    fetchSpy.mockResolvedValue(new Response("", { status: 200 }));
    await workItemService.removeDependency("wi-1", "wi-2");
    const [url, init] = fetchSpy.mock.calls[0];
    expect(url).toBe("/api/v1/workitems/wi-1/dependencies/wi-2");
    expect(init?.method).toBe("DELETE");
  });

  test("file endpoints carry a diff range as from/to only when one is chosen", async () => {
    const from = "c1".repeat(20);
    const to = "c2".repeat(20);
    const sent = (i: number) => new URL(fetchSpy.mock.calls[i][0] as string, "http://example.test");

    fetchSpy.mockImplementation(async () => okJsonResponse({ baseSha: null, commits: [] }));
    await workItemService.getFileCommits("wi-1");
    await workItemService.getFiles("wi-1");
    await workItemService.getFiles("wi-1", { from, to });
    await workItemService.getFileContent("wi-1", "src/a b.ts", { from });
    await workItemService.saveFileContent("wi-1", "src/a.ts", "text", { from, to });

    expect(sent(0).pathname).toBe("/api/v1/workitems/wi-1/files/commits");
    expect(fetchSpy.mock.calls[0][1]?.method).toBe("GET");
    expect(fetchSpy.mock.calls[1][0]).toBe("/api/v1/workitems/wi-1/files");

    expect(sent(2).pathname).toBe("/api/v1/workitems/wi-1/files");
    expect(sent(2).searchParams.get("from")).toBe(from);
    expect(sent(2).searchParams.get("to")).toBe(to);

    expect(sent(3).searchParams.get("path")).toBe("src/a b.ts");
    expect(sent(3).searchParams.get("from")).toBe(from);
    expect(sent(3).searchParams.has("to")).toBe(false);

    expect(sent(4).pathname).toBe("/api/v1/workitems/wi-1/files/content");
    expect(fetchSpy.mock.calls[4][1]?.method).toBe("PUT");
    expect(sent(4).searchParams.get("from")).toBe(from);
    expect(sent(4).searchParams.get("to")).toBe(to);
    expect(JSON.parse(fetchSpy.mock.calls[4][1]?.body as string)).toEqual({
      path: "src/a.ts",
      content: "text",
    });
  });

  test("downloadFile GETs the raw file, path encoded, and hands back its bytes untouched", async () => {
    const bytes = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x00, 0xff, 0x0a]);
    fetchSpy.mockResolvedValue(
      new Response(bytes, {
        status: 200,
        headers: { "Content-Type": "application/octet-stream" },
      }),
    );

    const blob = await workItemService.downloadFile("wi-1", "src/a b/#1?.png");

    const [url, init] = fetchSpy.mock.calls[0];
    expect(url).toBe("/api/v1/workitems/wi-1/files/raw?path=src%2Fa%20b%2F%231%3F.png");
    expect(init?.method).toBe("GET");
    expect(Array.from(new Uint8Array(await blob.arrayBuffer()))).toEqual(Array.from(bytes));
  });
});

describe("workItemService taskboard listing URL contract", () => {
  function requested(): URL {
    const [url, init] = fetchSpy.mock.calls[0];
    expect(init?.method).toBe("GET");
    return new URL(url as string, "http://example.test");
  }

  test("getPage sends the column's statuses, the filter and the window", async () => {
    fetchSpy.mockResolvedValue(okJsonResponse({ items: [], total: 0 }));

    await workItemService.getPage({
      statuses: [WorkItemStatus.WorkQueue, WorkItemStatus.Ready],
      search: "login",
      repositoryId: "repo-1",
      tags: ["frontend", "urgent"],
      skip: 20,
      take: 20,
    });

    const url = requested();
    expect(url.pathname).toBe("/api/v1/workitems/page");
    expect(url.searchParams.getAll("status")).toEqual(["WorkQueue", "Ready"]);
    expect(url.searchParams.get("search")).toBe("login");
    expect(url.searchParams.get("repositoryId")).toBe("repo-1");
    expect(url.searchParams.getAll("tags")).toEqual(["frontend", "urgent"]);
    expect(url.searchParams.get("skip")).toBe("20");
    expect(url.searchParams.get("take")).toBe("20");
  });

  test("getCounts sends the filter and getTags asks for every tag", async () => {
    fetchSpy.mockResolvedValue(okJsonResponse({}));
    await workItemService.getCounts({ search: "a b", repositoryId: "", tags: ["x", "y"] });

    const counts = requested();
    expect(counts.pathname).toBe("/api/v1/workitems/counts");
    expect(counts.searchParams.get("search")).toBe("a b");
    expect(counts.searchParams.getAll("tags")).toEqual(["x", "y"]);
    expect(counts.searchParams.get("repositoryId") ?? "").toBe("");

    fetchSpy.mockClear();
    fetchSpy.mockResolvedValue(okJsonResponse([]));
    await workItemService.getTags();
    expect(requested().pathname).toBe("/api/v1/workitems/tags");
  });
});

describe("workItemService edit proposal URL contract", () => {
  test.each([
    [
      "listEditProposals",
      () => workItemService.listEditProposals("wi-1"),
      "GET",
      "/edit-proposals",
    ],
    [
      "listRequestedEditProposals",
      () => workItemService.listRequestedEditProposals("wi-1"),
      "GET",
      "/requested-edit-proposals",
    ],
    [
      "approveEditProposal",
      () => workItemService.approveEditProposal("wi-1", "p-1"),
      "POST",
      "/edit-proposals/p-1/approve",
    ],
  ] as const)("%s calls %s /api/v1/workitems/wi-1%s", async (_name, call, method, suffix) => {
    fetchSpy.mockImplementation(async () => okJsonResponse({}));

    await call();

    const [url, init] = fetchSpy.mock.calls[0];
    expect(url).toBe(`/api/v1/workitems/wi-1${suffix}`);
    expect(init?.method).toBe(method);
  });

  test("listEditProposalsFor filters by status and chat session in the query", async () => {
    await workItemService.listEditProposalsFor({ status: "Pending", chatSessionId: "chat-1" });
    const [url, init] = fetchSpy.mock.calls[0];
    const parsed = new URL(url as string, "http://ild");
    expect(parsed.pathname).toBe("/api/v1/workitems/edit-proposals");
    expect(parsed.searchParams.get("status")).toBe("Pending");
    expect(parsed.searchParams.get("chatSessionId")).toBe("chat-1");
    expect(init?.method).toBe("GET");
  });

  test("rejectEditProposal POSTs the reason to /api/v1/workitems/:id/edit-proposals/:pid/reject", async () => {
    fetchSpy.mockResolvedValue(okJsonResponse({}));
    await workItemService.rejectEditProposal("wi-1", "p-1", "Too vague.");
    const [url, init] = fetchSpy.mock.calls[0];
    expect(url).toBe("/api/v1/workitems/wi-1/edit-proposals/p-1/reject");
    expect(init?.method).toBe("POST");
    expect(JSON.parse(init?.body as string)).toEqual({ reason: "Too vague." });
  });
});

describe("authService.onTokenChange", () => {
  afterEach(() => {
    localStorage.clear();
  });

  test("listeners are notified when setAuth is called", () => {
    const listener = vi.fn();
    const unsubscribe = authService.onTokenChange(listener);

    authService.setAuth({ id: "u1", username: "x", createdAt: "" }, "tok-1");

    expect(listener).toHaveBeenCalledWith("tok-1");
    unsubscribe();
  });

  test("listeners are notified with null when clearAuth is called", () => {
    const listener = vi.fn();
    const unsubscribe = authService.onTokenChange(listener);

    authService.clearAuth();

    expect(listener).toHaveBeenCalledWith(null);
    unsubscribe();
  });

  test("unsubscribe stops further notifications", () => {
    const listener = vi.fn();
    const unsubscribe = authService.onTokenChange(listener);
    unsubscribe();

    authService.clearAuth();

    expect(listener).not.toHaveBeenCalled();
  });
});

describe("networkService.updateEntry", () => {
  test("PUTs the rule to /api/v1/network/entries/:id and resolves to the server's entry", async () => {
    const entry = {
      id: "e1",
      host: ".example.com",
      listKind: "Blacklist",
      aiProviderId: null,
      createdAt: "2026-09-01T10:00:00Z",
    };
    fetchSpy.mockResolvedValue(okJsonResponse(entry));

    const result = await networkService.updateEntry("e1", {
      host: "*.example.com",
      listKind: "Blacklist",
      aiProviderId: null,
    });

    const [url, init] = fetchSpy.mock.calls[0];
    expect(url).toBe("/api/v1/network/entries/e1");
    expect(init?.method).toBe("PUT");
    expect(JSON.parse(init?.body as string)).toEqual({
      host: "*.example.com",
      listKind: "Blacklist",
      aiProviderId: null,
    });
    expect(result).toEqual(entry);
  });
});
