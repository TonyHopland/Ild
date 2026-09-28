import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, fireEvent, waitFor, within } from "@testing-library/react";
import PackageFeedsSettings from "./PackageFeedsSettings";
import * as services from "../../../services/auth";

const company = {
  id: "f1",
  name: "company",
  feedUrl: "https://pkgs.dev.azure.com/example-org/_packaging/company",
  patHint: "••••3fa9",
  createdAt: "2026-09-01T10:00:00Z",
  updatedAt: "2026-09-01T10:00:00Z",
};
const tools = {
  id: "f2",
  name: "tools",
  feedUrl: "https://pkgs.dev.azure.com/example-org/example-project/_packaging/tools",
  patHint: "••••",
  createdAt: "2026-09-02T10:00:00Z",
  updatedAt: "2026-09-02T10:00:00Z",
};

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

/** The smallest element around a feed's name that holds that feed's own Test button. */
async function rowOf(name: string) {
  let el: HTMLElement | null = await screen.findByText(name);
  while (el && !within(el).queryByRole("button", { name: /^test$/i })) el = el.parentElement;
  expect(el).toBeTruthy();
  return within(el!);
}

const feedUrlField = () => screen.getByLabelText(/feed url/i) as HTMLInputElement;
const patField = () => screen.getByLabelText(/\bPAT\b|personal access token/i) as HTMLInputElement;

async function openAddForm() {
  await screen.findByText("company");
  if (!screen.queryByLabelText(/feed url/i)) {
    fireEvent.click(screen.getByRole("button", { name: /^(\+\s*)?(add|new)\b/i }));
  }
  await screen.findByLabelText(/feed url/i);
}

function submitForm() {
  const buttons = screen.getAllByRole("button", {
    name: /^(save|create|add|update)( feed)?$/i,
  });
  fireEvent.click(buttons[buttons.length - 1]);
}

/** Answers a confirmation, if deleting asks for one. */
function confirmIfAsked() {
  const dialog = screen.queryByRole("dialog");
  const scope = dialog ? within(dialog) : screen;
  const confirm = scope.queryAllByRole("button", { name: /^(confirm|delete|yes|remove)/i });
  if (dialog || confirm.length > 0) {
    const last = confirm[confirm.length - 1];
    if (last) fireEvent.click(last);
  }
}

