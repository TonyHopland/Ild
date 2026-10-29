import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, fireEvent, waitFor, within } from "@testing-library/react";
import NetworkSettings, { groupConsecutive } from "./NetworkSettings";
import * as signalRHook from "../../../hooks/useSignalR";
import * as services from "../../../services/auth";
import type {
  NetworkForward,
  NetworkLogEntry,
  NetworkPolicyEntry,
  NetworkStatus,
} from "../../../types";
import { WorkItemHubProvider } from "../../../hooks/useWorkItemHub";
import { fakeHub, routeHubs } from "../../../test-support";

const hub = fakeHub();
const emit = hub.dispatch;

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

const postgres: NetworkForward = {
  id: "f1",
  name: "postgres",
  host: "postgres",
  port: 5432,
  localPort: 15432,
  createdAt: "2026-09-01T10:00:00Z",
  decision: "Advisory",
  listenError: null,
};

function mockServices(
  status: NetworkStatus = enforced,
  mode = "monitor",
  forwards: NetworkForward[] = [postgres],
) {
  routeHubs(signalRHook, { "/hubs/work-item": hub });
  vi.spyOn(services.networkService, "getStatus").mockResolvedValue(status);
  vi.spyOn(services.networkService, "getEntries").mockResolvedValue([github, evil]);
  vi.spyOn(services.networkService, "getForwards").mockResolvedValue(forwards);
  vi.spyOn(services.networkService, "getLog").mockResolvedValue([npmLine]);
  vi.spyOn(services.settingsService, "get").mockImplementation(async (key: string) => ({
    key,
    value: key === services.NetworkSettingKeys.Mode ? mode : "30",
  }));
  vi.spyOn(services.aiProviderService, "getAll").mockResolvedValue([
    { id: "p1", name: "Claude", type: "claude-code" } as any,
  ]);
}

const trafficLog = () => within(screen.getByLabelText("Traffic log"));
const forwardTable = async () => within(await screen.findByLabelText("Forwards"));
const forwardRow = async (name: string) =>
  within(
    (await screen.findByRole("button", { name: `Remove the ${name} forward` })).closest("tr")!,
  );
const modeButton = (name: string) =>
  within(screen.getByRole("group", { name: "Filter mode" })).getByRole("button", { name });

