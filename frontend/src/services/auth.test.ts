import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { authService, loopRunService, workItemService } from "./auth";

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
  test("getAll calls GET /api/v1/loopruns", async () => {
    await loopRunService.getAll();

    expect(fetchSpy).toHaveBeenCalledTimes(1);
    const [url, init] = fetchSpy.mock.calls[0];
    expect(url).toBe("/api/v1/loopruns");
    expect(init?.method).toBe("GET");
  });

  test("getById calls GET /api/v1/loopruns/:id", async () => {
    fetchSpy.mockResolvedValue(okJsonResponse({}));

    await loopRunService.getById("abc-123");

    const [url, init] = fetchSpy.mock.calls[0];
    expect(url).toBe("/api/v1/loopruns/abc-123");
    expect(init?.method).toBe("GET");
  });

  test("cancel calls POST /api/v1/loopruns/:id/cancel", async () => {
    fetchSpy.mockResolvedValue(new Response("", { status: 200 }));

    await loopRunService.cancel("abc-123");

    const [url, init] = fetchSpy.mock.calls[0];
    expect(url).toBe("/api/v1/loopruns/abc-123/cancel");
    expect(init?.method).toBe("POST");
  });

  test("pause calls POST /api/v1/loopruns/:id/pause", async () => {
    fetchSpy.mockResolvedValue(new Response("", { status: 200 }));
    await loopRunService.pause("abc-123");
    const [url, init] = fetchSpy.mock.calls[0];
    expect(url).toBe("/api/v1/loopruns/abc-123/pause");
    expect(init?.method).toBe("POST");
  });

  test("resume calls POST /api/v1/loopruns/:id/resume", async () => {
    fetchSpy.mockResolvedValue(new Response("", { status: 200 }));
    await loopRunService.resume("abc-123");
    const [url, init] = fetchSpy.mock.calls[0];
    expect(url).toBe("/api/v1/loopruns/abc-123/resume");
    expect(init?.method).toBe("POST");
  });
});

describe("workItemService URL contract", () => {
  test("transition calls POST /api/v1/workitems/:id/transition", async () => {
    fetchSpy.mockResolvedValue(okJsonResponse({}));
    await workItemService.transition("wi-1", "InProgress");
    const [url, init] = fetchSpy.mock.calls[0];
    expect(url).toBe("/api/v1/workitems/wi-1/transition");
    expect(init?.method).toBe("POST");
  });

  test("getDependencies calls GET /api/v1/workitems/:id/dependencies", async () => {
    await workItemService.getDependencies("wi-1");
    const [url, init] = fetchSpy.mock.calls[0];
    expect(url).toBe("/api/v1/workitems/wi-1/dependencies");
    expect(init?.method).toBe("GET");
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