describe("Package feeds settings", () => {
  test("lists each feed with its URL and masked PAT, never the PAT itself", async () => {
    vi.spyOn(services.packageFeedService, "list").mockResolvedValue([company, tools] as any);
    render(<PackageFeedsSettings />);

    const row = await rowOf("company");
    expect(row.getByText(company.feedUrl)).toBeTruthy();
    expect(row.getByText("••••3fa9")).toBeTruthy();
    expect(row.getByRole("button", { name: /^edit$/i })).toBeTruthy();
    expect(row.getByRole("button", { name: /^delete$/i })).toBeTruthy();
    expect((await rowOf("tools")).getByText(tools.feedUrl)).toBeTruthy();
  });

  test("adds a feed with a password PAT field and a scope hint, and shows it once saved", async () => {
    vi.spyOn(services.packageFeedService, "list")
      .mockResolvedValueOnce([company] as any)
      .mockResolvedValue([company, tools] as any);
    const create = vi.spyOn(services.packageFeedService, "create").mockResolvedValue(tools as any);
    render(<PackageFeedsSettings />);

    await openAddForm();
    expect(screen.getAllByText(/Packaging \(Read\)/).length).toBeGreaterThan(0);
    expect(patField().type).toBe("password");
    fireEvent.change(screen.getByLabelText(/^name/i), { target: { value: "tools" } });
    fireEvent.change(feedUrlField(), { target: { value: tools.feedUrl } });
    fireEvent.change(patField(), { target: { value: "new-pat-value" } });
    submitForm();

    await waitFor(() =>
      expect(create).toHaveBeenCalledWith(
        expect.objectContaining({ name: "tools", feedUrl: tools.feedUrl, pat: "new-pat-value" }),
      ),
    );
    expect((await rowOf("tools")).getByText(tools.feedUrl)).toBeTruthy();
  });

  test("shows the server's reason when a save is refused", async () => {
    vi.spyOn(services.packageFeedService, "list").mockResolvedValue([company] as any);
    vi.spyOn(services.packageFeedService, "create").mockRejectedValue({
      status: 400,
      message: "Feed URL must look like https://pkgs.dev.azure.com/{org}/_packaging/{feed}.",
    });
    render(<PackageFeedsSettings />);

    await openAddForm();
    fireEvent.change(screen.getByLabelText(/^name/i), { target: { value: "bad" } });
    fireEvent.change(feedUrlField(), { target: { value: "https://example.com/feed" } });
    fireEvent.change(patField(), { target: { value: "p" } });
    submitForm();

    expect(await screen.findByText(/Feed URL must look like/)).toBeTruthy();
  });

  test("edits a feed without showing or resending its PAT, and keeps its name", async () => {
    vi.spyOn(services.packageFeedService, "list").mockResolvedValue([company] as any);
    const update = vi
      .spyOn(services.packageFeedService, "update")
      .mockResolvedValue({ ...company, updatedAt: "2026-09-03T10:00:00Z" } as any);
    render(<PackageFeedsSettings />);

    fireEvent.click((await rowOf("company")).getByRole("button", { name: /^edit$/i }));

    await waitFor(() => expect(feedUrlField().value).toBe(company.feedUrl));
    expect(patField().value).toBe("");
    expect(patField().type).toBe("password");
    expect(patField().placeholder).toBe("(unchanged)");
    const name = screen.queryByLabelText(/^name/i) as HTMLInputElement | null;
    expect(name === null || name.disabled || name.readOnly).toBe(true);

    const moved = "https://pkgs.dev.azure.com/example-org/example-project/_packaging/company";
    fireEvent.change(feedUrlField(), { target: { value: moved } });
    submitForm();

    await waitFor(() => expect(update).toHaveBeenCalledTimes(1));
    const [id, payload] = update.mock.calls[0] as unknown as [
      string,
      { feedUrl: string; pat?: string },
    ];
    expect(id).toBe("f1");
    expect(payload.feedUrl).toBe(moved);
    expect(payload.pat ?? "").toBe("");
  });

  test("deletes a feed", async () => {
    vi.spyOn(services.packageFeedService, "list")
      .mockResolvedValueOnce([company, tools] as any)
      .mockResolvedValue([tools] as any);
    const remove = vi
      .spyOn(services.packageFeedService, "remove")
      .mockResolvedValue(undefined as any);
    render(<PackageFeedsSettings />);

    fireEvent.click((await rowOf("company")).getByRole("button", { name: /^delete$/i }));
    confirmIfAsked();

    await waitFor(() => expect(remove).toHaveBeenCalledWith("f1"));
    await waitFor(() => expect(screen.queryByText("company")).toBeNull());
  });

  test("tests a saved feed and shows what the test found in that row", async () => {
    vi.spyOn(services.packageFeedService, "list").mockResolvedValue([company, tools] as any);
    const run = vi.spyOn(services.packageFeedService, "test").mockResolvedValue({
      ok: false,
      outcome: "InvalidApiKey",
      message:
        "PAT rejected, probably expired or revoked. Create a new one with Packaging (Read) and paste it here.",
      detail: null,
    } as any);
    render(<PackageFeedsSettings />);

    const row = await rowOf("company");
    fireEvent.click(row.getByRole("button", { name: /^test$/i }));

    await waitFor(() => expect(run).toHaveBeenCalledWith("f1"));
    expect(await row.findByText(/PAT rejected, probably expired or revoked/)).toBeTruthy();
    expect((await rowOf("tools")).queryByText(/PAT rejected/)).toBeNull();
  });
});
