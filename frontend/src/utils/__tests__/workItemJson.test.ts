import { describe, expect, test } from "vite-plus/test";
import { parseConversation } from "../workItemJson";
import type { WorkItem, ConversationMessage } from "../../types";

describe("parseConversation", () => {
  test("returns messages from conversation array", () => {
    const messages: ConversationMessage[] = [
      { role: "ai", content: "Need approval", timestamp: "2025-01-01T00:00:00Z" },
      { role: "human", content: "Approved", timestamp: "2025-01-01T01:00:00Z" },
    ];

    const workItem = { conversation: messages } as WorkItem;
    const result = parseConversation(workItem);

    expect(result).toHaveLength(2);
    expect(result[0]).toEqual({
      role: "ai",
      content: "Need approval",
      timestamp: "2025-01-01T00:00:00Z",
    });
    expect(result[1]).toEqual({
      role: "human",
      content: "Approved",
      timestamp: "2025-01-01T01:00:00Z",
    });
  });

  test("returns empty array when conversation is undefined", () => {
    const workItem = {} as WorkItem;
    expect(parseConversation(workItem)).toEqual([]);
  });

  test("returns empty array when conversation is null", () => {
    const workItem = { conversation: null } as unknown as WorkItem;
    expect(parseConversation(workItem)).toEqual([]);
  });

  test("returns empty array when conversation is empty", () => {
    const workItem = { conversation: [] } as unknown as WorkItem;
    expect(parseConversation(workItem)).toEqual([]);
  });

  test("filters out malformed entries", () => {
    const workItem = {
      conversation: [
        { role: "ai", content: "valid", timestamp: "2025-01-01T00:00:00Z" },
        { role: 123, content: "bad role type", timestamp: "2025-01-01T00:00:00Z" },
        { content: "missing role", timestamp: "2025-01-01T00:00:00Z" },
        null,
      ],
    } as unknown as WorkItem;

    const result = parseConversation(workItem);
    expect(result).toHaveLength(1);
    expect(result[0].content).toBe("valid");
  });
});
