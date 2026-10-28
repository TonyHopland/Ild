import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { screen, fireEvent, waitFor, act, within } from "@testing-library/react";
import type { ChatSessionSummary } from "../types";
import { emitHubEvent, openChatList, settle, showChatActions, signalRHub } from "../test-support";
import {
  backToChatList,
  chatServer,
  chatSession,
  chatSummary,
  openChatPanel,
  renderChatBubble,
  resumeChat,
  stubChatServices,
} from "../test-support.chatBubble";

vi.mock("../hooks/useSignalR", async () => (await import("../test-support")).fakeSignalRModule());
vi.mock("../utils/openLoopDocument", () => ({ getOpenLoopDocument: vi.fn() }));
vi.mock("../services/chatSessionStore", () => ({ setCurrentChatSessionId: vi.fn() }));

const services = stubChatServices();
const { chatService } = services;
const server = chatServer(services);

afterEach(() => {
  signalRHub.reset();
  vi.clearAllMocks();
  localStorage.clear();
});

function panel() {
  return screen.getByRole("dialog");
}

/** The rename draft's input, if one is open: any text box but the message box. */
function renameInput(): HTMLInputElement | undefined {
  const message = screen.queryByLabelText("Chat message");
  return within(panel())
    .queryAllByRole("textbox")
    .find((box) => box !== message) as HTMLInputElement | undefined;
}

function startRenamingRow(name: string): HTMLInputElement {
  showChatActions(name);
  fireEvent.click(screen.getByRole("button", { name: `Rename chat ${name}` }));
  return renameInput()!;
}

/** Enter as a browser delivers it: a form submits, a lone input sees the key. */
function pressEnter(input: HTMLInputElement) {
  if (input.form) fireEvent.submit(input.form);
  else fireEvent.keyDown(input, { key: "Enter", code: "Enter" });
}

