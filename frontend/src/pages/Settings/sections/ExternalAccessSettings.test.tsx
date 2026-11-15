import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import ExternalAccessSettings from "./ExternalAccessSettings";
import { externalGrantService } from "../../../services/externalGrants";
import type { ExternalAgentGrant, ExternalAgentGrantDetail } from "../../../types";

const inOneHour = () => new Date(Date.now() + 60 * 60_000).toISOString();

const grant = (overrides: Partial<ExternalAgentGrant> = {}): ExternalAgentGrant => ({
  id: "g1",
  name: "Claude Code",
  scope: "Full",
  createdBy: "admin",
  createdAt: new Date().toISOString(),
  expiresAt: inOneHour(),
  status: "active",
  ...overrides,
});

const renderPage = () =>
  render(
    <MemoryRouter>
      <ExternalAccessSettings />
    </MemoryRouter>,
  );

const writeText = vi.fn().mockResolvedValue(undefined);

beforeEach(() => {
  vi.spyOn(externalGrantService, "list").mockResolvedValue([]);
  Object.assign(navigator, { clipboard: { writeText } });
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("Share access with an AI", () => {
  test("creates an access and shows the token, the URLs and the instructions to copy", async () => {
    const created = grant({ name: "Claude Code" });
    const create = vi
      .spyOn(externalGrantService, "create")
      .mockResolvedValue({ grant: created, token: "ildg_abc123" });
    renderPage();

    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Claude Code" } });
    fireEvent.change(screen.getByLabelText("Lasts"), { target: { value: "1440" } });
    fireEvent.click(screen.getByRole("button", { name: "Create access" }));

    await screen.findByText("Access for Claude Code");
    expect(create).toHaveBeenCalledWith("Claude Code", 1440, "Full");
    expect(screen.getByText("ildg_abc123")).toBeTruthy();
    expect(screen.getByText(`${window.location.origin}/mcp`)).toBeTruthy();
    const instructions = screen.getByLabelText("Instructions for the AI") as HTMLTextAreaElement;
    expect(instructions.value).toContain("Authorization: Bearer ildg_abc123");

    fireEvent.click(screen.getByRole("button", { name: "Copy everything" }));
    await waitFor(() => expect(writeText).toHaveBeenCalledWith(instructions.value));
  });

  test("offers control of work, says what it allows before it is picked, and creates with it", async () => {
    const create = vi
      .spyOn(externalGrantService, "create")
      .mockResolvedValue({ grant: grant({ name: "Driver", scope: "Control" }), token: "ildg_ctl" });
    renderPage();

    expect(screen.queryByText(/answer its human-feedback questions/)).toBeNull();
    fireEvent.change(screen.getByLabelText("Rights"), { target: { value: "Control" } });
    expect(screen.getByText(/answer its human-feedback questions/)).toBeTruthy();
    expect(screen.getByText(/spends AI provider money/)).toBeTruthy();

    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Driver" } });
    fireEvent.click(screen.getByRole("button", { name: "Create access" }));
    await screen.findByText("Access for Driver");
    expect(create).toHaveBeenCalledWith("Driver", expect.any(Number), "Control");
  });

  test("badges a Control grant on its row", async () => {
    vi.spyOn(externalGrantService, "list").mockResolvedValue([grant({ scope: "Control" })]);
    renderPage();

    expect(await screen.findByText("control of work")).toBeTruthy();
  });

  test("will not create an access with no name", () => {
    renderPage();

    expect(
      (screen.getByRole("button", { name: "Create access" }) as HTMLButtonElement).disabled,
    ).toBe(true);
  });

  test("forgets the token once it is dismissed", async () => {
    vi.spyOn(externalGrantService, "create").mockResolvedValue({
      grant: grant(),
      token: "ildg_once",
    });
    renderPage();
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "x" } });
    fireEvent.click(screen.getByRole("button", { name: "Create access" }));
    await screen.findByText("ildg_once");

    fireEvent.click(screen.getByRole("button", { name: "Done" }));

    expect(screen.queryByText("ildg_once")).toBeNull();
  });

  test("lists what was shared with its time left and last use, and revokes a live one", async () => {
    const list = vi
      .spyOn(externalGrantService, "list")
      .mockResolvedValueOnce([
        grant({ lastUsedAt: new Date().toISOString(), lastUsedFrom: "203.0.113.7" }),
        grant({
          id: "g2",
          name: "Old session",
          status: "revoked",
          revokedAt: new Date().toISOString(),
        }),
      ])
      .mockResolvedValue([grant({ status: "revoked", revokedAt: new Date().toISOString() })]);
    const revoke = vi.spyOn(externalGrantService, "revoke").mockResolvedValue(undefined);
    renderPage();

    await screen.findByText("Claude Code");
    expect(screen.getByText(/left · last used just now from 203\.0\.113\.7/)).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Revoke Old session" })).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: "Revoke Claude Code" }));

    await waitFor(() => expect(revoke).toHaveBeenCalledWith("g1"));
    await waitFor(() => expect(list).toHaveBeenCalledTimes(2));
  });

  test("revokes everything at once", async () => {
    vi.spyOn(externalGrantService, "list").mockResolvedValue([grant()]);
    const revokeAll = vi.spyOn(externalGrantService, "revokeAll").mockResolvedValue({ revoked: 1 });
    renderPage();
    await screen.findByText("Claude Code");

    fireEvent.click(screen.getByRole("button", { name: "Revoke all" }));

    await waitFor(() => expect(revokeAll).toHaveBeenCalled());
  });

  test("shows a grant's pending proposals, linked to where they are decided, and its activity", async () => {
    vi.spyOn(externalGrantService, "list").mockResolvedValue([grant()]);
    const detail: ExternalAgentGrantDetail = {
      grant: grant(),
      pendingProposals: [
        {
          kind: "workItemEdit",
          id: "p1",
          target: "WI-7",
          summary: "Retitle the login bug",
          rationale: "clearer",
          createdAt: new Date().toISOString(),
        },
      ],
      activity: [
        {
          at: new Date().toISOString(),
          method: "GET",
          path: "/api/v1/agent/repositories",
          statusCode: 200,
        },
        { at: new Date().toISOString(), method: "GET", path: "/api/v1/settings", statusCode: 403 },
      ],
    };
    vi.spyOn(externalGrantService, "get").mockResolvedValue(detail);
    renderPage();
    await screen.findByText("Claude Code");

    fireEvent.click(screen.getByRole("button", { name: "Details" }));

    const link = await screen.findByRole("link", { name: "Retitle the login bug" });
    expect(link.getAttribute("href")).toBe("/taskboard/WI-7");
    const activity = screen.getByText("Recent activity").parentElement!;
    expect(within(activity).getByText("403")).toBeTruthy();
  });

  test("renews a grant with a new lifetime and rights and shows the new token once", async () => {
    const list = vi
      .spyOn(externalGrantService, "list")
      .mockResolvedValueOnce([
        grant({ status: "revoked", revokedAt: new Date().toISOString(), scope: "Full" }),
      ])
      .mockResolvedValue([grant({ scope: "ReadOnly" })]);
    const renew = vi.spyOn(externalGrantService, "renew").mockResolvedValue({
      grant: grant({ scope: "ReadOnly" }),
      token: "ildg_renewed",
    });
    renderPage();
    await screen.findByText("Claude Code");

    fireEvent.click(screen.getByRole("button", { name: "Renew Claude Code" }));
    const dialog = await screen.findByRole("dialog", { name: "Renew Claude Code" });
    expect(within(dialog).getByText(/previous token stops working immediately/)).toBeTruthy();
    fireEvent.change(within(dialog).getByLabelText("Lasts"), { target: { value: "10080" } });
    fireEvent.change(within(dialog).getByLabelText("Rights"), { target: { value: "ReadOnly" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Renew and show new token" }));

    await screen.findByText("ildg_renewed");
    expect(renew).toHaveBeenCalledWith("g1", { lifetimeMinutes: 10080, scope: "ReadOnly" });
    expect(screen.queryByRole("dialog")).toBeNull();
    await waitFor(() => expect(list).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(screen.getByText("active")).toBeTruthy());

    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    expect(screen.queryByText("ildg_renewed")).toBeNull();
  });

  test("cancelling the renewal leaves the grant alone", async () => {
    vi.spyOn(externalGrantService, "list").mockResolvedValue([grant()]);
    const renew = vi.spyOn(externalGrantService, "renew");
    renderPage();
    await screen.findByText("Claude Code");

    fireEvent.click(screen.getByRole("button", { name: "Renew Claude Code" }));
    fireEvent.click(await screen.findByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog")).toBeNull();
    expect(renew).not.toHaveBeenCalled();
  });

  test("says so when loading fails", async () => {
    vi.spyOn(externalGrantService, "list").mockRejectedValue(new Error("boom"));

    renderPage();

    expect(await screen.findByText("boom")).toBeTruthy();
  });
});
