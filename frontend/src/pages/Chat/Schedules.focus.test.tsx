import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import type { ChatSchedule, ChatScheduleList } from "../../types";

const { chatScheduleService } = vi.hoisted(() => ({
  chatScheduleService: {
    list: vi.fn(),
    update: vi.fn(),
    create: vi.fn(),
    delete: vi.fn(),
    runNow: vi.fn(),
  },
}));

vi.mock("../../hooks/useSignalR", () => ({
  useSignalR: () => ({
    connectionState: "disconnected",
    on: () => {},
    off: () => {},
    invoke: () => Promise.resolve(),
  }),
}));

vi.mock("../../services/auth", () => ({
  chatScheduleService,
  repositoryService: { getEvery: () => Promise.resolve([]) },
  aiProviderService: { getAll: () => Promise.resolve([]) },
}));

import Schedules from "./Schedules";

const schedule: ChatSchedule = {
  id: "s1",
  name: "Weekly retro",
  prompt: "Review the week.",
  aiTag: null,
  cronExpression: "0 8 * * 1",
  timeZone: "UTC",
  enabled: true,
  repositoryScope: "All",
  repositoryIds: [],
  continueSession: true,
  nextFireAt: null,
  lastFiring: null,
  latestChatSessionId: null,
};

async function renderList() {
  chatScheduleService.list.mockResolvedValue({
    schedulerPaused: false,
    schedules: [schedule],
  } satisfies ChatScheduleList);
  render(
    <MemoryRouter>
      <Schedules />
    </MemoryRouter>,
  );
  return (await screen.findByText("Weekly retro")).closest("article") as HTMLElement;
}

/** A click as a person makes it: the button takes focus first. */
function press(button: HTMLElement) {
  button.focus();
  fireEvent.click(button);
}

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("Keyboard focus on the schedules list", () => {
  test("the delete confirmation takes focus, and gives it back to Delete when called off", async () => {
    const row = await renderList();

    press(within(row).getByRole("button", { name: "Delete" }));
    const confirm = within(row).getByRole("alertdialog");
    expect(document.activeElement).toBe(within(confirm).getByRole("button", { name: "Cancel" }));

    fireEvent.keyDown(confirm, { key: "Escape" });
    expect(within(row).queryByRole("alertdialog")).toBeNull();
    expect(document.activeElement).toBe(within(row).getByRole("button", { name: "Delete" }));

    press(within(row).getByRole("button", { name: "Delete" }));
    press(within(within(row).getByRole("alertdialog")).getByRole("button", { name: "Cancel" }));
    expect(document.activeElement).toBe(within(row).getByRole("button", { name: "Delete" }));
    expect(chatScheduleService.delete).not.toHaveBeenCalled();
  });

  test("the schedule form takes focus when it opens and gives it back to what opened it", async () => {
    const row = await renderList();

    const create = screen.getByRole("button", { name: "New schedule" });
    press(create);
    const dialog = await screen.findByRole("dialog");
    expect(document.activeElement).toBe(within(dialog).getByLabelText("Name"));
    fireEvent.keyDown(dialog, { key: "Escape" });
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(document.activeElement).toBe(create);

    const edit = within(row).getByRole("button", { name: "Edit" });
    press(edit);
    const editing = await screen.findByRole("dialog");
    expect(document.activeElement).toBe(within(editing).getByLabelText("Name"));
    press(within(editing).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(document.activeElement).toBe(edit);
  });
});