beforeEach(() => hub.reset());
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("Network settings", () => {
  test("shows both lists in one rule table with their scope", async () => {
    mockServices();
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });

    const rules = within(await screen.findByLabelText("Network rules"));
    const allow = rules.getByText(".github.com").closest("tr")!;
    expect(within(allow).getByText("Allow")).toBeTruthy();
    expect(within(allow).getByText("All providers")).toBeTruthy();
    expect(within(allow).getByText(/every subdomain/i)).toBeTruthy();

    const block = rules.getByText("evil.example").closest("tr")!;
    expect(within(block).getByText("Block")).toBeTruthy();
    expect(within(block).getByText("Claude")).toBeTruthy();
    expect(within(block).getByText(/exactly/i)).toBeTruthy();
  });

  test("marks the rules the current mode ignores", async () => {
    mockServices(enforced, "whitelist");
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });

    const rules = within(await screen.findByLabelText("Network rules"));
    await waitFor(() => expect(modeButton("Whitelist").getAttribute("aria-pressed")).toBe("true"));

    const block = rules.getByText("evil.example").closest("tr")!;
    expect(within(block).getByText("not in effect")).toBeTruthy();
    const allow = rules.getByText(".github.com").closest("tr")!;
    expect(within(allow).queryByText("not in effect")).toBeNull();
  });

  test("shows a banner when enforcement is advisory", async () => {
    mockServices(advisory);
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });

    const banner = await screen.findByRole("status");
    expect(banner.textContent).toMatch(/advisory mode/i);
    expect(banner.textContent).toContain("NET_ADMIN is not granted");
  });

  test("saves the mode and reverts when the save fails", async () => {
    mockServices(enforced, "monitor");
    const put = vi
      .spyOn(services.settingsService, "put")
      .mockResolvedValueOnce({ key: services.NetworkSettingKeys.Mode, value: "whitelist" })
      .mockRejectedValueOnce({
        status: 400,
        message: "network.mode must be off, monitor, whitelist or blacklist",
      });
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });

    await waitFor(() => expect(modeButton("Monitor").getAttribute("aria-pressed")).toBe("true"));

    fireEvent.click(modeButton("Whitelist"));
    await waitFor(() =>
      expect(put).toHaveBeenCalledWith(services.NetworkSettingKeys.Mode, "whitelist"),
    );
    expect(modeButton("Whitelist").getAttribute("aria-pressed")).toBe("true");

    fireEvent.click(modeButton("Blacklist"));
    await screen.findByText(/must be off, monitor, whitelist or blacklist/i);
    expect(modeButton("Whitelist").getAttribute("aria-pressed")).toBe("true");
  });

  test("adds a rule of either kind, scoped to a provider", async () => {
    mockServices();
    const created: NetworkPolicyEntry = {
      id: "e3",
      host: "api.anthropic.com",
      listKind: "Blacklist",
      aiProviderId: "p1",
      createdAt: "2026-09-02T12:00:00Z",
    };
    const add = vi.spyOn(services.networkService, "addEntry").mockResolvedValue(created);
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });

    const input = await screen.findByLabelText("Host pattern");
    fireEvent.change(input, { target: { value: "api.anthropic.com" } });
    fireEvent.click(within(screen.getByRole("group", { name: "Rule kind" })).getByText("Block"));
    fireEvent.change(screen.getByLabelText("Scope for the new rule"), { target: { value: "p1" } });
    fireEvent.click(screen.getByRole("button", { name: "Add rule" }));

    await waitFor(() =>
      expect(add).toHaveBeenCalledWith({
        host: "api.anthropic.com",
        listKind: "Blacklist",
        aiProviderId: "p1",
      }),
    );
    const rules = within(screen.getByLabelText("Network rules"));
    expect(await rules.findByText("api.anthropic.com")).toBeTruthy();
  });

  test("spells out what the pattern being typed will match", async () => {
    mockServices();
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });

    fireEvent.change(await screen.findByLabelText("Host pattern"), {
      target: { value: ".example.com" },
    });
    expect(screen.getByText(/matches example\.com and every subdomain of it/i)).toBeTruthy();
  });

  test("shows the server's reason when a host is refused", async () => {
    mockServices();
    vi.spyOn(services.networkService, "addEntry").mockRejectedValue({
      status: 400,
      message: "Enter a host name, not a URL",
    });
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });

    fireEvent.change(await screen.findByLabelText("Host pattern"), {
      target: { value: "https://evil.example" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Add rule" }));

    expect(await screen.findByText("Enter a host name, not a URL")).toBeTruthy();
  });

  test("filters the rules", async () => {
    mockServices();
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });

    await screen.findByText("evil.example");
    fireEvent.change(screen.getByLabelText("Filter rules"), { target: { value: "github" } });

    const rules = within(screen.getByLabelText("Network rules"));
    expect(rules.getByText(".github.com")).toBeTruthy();
    expect(rules.queryByText("evil.example")).toBeNull();
  });

  test("filters the rules down to one kind", async () => {
    mockServices();
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });
    await screen.findByText("evil.example");

    const kinds = within(screen.getByRole("group", { name: "Rule kind filter" }));
    fireEvent.click(kinds.getByRole("button", { name: "Allow" }));

    let rules = within(screen.getByLabelText("Network rules"));
    expect(rules.getByText(".github.com")).toBeTruthy();
    expect(rules.queryByText("evil.example")).toBeNull();

    fireEvent.click(kinds.getByRole("button", { name: "Block" }));

    rules = within(screen.getByLabelText("Network rules"));
    expect(rules.getByText("evil.example")).toBeTruthy();
    expect(rules.queryByText(".github.com")).toBeNull();

    fireEvent.click(kinds.getByRole("button", { name: "All" }));

    rules = within(screen.getByLabelText("Network rules"));
    expect(rules.getByText(".github.com")).toBeTruthy();
    expect(rules.getByText("evil.example")).toBeTruthy();
  });

  test("says a filter matched nothing rather than that there are no rules", async () => {
    mockServices();
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });
    await screen.findByText("evil.example");

    fireEvent.change(screen.getByLabelText("Filter rules"), { target: { value: "nothing" } });

    expect(screen.getByText(/no rule matches that filter/i)).toBeTruthy();
    expect(screen.queryByText(/no rules yet/i)).toBeNull();
  });

  test("removes an entry", async () => {
    mockServices();
    const remove = vi.spyOn(services.networkService, "deleteEntry").mockResolvedValue(undefined);
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });

    fireEvent.click(
      await screen.findByRole("button", { name: "Remove .github.com from the whitelist" }),
    );

    await waitFor(() => expect(remove).toHaveBeenCalledWith("e1"));
    await waitFor(() => expect(screen.queryByText(".github.com")).toBeNull());
  });

  test("promotes a log line and then reports the rule that covers it", async () => {
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
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });

    fireEvent.click(
      await screen.findByRole("button", { name: "Add registry.npmjs.org to whitelist" }),
    );
    await waitFor(() => expect(promote).toHaveBeenCalledWith("l1", "Whitelist"));

    const rules = within(screen.getByLabelText("Network rules"));
    expect(await rules.findByText("registry.npmjs.org")).toBeTruthy();
    expect(trafficLog().getByText(/covered by/i)).toBeTruthy();
    expect(
      screen.queryByRole("button", { name: "Add registry.npmjs.org to whitelist" }),
    ).toBeNull();
  });

  test("loads a host from the log into the rule form instead of committing it", async () => {
    mockServices();
    const add = vi.spyOn(services.networkService, "addEntry");
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });

    fireEvent.click(
      await screen.findByRole("button", { name: "Write a rule for registry.npmjs.org" }),
    );

    const host = screen.getByLabelText("Host pattern") as HTMLInputElement;
    expect(host.value).toBe("registry.npmjs.org");
    expect(add).not.toHaveBeenCalled();
  });

  test("the host loaded from the log can be widened before it is added", async () => {
    mockServices();
    const add = vi.spyOn(services.networkService, "addEntry").mockResolvedValue({
      id: "e9",
      host: ".npmjs.org",
      listKind: "Whitelist",
      aiProviderId: null,
      createdAt: "2026-09-02T12:00:00Z",
    });
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });

    fireEvent.click(
      await screen.findByRole("button", { name: "Write a rule for registry.npmjs.org" }),
    );
    fireEvent.change(screen.getByLabelText("Host pattern"), { target: { value: ".npmjs.org" } });
    fireEvent.click(screen.getByRole("button", { name: "Add rule" }));

    await waitFor(() =>
      expect(add).toHaveBeenCalledWith({
        host: ".npmjs.org",
        listKind: "Whitelist",
        aiProviderId: null,
      }),
    );
  });

  test("shows a forward's destination, its loopback address and that it is listening", async () => {
    mockServices();
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });

    const row = await forwardRow("postgres");
    expect(row.getByText(":5432")).toBeTruthy();
    expect(row.getByText("127.0.0.1")).toBeTruthy();
    expect(row.getByText(":15432")).toBeTruthy();
    expect(row.getByText("Listening")).toBeTruthy();
  });

  test("a forward whose local port will not bind says so on its own row", async () => {
    mockServices(enforced, "monitor", [
      { ...postgres, listenError: "Local port 15432 is already in use by something else" },
    ]);
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });

    const row = await forwardRow("postgres");
    expect(row.getByText("Local port unavailable")).toBeTruthy();
    expect(row.getByText(/already in use by something else/i)).toBeTruthy();
  });

  test("a forward the mode blocks is whitelisted in the same click", async () => {
    mockServices(enforced, "whitelist", [{ ...postgres, decision: "Blocked" }]);
    const add = vi.spyOn(services.networkService, "addEntry").mockResolvedValue({
      id: "e7",
      host: "postgres",
      listKind: "Whitelist",
      aiProviderId: null,
      createdAt: "2026-09-02T12:00:00Z",
    });
    const getForwards = vi
      .spyOn(services.networkService, "getForwards")
      .mockResolvedValueOnce([{ ...postgres, decision: "Blocked" }])
      .mockResolvedValue([postgres]);
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });

    const row = await forwardRow("postgres");
    expect(row.getByText("Host not allowed by current mode")).toBeTruthy();
    fireEvent.click(row.getByRole("button", { name: "Add postgres to whitelist" }));

    await waitFor(() =>
      expect(add).toHaveBeenCalledWith({
        host: "postgres",
        listKind: "Whitelist",
        aiProviderId: null,
      }),
    );
    await waitFor(() => expect(getForwards).toHaveBeenCalledTimes(2));
    expect(await (await forwardTable()).findByText("Listening")).toBeTruthy();
  });

  test("adds a forward and reports the server's reason when one is refused", async () => {
    mockServices();
    const add = vi
      .spyOn(services.networkService, "addForward")
      .mockRejectedValueOnce({
        status: 400,
        message: "A forward needs one destination, not a pattern: drop the leading dot or *",
      })
      .mockResolvedValue({ ...postgres, id: "f2", name: "redis", host: "cache", port: 6379 });
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });
    await forwardRow("postgres");

    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "redis" } });
    fireEvent.change(screen.getByLabelText("Destination host"), {
      target: { value: ".example.com" },
    });
    fireEvent.change(screen.getByLabelText("Destination port"), { target: { value: "6379" } });
    fireEvent.change(screen.getByLabelText("Local port"), { target: { value: "16379" } });
    fireEvent.click(screen.getByRole("button", { name: "Add forward" }));

    expect(await screen.findByText(/not a pattern/i)).toBeTruthy();

    fireEvent.change(screen.getByLabelText("Destination host"), { target: { value: "cache" } });
    fireEvent.click(screen.getByRole("button", { name: "Add forward" }));

    await waitFor(() =>
      expect(add).toHaveBeenLastCalledWith({
        name: "redis",
        host: "cache",
        port: 6379,
        localPort: 16379,
      }),
    );
    expect(await (await forwardTable()).findByText("redis")).toBeTruthy();
  });

  test("will not send a forward whose ports are blank or not numbers", async () => {
    mockServices();
    const add = vi.spyOn(services.networkService, "addForward");
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });
    await forwardRow("postgres");

    const addButton = () =>
      screen.getByRole("button", { name: "Add forward" }) as HTMLButtonElement;
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "redis" } });
    fireEvent.change(screen.getByLabelText("Destination host"), { target: { value: "cache" } });
    expect(addButton().disabled).toBe(true);

    fireEvent.change(screen.getByLabelText("Destination port"), { target: { value: "63a79" } });
    fireEvent.change(screen.getByLabelText("Local port"), { target: { value: "16379" } });
    expect(addButton().disabled).toBe(true);
    expect(screen.getByText(/whole numbers between 1 and 65535/i)).toBeTruthy();

    fireEvent.change(screen.getByLabelText("Destination port"), { target: { value: "6379" } });
    expect(addButton().disabled).toBe(false);
    expect(add).not.toHaveBeenCalled();
  });

  // `Number` reads both as ports in range, so they would have been sent as 1000
  // and 16 — values nobody typed.
  test.each(["1e3", "0x10", "+80", "80.0"])(
    "refuses %s as a port rather than coercing it",
    async (typed) => {
      mockServices();
      render(<NetworkSettings />, { wrapper: WorkItemHubProvider });
      await forwardRow("postgres");

      fireEvent.change(screen.getByLabelText("Name"), { target: { value: "redis" } });
      fireEvent.change(screen.getByLabelText("Destination host"), { target: { value: "cache" } });
      fireEvent.change(screen.getByLabelText("Destination port"), { target: { value: typed } });
      fireEvent.change(screen.getByLabelText("Local port"), { target: { value: "16379" } });

      expect(
        (screen.getByRole("button", { name: "Add forward" }) as HTMLButtonElement).disabled,
      ).toBe(true);
      expect(screen.getByText(/whole numbers between 1 and 65535/i)).toBeTruthy();
    },
  );

  test("Enter neither submits an incomplete forward nor sends a second while one is in flight", async () => {
    mockServices();
    let inFlight: (() => void) | null = null;
    const add = vi
      .spyOn(services.networkService, "addForward")
      .mockImplementation(
        () => new Promise((resolve) => (inFlight = () => resolve({ ...postgres, id: "f2" }))),
      );
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });
    await forwardRow("postgres");

    // Enter reaches add() past the disabled button, so the form must guard itself.
    const name = screen.getByLabelText("Name");
    fireEvent.keyDown(name, { key: "Enter" });
    expect(add).not.toHaveBeenCalled();

    fireEvent.change(name, { target: { value: "redis" } });
    fireEvent.change(screen.getByLabelText("Destination host"), { target: { value: "cache" } });
    fireEvent.keyDown(name, { key: "Enter" });
    expect(add).not.toHaveBeenCalled();

    fireEvent.change(screen.getByLabelText("Destination port"), { target: { value: "6379" } });
    fireEvent.change(screen.getByLabelText("Local port"), { target: { value: "16379" } });

    fireEvent.keyDown(name, { key: "Enter" });
    await waitFor(() => expect(add).toHaveBeenCalledTimes(1));

    fireEvent.keyDown(name, { key: "Enter" });
    fireEvent.click(screen.getByRole("button", { name: "Add forward" }));
    expect(add).toHaveBeenCalledTimes(1);

    inFlight!();
    await waitFor(() => expect(add).toHaveBeenCalledTimes(1));
  });

  test("removes a forward", async () => {
    mockServices();
    const remove = vi.spyOn(services.networkService, "deleteForward").mockResolvedValue(undefined);
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });

    fireEvent.click(await screen.findByRole("button", { name: "Remove the postgres forward" }));

    await waitFor(() => expect(remove).toHaveBeenCalledWith("f1"));
    expect(await screen.findByText(/no forwards yet/i)).toBeTruthy();
  });

  test("re-reads the forwards when the policy changes", async () => {
    mockServices();
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });
    await forwardRow("postgres");

    vi.spyOn(services.networkService, "getForwards").mockResolvedValue([
      { ...postgres, id: "f3", name: "smtp", host: "mail.internal", port: 25, localPort: 10025 },
    ]);
    emit("NetworkPolicyChanged", {});

    expect(await (await forwardTable()).findByText("smtp")).toBeTruthy();
    await waitFor(() =>
      expect(screen.queryByRole("button", { name: "Remove the postgres forward" })).toBeNull(),
    );
  });

  test("clears the log", async () => {
    mockServices();
    const clear = vi.spyOn(services.networkService, "clearLog").mockResolvedValue({ removed: 1 });
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });

    fireEvent.click(await screen.findByRole("button", { name: /clear log/i }));

    await waitFor(() => expect(clear).toHaveBeenCalled());
    expect(await screen.findByText(/no destinations recorded yet/i)).toBeTruthy();
    expect((screen.getByRole("button", { name: /clear log/i }) as HTMLButtonElement).disabled).toBe(
      true,
    );
  });

  test("saves a log retention window", async () => {
    mockServices();
    const put = vi
      .spyOn(services.settingsService, "put")
      .mockResolvedValue({ key: services.NetworkSettingKeys.LogRetentionDays, value: "7" });
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });

    const input = (await screen.findByLabelText("Delete log entries after")) as HTMLInputElement;
    await waitFor(() => expect(input.value).toBe("30"));

    fireEvent.change(input, { target: { value: "7" } });
    fireEvent.blur(input);

    await waitFor(() =>
      expect(put).toHaveBeenCalledWith(services.NetworkSettingKeys.LogRetentionDays, "7"),
    );
  });

  test("refuses a log retention window outside the allowed range without calling the server", async () => {
    mockServices();
    const put = vi.spyOn(services.settingsService, "put");
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });

    const input = (await screen.findByLabelText("Delete log entries after")) as HTMLInputElement;
    await waitFor(() => expect(input.value).toBe("30"));

    fireEvent.change(input, { target: { value: "3651" } });
    fireEvent.blur(input);

    expect(await screen.findByText(/between 0 \(disabled\) and 3650/i)).toBeTruthy();
    expect(put).not.toHaveBeenCalled();
  });

  test("a retention sweep re-reads the log rather than emptying the view", async () => {
    mockServices();
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });
    await screen.findByText("registry.npmjs.org");

    const survivor: NetworkLogEntry = {
      id: "l9",
      host: "api.anthropic.com",
      port: 443,
      timestamp: "2026-09-04T12:00:00Z",
      decision: "Allowed",
      aiProviderId: null,
    };
    vi.spyOn(services.networkService, "getLog").mockResolvedValue([survivor]);
    emit("NetworkLogCleared", {});

    expect(await trafficLog().findByText("api.anthropic.com")).toBeTruthy();
    await waitFor(() => expect(trafficLog().queryByText("registry.npmjs.org")).toBeNull());
  });

  test("a decision that arrives as a number still renders as its name", async () => {
    mockServices();
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });
    await screen.findByText("registry.npmjs.org");

    emit("NetworkLogAppended", {
      id: "l3",
      host: "numeric.example",
      port: 443,
      timestamp: "2026-09-02T12:02:00Z",
      decision: 1,
      aiProviderId: null,
    });

    const row = (await screen.findByText("numeric.example")).closest("tr")!;
    expect(within(row).getByText("Blocked")).toBeTruthy();
  });

  test("collapses a run of identical destinations into one counted row", async () => {
    mockServices();
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });
    await screen.findByText("registry.npmjs.org");

    for (const id of ["l2", "l3"]) {
      emit("NetworkLogAppended", {
        ...npmLine,
        id,
        timestamp: `2026-09-02T12:0${id === "l2" ? 1 : 2}:00Z`,
      } satisfies NetworkLogEntry);
    }

    await waitFor(() => expect(trafficLog().getByText("×3")).toBeTruthy());
    expect(trafficLog().getAllByText("registry.npmjs.org")).toHaveLength(1);

    fireEvent.click(screen.getByLabelText("Group repeats"));
    await waitFor(() => expect(trafficLog().getAllByText("registry.npmjs.org")).toHaveLength(3));
  });

  test("groups only runs that are actually consecutive", () => {
    const line = (id: string, host: string): NetworkLogEntry => ({
      ...npmLine,
      id,
      host,
    });
    const groups = groupConsecutive([
      line("1", "a.example"),
      line("2", "a.example"),
      line("3", "b.example"),
      line("4", "a.example"),
    ]);

    expect(groups.map((g) => [g.entry.host, g.count])).toEqual([
      ["a.example", 2],
      ["b.example", 1],
      ["a.example", 1],
    ]);
  });

  test("filters the log by host and by decision", async () => {
    mockServices();
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });
    await screen.findByText("registry.npmjs.org");

    emit("NetworkLogAppended", {
      id: "l9",
      host: "api.anthropic.com",
      port: 443,
      timestamp: "2026-09-02T12:05:00Z",
      decision: "Allowed",
      aiProviderId: null,
    } satisfies NetworkLogEntry);
    await screen.findByText("api.anthropic.com");

    fireEvent.change(screen.getByLabelText("Filter the traffic log by host"), {
      target: { value: "npmjs" },
    });
    expect(trafficLog().queryByText("api.anthropic.com")).toBeNull();

    fireEvent.change(screen.getByLabelText("Filter the traffic log by host"), {
      target: { value: "" },
    });
    fireEvent.click(within(screen.getByRole("group", { name: "Decision" })).getByText("Allowed"));
    expect(trafficLog().queryByText("registry.npmjs.org")).toBeNull();
    expect(trafficLog().getByText("api.anthropic.com")).toBeTruthy();
  });

  test("a load failure is shown and cleared by the next successful load", async () => {
    mockServices();
    vi.spyOn(services.networkService, "getEntries")
      .mockRejectedValueOnce({ status: 500, message: "database unavailable" })
      .mockResolvedValue([github]);
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });

    expect(await screen.findByText("database unavailable")).toBeTruthy();

    emit("NetworkPolicyChanged", {});
    await waitFor(() => expect(screen.queryByText("database unavailable")).toBeNull());
    expect(await screen.findByText(".github.com")).toBeTruthy();
  });

  test("leaving the page takes its three listeners off the shared hub", async () => {
    mockServices();
    const { unmount } = render(<NetworkSettings />, { wrapper: WorkItemHubProvider });
    const events = ["NetworkPolicyChanged", "NetworkLogAppended", "NetworkLogCleared"];
    await waitFor(() => expect(events.map((e) => hub.handlers[e]?.size)).toEqual([1, 1, 1]));

    unmount();

    expect(events.map((e) => hub.handlers[e]?.size ?? 0)).toEqual([0, 0, 0]);
  });

  test("live-updates the log and the lists from the hub", async () => {
    mockServices();
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });
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
    const rows = trafficLog().getAllByRole("row").slice(1);
    expect(rows[0].textContent).toContain("api.anthropic.com");

    vi.spyOn(services.networkService, "getLog").mockResolvedValue([]);
    emit("NetworkLogCleared", {});
    expect(await screen.findByText(/no destinations recorded yet/i)).toBeTruthy();

    const getEntries = vi.spyOn(services.networkService, "getEntries").mockResolvedValue([github]);
    emit("NetworkPolicyChanged", {});
    await waitFor(() => expect(getEntries).toHaveBeenCalled());
    await waitFor(() => expect(screen.queryByText("evil.example")).toBeNull());
  });

  test("offers no internal destinations setting and never reads one", async () => {
    mockServices();
    const get = vi.spyOn(services.settingsService, "get");
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });

    await screen.findByText("Filter mode", { selector: "h3" });
    await waitFor(() => expect(get).toHaveBeenCalled());

    expect(screen.queryByLabelText(/internal destinations/i)).toBeNull();
    expect(screen.queryByText(/internal destinations/i)).toBeNull();
    expect(get.mock.calls.map(([key]) => key).filter((key) => /internal/i.test(key))).toEqual([]);
  });
});

