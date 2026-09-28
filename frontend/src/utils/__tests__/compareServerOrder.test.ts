import { describe, expect, test } from "vite-plus/test";
import { WorkItem } from "../../types";
import { compareServerOrder } from "../taskboardColumns";

function created(id: string, createdAt: string): WorkItem {
  return { id, createdAt } as WorkItem;
}

function order(items: WorkItem[]): string[] {
  return [...items].sort(compareServerOrder).map((item) => item.id);
}

describe("compareServerOrder at the server's precision", () => {
  test("orders creation times inside one millisecond by their 100 ns ticks, not by id", () => {
    expect(
      order([
        created("z", "2026-01-01T00:00:00.1230001Z"),
        created("a", "2026-01-01T00:00:00.1230009Z"),
        created("m", "2026-01-01T00:00:00.123Z"),
      ]),
    ).toEqual(["a", "z", "m"]);
  });

  test("reads a missing fraction as zero and keeps the zone of the whole seconds", () => {
    expect(
      order([
        created("utc", "2026-01-01T00:00:00Z"),
        created("later", "2026-01-01T02:00:00.0000001+02:00"),
        created("earlier", "2025-12-31T23:59:59.9999999Z"),
      ]),
    ).toEqual(["later", "utc", "earlier"]);
  });

  test("breaks an exact tie by id, highest first", () => {
    expect(
      order([created("a", "2026-01-01T00:00:00.5Z"), created("b", "2026-01-01T00:00:00.5000000Z")]),
    ).toEqual(["b", "a"]);
  });
});
