import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, cleanup } from "@testing-library/react";
import ChatTranscript from "./ChatTranscript";
import { WorkItemHubProvider } from "../hooks/useWorkItemHub";
import * as signalRHook from "../hooks/useSignalR";
import { fakeHub, reply, routeHubs, stubFetch } from "../test-support";
import type { ChatMessage } from "../types";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const message: ChatMessage = {
  id: "m1",
  role: "assistant",
  content: "Preview: [open](https://example.com/preview)",
  interrupted: false,
  sequence: 1,
  createdAt: "2026-10-01T10:00:00Z",
};

describe("ChatTranscript links", () => {
  test("a finished reply and the streaming reply open links to other sites in a new tab", () => {
    routeHubs(signalRHook, { "/hubs/work-item": fakeHub() });
    stubFetch(() => reply(200, []));
    const previewUrl = `${window.location.protocol}//wi-42.${window.location.hostname}/`;
    const { container } = render(
      <ChatTranscript
        chatSessionId="c1"
        messages={[message]}
        streaming={`Streaming: [live](${previewUrl})`}
      />,
      { wrapper: WorkItemHubProvider },
    );

    const finished = container.querySelector('a[href="https://example.com/preview"]');
    const streaming = container.querySelector(`a[href="${previewUrl}"]`);
    expect(finished?.getAttribute("target")).toBe("_blank");
    expect(streaming?.getAttribute("target")).toBe("_blank");
  });
});
