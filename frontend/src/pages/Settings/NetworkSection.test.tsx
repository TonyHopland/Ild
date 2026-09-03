import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, fireEvent, waitFor, within } from "@testing-library/react";
import NetworkSection from "./NetworkSection";
import * as signalRHook from "../../hooks/useSignalR";
import * as services from "../../services/auth";
import type { NetworkLogEntry, NetworkPolicyEntry, NetworkStatus } from "../../types";

type Handler = (message: { type: string; payload: unknown; timestamp: string }) => void;
const handlers = new Map<string, Set<Handler>>();

function emit(type: string, payload: unknown) {
  handlers.get(type)?.forEach((h) => h({ type, payload, timestamp: new Date().toISOString() }));
}

const enforced: NetworkStatus = {
  enforcement: "enforced",
  reason: "nftables rules drop everything uid 10002 sends except loopback and DNS.",
  proxyEnabled: true,
  proxyPort: 3128,
};
const advisory: NetworkStatus = {
  enforcement: "advisory",
  reason: "NET_ADMIN is not granted to the container.",
  proxyEnabled: true,
  proxyPort: 3128,
};

const github: NetworkPolicyEntry = {
  id: "e1",
  host: ".github.com",
  listKind: "Whitelist",
  aiProviderId: null,
  createdAt: "2026-09-01T10:00:00Z",
};
const evil: NetworkPolicyEntry = {
  id: "e2",
  host: "evil.example",
  listKind: "Blacklist",
  aiProviderId: "p1",
  createdAt: "2026-09-01T10:00:00Z",
};
const npmLine: NetworkLogEntry = {
  id: "l1",
  host: "registry.npmjs.org",
  port: 443,
  timestamp: "2026-09-02T12:00:00Z",
  decision: "Blocked",
  aiProviderId: "p1",
};

function mockServices(status: NetworkStatus = enforced, mode = "off") {
  vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
    connectionState: "connected",
    on: vi.fn((type: string, handler: Handler) => {
      const set = handlers.get(type) ?? new Set<Handler>();
      set.add(handler);
      handlers.set(type, set);
    }),
    off: vi.fn((type: string, handler: Handler) => {
      handlers.get(type)?.delete(handler);
    }),
    invoke: vi.fn(),
  } as any);
  vi.spyOn(services.networkService, "getStatus").mockResolvedValue(status);
  vi.spyOn(services.networkService, "getEntries").mockResolvedValue([github, evil]);
  vi.spyOn(services.networkService, "getLog").mockResolvedValue([npmLine]);
  vi.spyOn(services.settingsService, "get").mockResolvedValue({
    key: services.NetworkSettingKeys.Mode,
    value: mode,
  });
  vi.spyOn(services.aiProviderService, "getAll").mockResolvedValue([
    { id: "p1", name: "Claude", type: "claude-code" } as any,
  ]);
}