describe("Network modes", () => {
  const modeCard = () => screen.getByText("Filter mode", { selector: "h3" }).closest("section")!;
  const offWarning = () => screen.queryAllByText(/reach any host/i);
  const confirmation = () => screen.queryByRole("dialog");
  const confirmButton = (dialog: HTMLElement) =>
    within(dialog)
      .getAllByRole("button")
      .find((b) => b.textContent !== "Cancel")!;

  const pressed = (name: string) => modeButton(name).getAttribute("aria-pressed") === "true";

  test("offers Off, Monitor, Whitelist and Blacklist", async () => {
    mockServices();
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });
    await waitFor(() => expect(pressed("Monitor")).toBe(true));

    const names = within(screen.getByRole("group", { name: "Filter mode" }))
      .getAllByRole("button")
      .map((b) => b.textContent);
    expect(names).toEqual(["Off", "Monitor", "Whitelist", "Blacklist"]);
  });

  test("each mode explains itself in its own words", async () => {
    const texts: string[] = [];
    for (const mode of ["off", "monitor", "whitelist", "blacklist"]) {
      mockServices(enforced, mode);
      render(<NetworkSettings />, { wrapper: WorkItemHubProvider });
      await waitFor(() => expect(pressed(mode[0].toUpperCase() + mode.slice(1))).toBe(true));
      texts.push(modeCard().textContent!);
      cleanup();
      vi.restoreAllMocks();
    }
    expect(new Set(texts).size).toBe(4);
  });

  test("choosing Off asks first; cancelling saves nothing and keeps the previous mode", async () => {
    mockServices(enforced, "monitor");
    const put = vi
      .spyOn(services.settingsService, "put")
      .mockResolvedValue({ key: services.NetworkSettingKeys.Mode, value: "off" });
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });
    await waitFor(() => expect(pressed("Monitor")).toBe(true));

    fireEvent.click(modeButton("Off"));

    const dialog = await screen.findByRole("dialog");
    expect(dialog.textContent).toContain(
      "agents can reach any host, including your LAN and cloud metadata, and nothing is logged",
    );
    expect(put).not.toHaveBeenCalled();

    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));

    await waitFor(() => expect(confirmation()).toBeNull());
    expect(put).not.toHaveBeenCalled();
    expect(pressed("Monitor")).toBe(true);
    expect(pressed("Off")).toBe(false);
  });

  test("confirming Off saves it", async () => {
    mockServices(enforced, "monitor");
    const put = vi
      .spyOn(services.settingsService, "put")
      .mockResolvedValue({ key: services.NetworkSettingKeys.Mode, value: "off" });
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });
    await waitFor(() => expect(pressed("Monitor")).toBe(true));

    fireEvent.click(modeButton("Off"));
    fireEvent.click(confirmButton(await screen.findByRole("dialog")));

    await waitFor(() => expect(put).toHaveBeenCalledWith(services.NetworkSettingKeys.Mode, "off"));
    expect(put).toHaveBeenCalledTimes(1);
    await waitFor(() => expect(pressed("Off")).toBe(true));
    expect(confirmation()).toBeNull();
  });

  test("a refused save of Off reverts to the previous mode and shows the server message", async () => {
    mockServices(enforced, "whitelist");
    vi.spyOn(services.settingsService, "put").mockRejectedValue({
      status: 400,
      message: "network.mode could not be saved right now",
    });
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });
    await waitFor(() => expect(pressed("Whitelist")).toBe(true));

    fireEvent.click(modeButton("Off"));
    fireEvent.click(confirmButton(await screen.findByRole("dialog")));

    await screen.findByText(/could not be saved right now/i);
    expect(pressed("Whitelist")).toBe(true);
    expect(pressed("Off")).toBe(false);
  });

  test("the other modes save without a question", async () => {
    mockServices(enforced, "off");
    const put = vi
      .spyOn(services.settingsService, "put")
      .mockResolvedValue({ key: services.NetworkSettingKeys.Mode, value: "monitor" });
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });
    await waitFor(() => expect(pressed("Off")).toBe(true));

    for (const [button, value] of [
      ["Monitor", "monitor"],
      ["Whitelist", "whitelist"],
      ["Blacklist", "blacklist"],
    ]) {
      fireEvent.click(modeButton(button));
      await waitFor(() =>
        expect(put).toHaveBeenLastCalledWith(services.NetworkSettingKeys.Mode, value),
      );
      expect(confirmation()).toBeNull();
    }
  });

  test("a warning stays up while Off is the loaded mode, and not before the mode has loaded", async () => {
    mockServices(enforced, "off");
    let release: (v: { key: string; value: string }) => void = () => {};
    const real = vi.mocked(services.settingsService.get).getMockImplementation()!;
    vi.mocked(services.settingsService.get).mockImplementation((key: string) =>
      key === services.NetworkSettingKeys.Mode
        ? new Promise((resolve) => (release = resolve))
        : real(key),
    );
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });
    await screen.findByLabelText("Network rules");
    expect(offWarning()).toHaveLength(0);

    release({ key: services.NetworkSettingKeys.Mode, value: "off" });

    await waitFor(() => expect(offWarning().length).toBeGreaterThan(0));
    // It is not the advisory banner's live region.
    expect(offWarning().every((el) => el.closest("[role=status]") === null)).toBe(true);
    expect(screen.queryByRole("status")).toBeNull();
  });

  test("the Off warning carries the firewall's own word on whether Off can reach the network", async () => {
    const offUnavailable = {
      ...enforced,
      reason:
        "Network mode Off is unavailable: it needs no-new-privileges; agents launched in it have no network.",
    };
    mockServices(offUnavailable, "off");
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });

    const banner = (await screen.findByText(/Network mode is Off/)).closest("div")!;
    expect(banner.textContent).toMatch(/Off is unavailable/);
  });

  test.each(["OFF", " Off "])(
    "a mode stored as %j is read as Off, as the backend reads it",
    async (stored) => {
      mockServices(enforced, stored);
      render(<NetworkSettings />, { wrapper: WorkItemHubProvider });

      await waitFor(() => expect(pressed("Off")).toBe(true));
      expect(offWarning().length).toBeGreaterThan(0);
    },
  );

  test("no Off warning in a mode that has a proxy", async () => {
    mockServices(enforced, "monitor");
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });
    await waitFor(() => expect(pressed("Monitor")).toBe(true));

    expect(offWarning()).toHaveLength(0);
  });

  test("says which changes apply to the next agent launch and which to the next connection", async () => {
    mockServices(enforced, "whitelist");
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });
    await waitFor(() => expect(pressed("Whitelist")).toBe(true));

    const card = modeCard().textContent!;
    expect(card).toMatch(/next agent launch/i);
    expect(card).toMatch(/running agents keep/i);
    expect(card).toMatch(/next connection/i);
  });

  test("without the firewall it says Off and Monitor differ only in logging", async () => {
    mockServices(advisory, "monitor");
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });
    await waitFor(() => expect(pressed("Monitor")).toBe(true));
    expect(modeCard().textContent).toMatch(/differ only in logging/i);
    cleanup();

    mockServices(enforced, "monitor");
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });
    await waitFor(() => expect(pressed("Monitor")).toBe(true));
    expect(modeCard().textContent).not.toMatch(/differ only in logging/i);
  });

  test.each(["monitor", "off"])(
    "%s shows no rule in effect and no rule marked as ignored",
    async (mode) => {
      mockServices(enforced, mode);
      render(<NetworkSettings />, { wrapper: WorkItemHubProvider });
      await waitFor(() => expect(pressed(mode === "off" ? "Off" : "Monitor")).toBe(true));

      const rules = within(await screen.findByLabelText("Network rules"));
      expect(rules.getByText(".github.com")).toBeTruthy();
      expect(screen.queryAllByText("not in effect")).toHaveLength(0);
      expect(modeCard().textContent).not.toMatch(/\d+ rules? (is|are) in effect/i);
      const note = screen.getByText("Rules", { selector: "h3" }).closest("section")!.textContent!;
      expect(note).toMatch(/no rule is in effect/i);
      expect(note).toMatch(mode === "off" ? /nothing is logged/i : /logged/i);
    },
  );
});

