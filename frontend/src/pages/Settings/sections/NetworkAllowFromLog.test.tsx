import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, fireEvent, waitFor, within } from "@testing-library/react";
import NetworkSettings from "./NetworkSettings";
import * as signalRHook from "../../../hooks/useSignalR";
import * as services from "../../../services/auth";
import type { NetworkLogDestination, NetworkPolicyEntry } from "../../../types";
import { WorkItemHubProvider } from "../../../hooks/useWorkItemHub";
import { fakeHub, routeHubs } from "../../../test-support";

const hub = fakeHub();

const destinations: NetworkLogDestination[] = [
  { host: "registry.example", count: 12, lastSeen: "2026-11-27T10:00:00Z", allowed: false },
  { host: "api.example", count: 3, lastSeen: "2026-11-26T10:00:00Z", allowed: false },
  { host: "known.example", count: 1, lastSeen: "2026-11-25T10:00:00Z", allowed: true },
];

function mockServices(mode = "monitor", entries: NetworkPolicyEntry[] = []) {
  vi.spyOn(services.settingsService, "getRunHost").mockResolvedValue({ kind: "container" });
  routeHubs(signalRHook, { "/hubs/work-item": hub });
  vi.spyOn(services.networkService, "getStatus").mockResolvedValue({
    enforcement: "enforced",
    reason: "",
    contained: true,
    proxyEnabled: true,
    proxyPort: 3128,
    directRoutes: [],
  });
  vi.spyOn(services.networkService, "getEntries").mockResolvedValue(entries);
  vi.spyOn(services.networkService, "getForwards").mockResolvedValue([]);
  vi.spyOn(services.networkService, "getProtected").mockResolvedValue([]);
  vi.spyOn(services.networkService, "getLog").mockResolvedValue([]);
  vi.spyOn(services.settingsService, "get").mockImplementation(async (key: string) => ({
    key,
    value: key === services.NetworkSettingKeys.Mode ? mode : "30",
  }));
  vi.spyOn(services.aiProviderService, "getAll").mockResolvedValue([
    { id: "p1", name: "Claude", type: "claude-code" } as any,
  ]);
  return vi.spyOn(services.networkService, "getLogDestinations").mockResolvedValue(destinations);
}

beforeEach(() => hub.reset());
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

const openDialog = async () => {
  fireEvent.click(
    (await screen.findAllByRole("button", { name: /add allowed hosts from the log/i }))[0],
  );
  return within(await screen.findByRole("dialog", { name: "Add allowed hosts from the log" }));
};

describe("Add allowed hosts from the log", () => {
  test("lists the hosts with their counts, leaves out the allowed ones and writes nothing until confirmed", async () => {
    mockServices();
    const addAllowed = vi.spyOn(services.networkService, "addAllowed").mockResolvedValue([]);
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });

    const dialog = await openDialog();

    const registry = (await dialog.findByText("registry.example")).closest("tr")!;
    expect(within(registry).getByText("×12")).toBeTruthy();
    expect(dialog.queryByText("known.example")).toBeNull();
    fireEvent.click(within(registry).getByLabelText("Allow registry.example"));
    expect(addAllowed).not.toHaveBeenCalled();
  });

  test("writes the ticked hosts, with the chosen match, in the chosen scope on confirm", async () => {
    const read = mockServices();
    const created: NetworkPolicyEntry = {
      id: "n1",
      host: "registry.example",
      listKind: "Whitelist",
      aiProviderId: "p1",
      createdAt: "2026-11-27T10:00:00Z",
    };
    const addAllowed = vi.spyOn(services.networkService, "addAllowed").mockResolvedValue([created]);
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });

    const dialog = await openDialog();
    await dialog.findByText("registry.example");
    fireEvent.change(dialog.getByLabelText("Scope to allow the hosts in"), {
      target: { value: "p1" },
    });
    await waitFor(() => expect(read).toHaveBeenLastCalledWith("provider", "p1", 7));
    await dialog.findByText("registry.example");
    fireEvent.click(dialog.getByLabelText("Allow registry.example"));
    fireEvent.change(dialog.getByLabelText("How to match registry.example"), {
      target: { value: "suffix" },
    });
    fireEvent.click(dialog.getByLabelText("Allow api.example"));
    fireEvent.click(dialog.getByRole("button", { name: /add 2 allowed hosts/i }));

    await waitFor(() =>
      expect(addAllowed).toHaveBeenCalledWith("provider", "p1", [
        ".registry.example",
        "api.example",
      ]),
    );
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(await screen.findByText("registry.example")).toBeTruthy();
  });

  test("cancelling writes nothing", async () => {
    mockServices();
    const addAllowed = vi.spyOn(services.networkService, "addAllowed").mockResolvedValue([]);
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });

    const dialog = await openDialog();
    fireEvent.click(await dialog.findByLabelText("Allow registry.example"));
    fireEvent.click(dialog.getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog")).toBeNull();
    expect(addAllowed).not.toHaveBeenCalled();
  });

  test("warns once when switching to whitelist leaves a scope with used hosts and no allow entries, and links to the dialog", async () => {
    const read = mockServices("monitor");
    vi.spyOn(services.settingsService, "put").mockResolvedValue({
      key: services.NetworkSettingKeys.Mode,
      value: "whitelist",
    });
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });

    fireEvent.click(
      within(await screen.findByRole("group", { name: "Filter mode" })).getByRole("button", {
        name: "Whitelist",
      }),
    );

    const warnings = await screen.findAllByText(
      /2 hosts were used in the last 7 days and aren't allowed/,
    );
    expect(warnings).toHaveLength(2);
    const banner = warnings[0].closest("[role=status]")!;
    expect(banner.textContent).toMatch(/Installers/);
    expect(banner.textContent).toMatch(/Claude/);
    expect(read).toHaveBeenCalledWith("provider", "p1", 7);
    fireEvent.click(
      within(warnings[1].closest("div")!).getByRole("button", { name: /add allowed hosts/i }),
    );

    expect(
      await screen.findByRole("dialog", { name: "Add allowed hosts from the log" }),
    ).toBeTruthy();
    expect(screen.queryByText(/hosts were used in the last 7 days/)).toBeNull();
  });

  test("does not warn about a scope that already has an allow entry", async () => {
    const read = mockServices("monitor", [
      {
        id: "e1",
        host: "registry.example",
        listKind: "Whitelist",
        aiProviderId: null,
        createdAt: "2026-11-27T10:00:00Z",
      },
    ]);
    vi.spyOn(services.settingsService, "put").mockResolvedValue({
      key: services.NetworkSettingKeys.Mode,
      value: "whitelist",
    });
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });

    await screen.findByText("registry.example");
    fireEvent.click(
      within(screen.getByRole("group", { name: "Filter mode" })).getByRole("button", {
        name: "Whitelist",
      }),
    );

    await waitFor(() => expect(services.settingsService.put).toHaveBeenCalled());
    expect(read).not.toHaveBeenCalled();
    expect(screen.queryByText(/hosts were used in the last 7 days/)).toBeNull();
  });
});
