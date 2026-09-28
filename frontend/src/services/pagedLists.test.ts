import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { loopTemplateService, repositoryService, workItemService } from "./auth";

const okJsonResponse = (body: unknown): Response =>
  new Response(JSON.stringify(body), {
    status: 200,
    headers: { "Content-Type": "application/json" },
  });

const rows = (count: number, from = 0) =>
  Array.from({ length: count }, (_, i) => ({ id: `r-${from + i}` }));

let fetchSpy: ReturnType<typeof vi.spyOn>;

beforeEach(() => {
  fetchSpy = vi.spyOn(globalThis, "fetch");
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe.each([
  ["repositoryService.getEvery", () => repositoryService.getEvery(), "/api/v1/repositories"],
  ["loopTemplateService.getEvery", () => loopTemplateService.getEvery(), "/api/v1/looptemplates"],
  ["workItemService.getEvery", () => workItemService.getEvery(), "/api/v1/workitems"],
])("%s", (_, getEvery, path) => {
  test("pages past the API's per-request cap until a short page", async () => {
    fetchSpy
      .mockResolvedValueOnce(okJsonResponse(rows(500)))
      .mockResolvedValueOnce(okJsonResponse(rows(1, 500)));

    const all = await getEvery();

    expect(all.map((row) => row.id)).toEqual(rows(501).map((row) => row.id));
    expect(fetchSpy.mock.calls.map((call: unknown[]) => call[0])).toEqual([
      `${path}?skip=0&take=500`,
      `${path}?skip=500&take=500`,
    ]);
  });

  test("stops after one request when the first page is short", async () => {
    fetchSpy.mockResolvedValueOnce(okJsonResponse(rows(3)));

    expect(await getEvery()).toHaveLength(3);
    expect(fetchSpy).toHaveBeenCalledTimes(1);
  });
});