describe("Editing a network forward", () => {
  const redis: NetworkForward = {
    ...postgres,
    id: "f2",
    name: "redis",
    host: "cache",
    port: 6379,
    localPort: 16379,
  };
  const field = (label: string) => screen.getByLabelText(label) as HTMLInputElement;
  const click = (name: string) => fireEvent.click(screen.getByRole("button", { name }));
  const startEditing = async (name = "postgres") => {
    fireEvent.click(await screen.findByRole("button", { name: `Edit the ${name} forward` }));
  };
  const type = (label: string, value: string) =>
    fireEvent.change(field(label), { target: { value } });

  test("saves changed values, trimmed and numeric, and shows the server's row in place", async () => {
    mockServices(enforced, "monitor", [postgres, redis]);
    const updated: NetworkForward = {
      ...postgres,
      name: "primary",
      host: "db.example",
      port: 5433,
      localPort: 15433,
    };
    const update = vi.spyOn(services.networkService, "updateForward").mockResolvedValue(updated);
    vi.spyOn(services.networkService, "getForwards")
      .mockResolvedValueOnce([postgres, redis])
      .mockResolvedValue([updated, redis]);
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });

    await startEditing();
    expect(field("Name of the postgres forward").value).toBe("postgres");
    expect(field("Destination host of the postgres forward").value).toBe("postgres");
    expect(field("Destination port of the postgres forward").value).toBe("5432");
    expect(field("Local port of the postgres forward").value).toBe("15432");

    type("Name of the postgres forward", " primary ");
    type("Destination host of the postgres forward", " db.example ");
    type("Destination port of the postgres forward", "5433");
    type("Local port of the postgres forward", "15433");
    click("Save the postgres forward");

    await waitFor(() =>
      expect(update).toHaveBeenCalledWith("f1", {
        name: "primary",
        host: "db.example",
        port: 5433,
        localPort: 15433,
      }),
    );
    const table = await forwardTable();
    expect(await table.findByText("primary")).toBeTruthy();
    expect(table.getByText("db.example")).toBeTruthy();
    expect(screen.queryByRole("button", { name: /^Save the / })).toBeNull();
    expect(screen.getByRole("button", { name: "Edit the primary forward" })).toBeTruthy();
    // The other row is untouched.
    expect(table.getByText("redis")).toBeTruthy();
    expect(update).toHaveBeenCalledTimes(1);
  });

  test("keeps the edits and shows the server's reason in the row when a save is refused, then saves the fix", async () => {
    mockServices(enforced, "monitor", [postgres, redis]);
    const fixed: NetworkForward = { ...postgres, localPort: 17000 };
    const update = vi
      .spyOn(services.networkService, "updateForward")
      .mockRejectedValueOnce({
        status: 400,
        message: "Local port 16379 already forwards to cache:6379",
      })
      .mockResolvedValue(fixed);
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });

    await startEditing();
    type("Local port of the postgres forward", "16379");
    click("Save the postgres forward");

    const row = within(
      screen.getByRole("button", { name: "Save the postgres forward" }).closest("tr")!,
    );
    expect(await row.findByText(/already forwards to cache:6379/)).toBeTruthy();
    expect(field("Local port of the postgres forward").value).toBe("16379");
    expect(screen.getByRole("button", { name: "Save the postgres forward" })).toBeTruthy();

    type("Local port of the postgres forward", "17000");
    click("Save the postgres forward");

    await waitFor(() => expect(update).toHaveBeenCalledTimes(2));
    expect(update).toHaveBeenLastCalledWith("f1", {
      name: "postgres",
      host: "postgres",
      port: 5432,
      localPort: 17000,
    });
    await waitFor(() => expect(screen.queryByRole("button", { name: /^Save the / })).toBeNull());
    expect(screen.queryByText(/already forwards to cache/)).toBeNull();
  });

  test("cancel throws the edits away without sending anything, and they do not come back", async () => {
    mockServices();
    const update = vi.spyOn(services.networkService, "updateForward");
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });

    await startEditing();
    type("Name of the postgres forward", "scribble");
    type("Local port of the postgres forward", "9");
    click("Cancel editing the postgres forward");

    const table = await forwardTable();
    expect(table.getByRole("button", { name: "Edit the postgres forward" })).toBeTruthy();
    expect(table.queryByText("scribble")).toBeNull();
    expect(screen.queryByLabelText("Name of the postgres forward")).toBeNull();

    await startEditing();
    expect(field("Name of the postgres forward").value).toBe("postgres");
    expect(field("Local port of the postgres forward").value).toBe("15432");
    expect(update).not.toHaveBeenCalled();
  });

  test("will not save an incomplete draft, by button or by Enter, and says why for ports", async () => {
    mockServices();
    const update = vi.spyOn(services.networkService, "updateForward");
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });
    await startEditing();
    const save = () =>
      screen.getByRole("button", { name: "Save the postgres forward" }) as HTMLButtonElement;

    type("Name of the postgres forward", "   ");
    expect(save().disabled).toBe(true);
    fireEvent.keyDown(field("Name of the postgres forward"), { key: "Enter" });
    type("Name of the postgres forward", "postgres");
    type("Destination host of the postgres forward", "");
    expect(save().disabled).toBe(true);
    type("Destination host of the postgres forward", "postgres");
    expect(save().disabled).toBe(false);

    type("Local port of the postgres forward", "1e3");
    expect(save().disabled).toBe(true);
    expect(screen.getByText(/whole numbers between 1 and 65535/i)).toBeTruthy();
    fireEvent.keyDown(field("Local port of the postgres forward"), { key: "Enter" });
    type("Destination port of the postgres forward", "70000");
    expect(save().disabled).toBe(true);

    expect(update).not.toHaveBeenCalled();
  });

  test("Enter saves a complete draft, and a second Save while one is in flight sends nothing", async () => {
    mockServices();
    let finish: (f: NetworkForward) => void = () => {};
    const update = vi
      .spyOn(services.networkService, "updateForward")
      .mockReturnValue(new Promise<NetworkForward>((resolve) => (finish = resolve)));
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });
    await startEditing();

    type("Name of the postgres forward", "primary");
    fireEvent.keyDown(field("Name of the postgres forward"), { key: "Enter" });
    fireEvent.keyDown(field("Name of the postgres forward"), { key: "Enter" });
    const save = screen.getByRole("button", { name: "Save the postgres forward" });
    fireEvent.click(save);

    expect(update).toHaveBeenCalledTimes(1);
    finish({ ...postgres, name: "primary" });
    expect(await (await forwardTable()).findByText("primary")).toBeTruthy();
  });

  test("a live refresh while a row is being edited does not discard its edits or touch the add form", async () => {
    mockServices(enforced, "monitor", [postgres, redis]);
    render(<NetworkSettings />, { wrapper: WorkItemHubProvider });
    fireEvent.change(await screen.findByLabelText("Name"), { target: { value: "draft-add" } });

    await startEditing("redis");
    type("Name of the redis forward", "cache-edit");
    const getForwards = vi
      .spyOn(services.networkService, "getForwards")
      .mockResolvedValue([postgres, { ...redis, decision: "Blocked" }]);
    emit("NetworkPolicyChanged", {});

    await waitFor(() => expect(getForwards).toHaveBeenCalled());
    await (await forwardTable()).findByText("Host not allowed by current mode");
    expect(field("Name of the redis forward").value).toBe("cache-edit");
    expect((screen.getByLabelText("Name") as HTMLInputElement).value).toBe("draft-add");
    // Only the edited row is in edit mode.
    expect(screen.queryByLabelText("Name of the postgres forward")).toBeNull();
    expect(screen.getByRole("button", { name: "Edit the postgres forward" })).toBeTruthy();
  });
});
