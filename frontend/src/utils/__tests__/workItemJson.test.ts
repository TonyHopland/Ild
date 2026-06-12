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
      name: null,
    });
    expect(result[1]).toEqual({
      role: "human",
      content: "Approved",
      timestamp: "2025-01-01T01:00:00Z",
      name: null,
    });
  });

  test("carries the author name through, defaulting to null", () => {
    const workItem = {
      conversation: [
        { role: "ai", name: "AI Coder", content: "done", timestamp: "2025-01-01T00:00:00Z" },
        { role: "human", content: "ok", timestamp: "2025-01-01T01:00:00Z" },
      ],
    } as unknown as WorkItem;

    const result = parseConversation(workItem);
    expect(result[0].name).toBe("AI Coder");
    expect(result[1].name).toBeNull();
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

  test("drops the AI 'Human Input Needed' sentinel that precedes a human response", () => {
    const workItem = {
      conversation: [
        {
          role: "ai",
          name: "Review",
          content: "Human Input Needed",
          timestamp: "2025-01-01T00:00:00Z",
        },
        { role: "human", content: "Looks good", timestamp: "2025-01-01T01:00:00Z" },
      ],
    } as unknown as WorkItem;

    const result = parseConversation(workItem);
    expect(result).toHaveLength(1);
    expect(result[0]).toEqual({
      role: "human",
      content: "Looks good",
      timestamp: "2025-01-01T01:00:00Z",
      name: null,
    });
  });

  test("keeps a human message that happens to say 'Human Input Needed'", () => {
    const workItem = {
      conversation: [
        { role: "human", content: "Human Input Needed", timestamp: "2025-01-01T00:00:00Z" },
      ],
    } as unknown as WorkItem;

    const result = parseConversation(workItem);
    expect(result).toHaveLength(1);
    expect(result[0].role).toBe("human");
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
