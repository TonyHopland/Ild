import { describe, expect, test } from "vite-plus/test";
import type { ChatMessage } from "../types";
import { byTranscriptOrder } from "./transcriptOrder";

const message = (content: string, sequence: number, createdAt: string): ChatMessage => ({
  id: content,
  role: "assistant",
  content,
  interrupted: false,
  sequence,
  createdAt,
});

describe("byTranscriptOrder", () => {
  test("messages that share a sequence follow their save time, below the millisecond too", () => {
    const later = message("later", 1, "2026-10-09T08:00:00.0004000Z");
    const earlier = message("earlier", 1, "2026-10-09T08:00:00.0001000Z");
    const first = message("first", 0, "2026-10-09T09:00:00Z");

    expect([later, earlier, first].sort(byTranscriptOrder).map((m) => m.content)).toEqual([
      "first",
      "earlier",
      "later",
    ]);
  });
});