describe("chat titles", () => {
  test("a title saved on the server replaces the open chat's header and the list row without a reload", async () => {
    server.chats = [chatSummary("a", "Could this be solved better by")];
    server.sessions.a = chatSession({ id: "a", name: "Could this be solved better by" });
    await renderChatBubble();
    await openChatPanel();
    await resumeChat("Could this be solved better by");

    server.update("a", { name: "Login page fix" });
    emitHubEvent("ChatTitleChanged", { chatSessionId: "a" });
    await within(panel()).findByText("Login page fix");
    expect(within(panel()).queryByText("Could this be solved better by")).toBeNull();

    await backToChatList();
    expect(screen.getByText("Login page fix")).toBeTruthy();

    server.update("a", { name: "Login page redirect loop" });
    emitHubEvent("ChatTitleChanged", { chatSessionId: "a" });
    await screen.findByText("Login page redirect loop");
    expect(screen.queryByText("Login page fix")).toBeNull();
  });

  test("a row is renamed inline: Enter saves, Cancel and Escape discard, and an empty name cannot be saved", async () => {
    server.chats = [chatSummary("a", "Alpha"), chatSummary("b", "Beta")];
    await renderChatBubble();
    await openChatPanel();
    await openChatList();
    await screen.findByText("Alpha");

    let input = startRenamingRow("Alpha");
    fireEvent.change(input, { target: { value: "Deploy loop wiring" } });
    pressEnter(input);
    await waitFor(() => expect(chatService.rename).toHaveBeenCalledWith("a", "Deploy loop wiring"));
    await screen.findByText("Deploy loop wiring");
    await waitFor(() => expect(renameInput()).toBeUndefined());

    input = startRenamingRow("Deploy loop wiring");
    fireEvent.change(input, { target: { value: "Thrown away" } });
    fireEvent.click(within(panel()).getByRole("button", { name: "Cancel" }));
    expect(renameInput()).toBeUndefined();

    input = startRenamingRow("Beta");
    fireEvent.change(input, { target: { value: "Thrown away too" } });
    fireEvent.keyDown(input, { key: "Escape", code: "Escape" });
    expect(renameInput()).toBeUndefined();

    input = startRenamingRow("Beta");
    fireEvent.change(input, { target: { value: "   " } });
    expect(
      (within(panel()).getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled,
    ).toBe(true);
    pressEnter(input);
    await settle();

    expect(chatService.rename).toHaveBeenCalledTimes(1);
    expect(screen.getByText("Deploy loop wiring")).toBeTruthy();
    expect(screen.queryByText("Thrown away")).toBeNull();
    expect(screen.queryByText("Thrown away too")).toBeNull();
  });

  test("a failed rename shows an error and keeps the old title", async () => {
    server.chats = [chatSummary("a", "Alpha")];
    chatService.rename.mockRejectedValue(new Error("Name is taken"));
    await renderChatBubble();
    await openChatPanel();
    await openChatList();
    await screen.findByText("Alpha");

    const input = startRenamingRow("Alpha");
    fireEvent.change(input, { target: { value: "Deploy loop wiring" } });
    pressEnter(input);

    await within(panel()).findByText("Name is taken");
    const draft = renameInput();
    if (draft) fireEvent.keyDown(draft, { key: "Escape", code: "Escape" });
    expect(screen.getByText("Alpha")).toBeTruthy();
    expect(screen.queryByText("Deploy loop wiring")).toBeNull();
  });

  test("switching chat discards an unsaved rename", async () => {
    server.chats = [chatSummary("a", "Alpha"), chatSummary("b", "Beta")];
    server.sessions.a = chatSession({ id: "a", name: "Alpha" });
    server.sessions.b = chatSession({ id: "b", name: "Beta" });
    await renderChatBubble();
    await openChatPanel();
    await resumeChat("Alpha");
    await backToChatList();
    expect(screen.getByText("Alpha")).toBeTruthy();

    fireEvent.change(startRenamingRow("Alpha"), { target: { value: "Unsaved row name" } });
    await resumeChat("Beta");
    expect(renameInput()).toBeUndefined();
    await backToChatList();
    expect(renameInput()).toBeUndefined();

    await resumeChat("Alpha");
    expect(renameInput()).toBeUndefined();
    expect(within(panel()).getByText("Alpha")).toBeTruthy();
    expect(chatService.rename).not.toHaveBeenCalled();
  });
});

describe("a rename being saved", () => {
  test("cannot be edited while its save is out, so what is saved is what was shown", async () => {
    server.chats = [chatSummary("a", "Alpha")];
    let saved!: () => void;
    chatService.rename.mockImplementation(
      (id: string, name: string) =>
        new Promise<void>((resolve) => {
          saved = () => {
            server.update(id, { name: name });
            resolve();
          };
        }),
    );
    await renderChatBubble();
    await openChatPanel();
    await openChatList();
    await screen.findByText("Alpha");

    const input = startRenamingRow("Alpha");
    fireEvent.change(input, { target: { value: "Deploy loop wiring" } });
    pressEnter(input);
    await waitFor(() => expect(chatService.rename).toHaveBeenCalledWith("a", "Deploy loop wiring"));

    expect(renameInput()!.readOnly).toBe(true);

    await act(async () => saved());
    await screen.findByText("Deploy loop wiring");
    await waitFor(() => expect(renameInput()).toBeUndefined());
  });

  test("can be edited again once a save fails", async () => {
    server.chats = [chatSummary("a", "Alpha")];
    chatService.rename.mockRejectedValue(new Error("Name is taken"));
    await renderChatBubble();
    await openChatPanel();
    await openChatList();
    await screen.findByText("Alpha");

    const input = startRenamingRow("Alpha");
    fireEvent.change(input, { target: { value: "Deploy loop wiring" } });
    pressEnter(input);

    await within(panel()).findByText("Name is taken");
    expect(renameInput()!.readOnly).toBe(false);
    expect(within(panel()).getByRole("alert").textContent).toBe("Name is taken");
  });

  test("cancelled mid-save still holds its chat: a new rename of it waits for the first to settle", async () => {
    server.chats = [chatSummary("a", "Alpha")];
    const answers: (() => void)[] = [];
    chatService.rename.mockImplementation(
      (id: string, name: string) =>
        new Promise<void>((resolve) => {
          answers.push(() => {
            server.update(id, { name: name });
            resolve();
          });
        }),
    );
    await renderChatBubble();
    await openChatPanel();
    await openChatList();
    await screen.findByText("Alpha");

    let input = startRenamingRow("Alpha");
    fireEvent.change(input, { target: { value: "First name" } });
    pressEnter(input);
    await waitFor(() => expect(chatService.rename).toHaveBeenCalledTimes(1));
    fireEvent.click(within(panel()).getByRole("button", { name: "Cancel" }));

    input = startRenamingRow("Alpha");
    fireEvent.change(input, { target: { value: "Second name" } });
    expect(
      (within(panel()).getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled,
    ).toBe(true);
    pressEnter(input);
    await act(async () => {});
    expect(chatService.rename).toHaveBeenCalledTimes(1);

    await act(async () => answers[0]());
    await waitFor(() =>
      expect(
        (within(panel()).getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled,
      ).toBe(false),
    );
    expect(renameInput()!.value).toBe("Second name");
    pressEnter(renameInput()!);
    await waitFor(() => expect(chatService.rename).toHaveBeenCalledTimes(2));
    expect(chatService.rename).toHaveBeenLastCalledWith("a", "Second name");
  });

  test.each([
    ["stalls", () => new Promise<ChatSessionSummary[]>(() => {})],
    ["fails", () => Promise.reject(new Error("history unavailable"))],
  ])(
    "taken by the server is shown at once, even when the re-read after it %s",
    async (_, reread) => {
      server.chats = [chatSummary("a", "Alpha")];
      await renderChatBubble();
      await openChatPanel();
      await openChatList();
      await screen.findByText("Alpha");
      chatService.listHistory.mockImplementation(reread);

      const input = startRenamingRow("Alpha");
      fireEvent.change(input, { target: { value: "Deploy loop wiring" } });
      pressEnter(input);

      await screen.findByText("Deploy loop wiring");
      await waitFor(() => expect(renameInput()).toBeUndefined());
      expect(screen.queryByText("Alpha")).toBeNull();
    },
  );

  test("is sent and shown as the server stores it: trimmed, without NUL characters", async () => {
    server.chats = [chatSummary("a", "Alpha")];
    await renderChatBubble();
    await openChatPanel();
    await openChatList();
    await screen.findByText("Alpha");
    chatService.listHistory.mockImplementation(() => new Promise<ChatSessionSummary[]>(() => {}));

    const input = startRenamingRow("Alpha");
    fireEvent.change(input, { target: { value: "  Deploy\u0000 loop  " } });
    pressEnter(input);

    await waitFor(() => expect(chatService.rename).toHaveBeenCalledWith("a", "Deploy loop"));
    await screen.findByText("Deploy loop");
  });
});

describe("a rename draft", () => {
  test("cancelled with Escape leaves the page's own Escape alone, so an open work item stays open", async () => {
    server.chats = [chatSummary("a", "Alpha")];
    const pageEscape = vi.fn();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") pageEscape();
    };
    document.addEventListener("keydown", onKey);
    try {
      await renderChatBubble();
      await openChatPanel();
      await openChatList();
      await screen.findByText("Alpha");

      fireEvent.keyDown(startRenamingRow("Alpha"), { key: "Escape", code: "Escape" });

      expect(renameInput()).toBeUndefined();
      expect(pageEscape).not.toHaveBeenCalled();
      expect(chatService.rename).not.toHaveBeenCalled();
    } finally {
      document.removeEventListener("keydown", onKey);
    }
  });
});
