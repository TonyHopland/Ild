import type { ChatMessage } from "../types";
import { compareCreatedAt } from "./createdAt";

/** Transcript order, as the server reads it: by sequence, then by save time where two share one. */
export const byTranscriptOrder = (a: ChatMessage, b: ChatMessage) =>
  a.sequence - b.sequence || compareCreatedAt(a.createdAt, b.createdAt);