beforeEach(() => handlers.clear());
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("Network settings", () => {
  test("shows the lists with their scope and the log with its decisions", async () => {
    mockServices();
    render(<NetworkSection />);

    const whitelist = await screen.findByLabelText("Whitelist entries");
    expect(within(whitelist).getByText(".github.com")).toBeTruthy();
    expect(within(whitelist).getByText("all providers")).toBeTruthy();
    const blacklist = screen.getByLabelText("Blacklist entries");
    expect(within(blacklist).getByText("evil.example")).toBeTruthy();
    expect(within(blacklist).getByText("Claude")).toBeTruthy();

    expect(screen.getByText("registry.npmjs.org")).toBeTruthy();
    expect(screen.getByText("Blocked")).toBeTruthy();
    expect(screen.queryByRole("status")).toBeNull();
  });

  test("shows a banner when enforcement is advisory", async () => {
    mockServices(advisory);
    render(<NetworkSection />);

    const banner = await screen.findByRole("status");
    expect(banner.textContent).toMatch(/advisory mode/i);
    expect(banner.textContent).toContain("NET_ADMIN is not granted");
  });

  test("saves the mode and reverts when the save fails", async () => {
    mockServices(enforced, "off");
    const put = vi
      .spyOn(services.settingsService, "put")
      .mockResolvedValueOnce({ key: services.NetworkSettingKeys.Mode, value: "whitelist" })
      .mockRejectedValueOnce({
        status: 400,
        message: "network.mode must be off, whitelist or blacklist",
      });
    render(<NetworkSection />);

    const select = (await screen.findByLabelText(/filter mode/i)) as HTMLSelectElement;
    await waitFor(() => expect(select.value).toBe("off"));

    fireEvent.change(select, { target: { value: "whitelist" } });
    await waitFor(() =>
      expect(put).toHaveBeenCalledWith(services.NetworkSettingKeys.Mode, "whitelist"),
    );
    expect(select.value).toBe("whitelist");

    fireEvent.change(select, { target: { value: "blacklist" } });
    await screen.findByText(/must be off, whitelist or blacklist/i);
    expect(select.value).toBe("whitelist");
  });

  test("adds a host to the whitelist, scoped to a provider", async () => {
    mockServices();
    const created: NetworkPolicyEntry = {
      id: "e3",
      host: "api.anthropic.com",
      listKind: "Whitelist",
      aiProviderId: "p1",
      createdAt: "2026-09-02T12:00:00Z",
    };
    const add = vi.spyOn(services.networkService, "addEntry").mockResolvedValue(created);
    render(<NetworkSection />);

    const input = await screen.findByLabelText("Host to add to the whitelist");
    fireEvent.change(input, { target: { value: "api.anthropic.com" } });
    fireEvent.change(screen.getByLabelText("Scope for the new whitelist entry"), {
      target: { value: "p1" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Add to whitelist" }));

    await waitFor(() =>
      expect(add).toHaveBeenCalledWith({
        host: "api.anthropic.com",
        listKind: "Whitelist",
        aiProviderId: "p1",
      }),
    );
    const whitelist = screen.getByLabelText("Whitelist entries");
    expect(await within(whitelist).findByText("api.anthropic.com")).toBeTruthy();
  });

  test("shows the server's reason when a host is refused", async () => {
    mockServices();
    vi.spyOn(services.networkService, "addEntry").mockRejectedValue({
      status: 400,
      message: "Enter a host name, not a URL",
    });
    render(<NetworkSection />);

    fireEvent.change(await screen.findByLabelText("Host to add to the blacklist"), {
      target: { value: "https://evil.example" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Add to blacklist" }));

    expect(await screen.findByText("Enter a host name, not a URL")).toBeTruthy();
  });

  test("removes an entry", async () => {
    mockServices();
    const remove = vi.spyOn(services.networkService, "deleteEntry").mockResolvedValue(undefined);
    render(<NetworkSection />);

    fireEvent.click(
      await screen.findByRole("button", { name: "Remove .github.com from the whitelist" }),
    );

    await waitFor(() => expect(remove).toHaveBeenCalledWith("e1"));
    await waitFor(() => expect(screen.queryByText(".github.com")).toBeNull());
  });

  test("promotes a log line to either list", async () => {
    mockServices();
    const promoted: NetworkPolicyEntry = {
      id: "e4",
      host: "registry.npmjs.org",
      listKind: "Whitelist",
      aiProviderId: null,
      createdAt: "2026-09-02T12:00:00Z",
    };
    const promote = vi
      .spyOn(services.networkService, "addLogEntryToList")
      .mockResolvedValue(promoted);
    render(<NetworkSection />);

    fireEvent.click(
      await screen.findByRole("button", { name: "Add registry.npmjs.org to whitelist" }),
    );
    await waitFor(() => expect(promote).toHaveBeenCalledWith("l1", "Whitelist"));
    const whitelist = screen.getByLabelText("Whitelist entries");
    expect(await within(whitelist).findByText("registry.npmjs.org")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Add registry.npmjs.org to blacklist" }));
    await waitFor(() => expect(promote).toHaveBeenCalledWith("l1", "Blacklist"));
  });

  test("clears the log", async () => {
    mockServices();
    const clear = vi.spyOn(services.networkService, "clearLog").mockResolvedValue({ removed: 1 });
    render(<NetworkSection />);

    fireEvent.click(await screen.findByRole("button", { name: /clear log/i }));

    await waitFor(() => expect(clear).toHaveBeenCalled());
    expect(await screen.findByText(/no destinations recorded yet/i)).toBeTruthy();
    expect((screen.getByRole("button", { name: /clear log/i }) as HTMLButtonElement).disabled).toBe(
      true,
    );
  });

  test("a load failure is shown and cleared by the next successful load", async () => {
    mockServices();
    vi.spyOn(services.networkService, "getEntries")
      .mockRejectedValueOnce({ status: 500, message: "database unavailable" })
      .mockResolvedValue([github]);
    render(<NetworkSection />);

    expect(await screen.findByText("database unavailable")).toBeTruthy();

    emit("NetworkPolicyChanged", {});
    await waitFor(() => expect(screen.queryByText("database unavailable")).toBeNull());
    expect(await screen.findByText(".github.com")).toBeTruthy();
  });

  test("live-updates the log and the lists from the hub", async () => {
    mockServices();
    render(<NetworkSection />);
    await screen.findByText("registry.npmjs.org");

    emit("NetworkLogAppended", {
      id: "l2",
      host: "api.anthropic.com",
      port: 443,
      timestamp: "2026-09-02T12:01:00Z",
      decision: "Allowed",
      aiProviderId: null,
    } satisfies NetworkLogEntry);
    expect(await screen.findByText("api.anthropic.com")).toBeTruthy();
    const rows = screen.getAllByRole("row").slice(1);
    expect(rows[0].textContent).toContain("api.anthropic.com");

    emit("NetworkLogCleared", {});
    expect(await screen.findByText(/no destinations recorded yet/i)).toBeTruthy();

    const getEntries = vi.spyOn(services.networkService, "getEntries").mockResolvedValue([github]);
    emit("NetworkPolicyChanged", {});
    await waitFor(() => expect(getEntries).toHaveBeenCalled());
    await waitFor(() => expect(screen.queryByText("evil.example")).toBeNull());
  });
});
