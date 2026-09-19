import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { settingsService, workItemService } from "./auth";

const okJsonResponse = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });

const errorJsonResponse = (status: number, error: string): Response =>
  new Response(JSON.stringify({ error }), {
    status,
    headers: { "Content-Type": "application/json" },
  });

let fetchSpy: ReturnType<typeof vi.spyOn>;

beforeEach(() => {
  localStorage.setItem("auth_token", "token-123");
  fetchSpy = vi.spyOn(globalThis, "fetch").mockResolvedValue(okJsonResponse([]));
});

afterEach(() => {
  vi.restoreAllMocks();
  localStorage.clear();
});

const lastCall = () => fetchSpy.mock.calls[fetchSpy.mock.calls.length - 1] as [string, RequestInit];

describe("attachment upload contract", () => {
  test("uploadAttachment posts one file as multipart under the field 'files'", async () => {
    fetchSpy.mockResolvedValue(
      okJsonResponse(
        [
          {
            id: "att-1",
            fileName: "shot.png",
            contentType: "image/png",
            sizeBytes: 3,
            createdAt: "2026-09-18T10:00:00Z",
          },
        ],
        201,
      ),
    );
    const file = new File(["abc"], "shot.png", { type: "image/png" });

    const created = await workItemService.uploadAttachment("wi-1", file);

    const [url, init] = lastCall();
    expect(url).toBe("/api/v1/workitems/wi-1/attachments");
    expect(init.method).toBe("POST");
    expect(init.body).toBeInstanceOf(FormData);
    const sent = (init.body as FormData).getAll("files");
    expect(sent).toHaveLength(1);
    expect((sent[0] as File).name).toBe("shot.png");
    expect(created[0].fileName).toBe("shot.png");
  });

  // The browser has to compute the multipart boundary, which it only does when
  // the request carries no Content-Type of its own.
  test("uploadAttachment sends the bearer token but no Content-Type", async () => {
    const file = new File(["abc"], "shot.png", { type: "image/png" });

    await workItemService.uploadAttachment("wi-1", file);

    const headers = new Headers(lastCall()[1].headers as HeadersInit);
    expect(headers.get("content-type")).toBeNull();
    expect(headers.get("authorization")).toBe("Bearer token-123");
  });

  test("a refused upload rejects with the server's own message", async () => {
    fetchSpy.mockResolvedValue(
      errorJsonResponse(400, "'big.bin' is larger than the 25 MB allowed per file."),
    );

    await expect(
      workItemService.uploadAttachment("wi-1", new File(["a"], "big.bin")),
    ).rejects.toMatchObject({
      status: 400,
      message: "'big.bin' is larger than the 25 MB allowed per file.",
    });
  });
});

describe("attachment download and removal contract", () => {
  test("downloadAttachment GETs the attachment and returns its bytes", async () => {
    fetchSpy.mockResolvedValue(new Response("PNGBYTES", { status: 200 }));

    const blob = await workItemService.downloadAttachment("wi-1", "att-1");

    const [url, init] = lastCall();
    expect(url).toBe("/api/v1/workitems/wi-1/attachments/att-1");
    expect(init.method).toBe("GET");
    // The bytes come back through the API client — which carries the token in a
    // header — rather than through a bare href the browser would send without it.
    expect(new Headers(init.headers as HeadersInit).get("authorization")).toBe("Bearer token-123");
    expect(await blob.text()).toBe("PNGBYTES");
  });

  test("deleteAttachment DELETEs the attachment", async () => {
    fetchSpy.mockResolvedValue(new Response(null, { status: 204 }));

    await workItemService.deleteAttachment("wi-1", "att-1");

    const [url, init] = lastCall();
    expect(url).toBe("/api/v1/workitems/wi-1/attachments/att-1");
    expect(init.method).toBe("DELETE");
  });

  test("an unreachable WorkItem server rejects with what the API reported", async () => {
    fetchSpy.mockResolvedValue(errorJsonResponse(503, "WorkItemServer unreachable"));

    await expect(workItemService.deleteAttachment("wi-1", "att-1")).rejects.toMatchObject({
      status: 503,
      message: "WorkItemServer unreachable",
    });
  });
});

describe("attachment limits contract", () => {
  test("getAttachmentLimits reads the configured limits from settings", async () => {
    fetchSpy.mockResolvedValue(
      okJsonResponse({
        maxBytesPerFile: 26214400,
        maxFilesPerRequest: 10,
        maxTotalBytesPerWorkItem: 262144000,
      }),
    );

    const limits = await settingsService.getAttachmentLimits();

    const [url, init] = lastCall();
    expect(url).toBe("/api/v1/settings/attachments");
    expect(init.method).toBe("GET");
    expect(limits).toMatchObject({
      maxBytesPerFile: 26214400,
      maxFilesPerRequest: 10,
      maxTotalBytesPerWorkItem: 262144000,
    });
  });
});
