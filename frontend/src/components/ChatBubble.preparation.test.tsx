import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { screen, fireEvent, act } from "@testing-library/react";
import { settle, signalRHub } from "../test-support";
import {
  chatSession,
  emitChatEvent as emit,
  openResumedChat,
  stubChatServices,
} from "../test-support.chatBubble";

vi.mock("../hooks/useSignalR", async () => (await import("../test-support")).fakeSignalRModule());
vi.mock("../utils/openLoopDocument", () => ({ getOpenLoopDocument: vi.fn() }));
vi.mock("../services/chatSessionStore", () => ({ setCurrentChatSessionId: vi.fn() }));

const { chatService } = stubChatServices();

afterEach(() => {
  signalRHub.reset();
  vi.clearAllMocks();
  localStorage.clear();
});

describe("ChatBubble while the chat's container is being prepared", () => {
  test("the message box stays usable and the notice goes when the container is ready", async () => {
    await openResumedChat(chatSession({ preparing: true }));
    await settle();

    expect(screen.getByText(/Preparing the chat/)).toBeTruthy();
    const input = screen.getByLabelText("Chat message") as HTMLTextAreaElement;
    expect(input.disabled).toBe(false);

    chatService.sendMessage.mockResolvedValue("t1");
    fireEvent.change(input, { target: { value: "hello" } });
    fireEvent.click(screen.getByText("Send"));
    await settle();
    expect(chatService.sendMessage).toHaveBeenCalledTimes(1);

    act(() => emit("ChatPrepared", { chatSessionId: "s1" }));
    expect(screen.queryByText(/Preparing the chat/)).toBeNull();
  });

  test("a failed preparation shows the error and puts the sent message back in the box", async () => {
    await openResumedChat(chatSession({ preparing: true }));
    await settle();

    chatService.sendMessage.mockResolvedValue("t1");
    const input = screen.getByLabelText("Chat message") as HTMLTextAreaElement;
    fireEvent.change(input, { target: { value: "hello" } });
    fireEvent.click(screen.getByText("Send"));
    await settle();
    expect(input.value).toBe("");

    act(() => emit("ChatPreparationFailed", { chatSessionId: "s1", error: "no room" }));

    expect(screen.getByRole("alert").textContent).toContain("no room");
    expect((screen.getByLabelText("Chat message") as HTMLTextAreaElement).value).toBe("hello");
    expect(screen.queryByText(/Preparing the chat/)).toBeNull();
  });
});
