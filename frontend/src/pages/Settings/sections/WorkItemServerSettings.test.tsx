import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, fireEvent, waitFor } from "@testing-library/react";
import WorkItemServerSettings from "./WorkItemServerSettings";
import * as services from "../../../services/auth";
import type { WorkItemServerConfig } from "../../../types";

const stored: WorkItemServerConfig = {
  url: "https://workitems.example.com",
  hasApiKey: true,
  pollIntervalSeconds: 120,
  graceIntervalSeconds: 10,
};

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

const field = (label: RegExp) => screen.getByLabelText(label) as HTMLInputElement;
const urlField = () => field(/url/i);
const keyField = () => field(/api key/i);
const pollField = () => field(/poll interval/i);
const graceField = () => field(/grace interval/i);

async function renderLoaded(config: WorkItemServerConfig = stored) {
  vi.spyOn(services.workItemServerService, "get").mockResolvedValue(config);
  render(<WorkItemServerSettings />);
  await screen.findByLabelText(/url/i);
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("WorkItemServerSettings loading", () => {
  test("shows the stored config, leaving a stored key empty with an (unchanged) placeholder", async () => {
    await renderLoaded();

    expect(urlField().value).toBe("https://workitems.example.com");
    expect(keyField().value).toBe("");
    expect(keyField().placeholder).toBe("(unchanged)");
    expect(pollField().value).toBe("120");
    expect(graceField().value).toBe("10");
  });

  test("shows an empty url and the 60/5 defaults for null values", async () => {
    await renderLoaded({
      url: null,
      hasApiKey: false,
      pollIntervalSeconds: null as unknown as number,
      graceIntervalSeconds: null as unknown as number,
    });

    expect(urlField().value).toBe("");
    expect(keyField().placeholder).not.toBe("(unchanged)");
    expect(pollField().value).toBe("60");
    expect(graceField().value).toBe("5");
  });

  test("shows a loading indicator instead of the form until the config arrives", async () => {
    const load = deferred<WorkItemServerConfig>();
    vi.spyOn(services.workItemServerService, "get").mockReturnValue(load.promise);
    render(<WorkItemServerSettings />);

    expect(screen.getByText(/loading/i)).toBeTruthy();
    expect(screen.queryByLabelText(/url/i)).toBeNull();

    load.resolve(stored);
    expect(((await screen.findByLabelText(/url/i)) as HTMLInputElement).value).toBe(
      "https://workitems.example.com",
    );
    expect(screen.queryByText(/loading/i)).toBeNull();
  });

  test("still renders the form with defaults when loading fails", async () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    vi.spyOn(services.workItemServerService, "get").mockRejectedValue(new Error("boom"));
    render(<WorkItemServerSettings />);

    await screen.findByLabelText(/url/i);
    expect(urlField().value).toBe("");
    expect(keyField().value).toBe("");
    expect(pollField().value).toBe("60");
    expect(graceField().value).toBe("5");
  });
});

describe("WorkItemServerSettings saving", () => {
  test("sends all four values, shows Saving... while in flight, then refreshes from the response", async () => {
    await renderLoaded();
    const save = deferred<WorkItemServerConfig>();
    const update = vi.spyOn(services.workItemServerService, "update").mockReturnValue(save.promise);

    fireEvent.change(urlField(), { target: { value: "https://new.example.com" } });
    fireEvent.change(keyField(), { target: { value: "secret-key" } });
    fireEvent.change(pollField(), { target: { value: "30" } });
    fireEvent.change(graceField(), { target: { value: "7" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(update).toHaveBeenCalledWith({
      url: "https://new.example.com",
      apiKey: "secret-key",
      pollIntervalSeconds: 30,
      graceIntervalSeconds: 7,
    });
    const busy = await screen.findByRole("button", { name: "Saving..." });
    expect((busy as HTMLButtonElement).disabled).toBe(true);

    // The server's answer, not what was typed, is what the form shows afterwards.
    save.resolve({
      url: "https://normalised.example.com",
      hasApiKey: true,
      pollIntervalSeconds: 45,
      graceIntervalSeconds: 8,
    });

    expect(await screen.findByText("Settings saved.")).toBeTruthy();
    expect(urlField().value).toBe("https://normalised.example.com");
    expect(keyField().value).toBe("");
    expect(keyField().placeholder).toBe("(unchanged)");
    expect(pollField().value).toBe("45");
    expect(graceField().value).toBe("8");
    expect((screen.getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled).toBe(
      false,
    );
  });

  test("sends a blank key and url as null so the stored key is kept", async () => {
    await renderLoaded();
    const update = vi.spyOn(services.workItemServerService, "update").mockResolvedValue(stored);

    fireEvent.change(urlField(), { target: { value: "" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() =>
      expect(update).toHaveBeenCalledWith({
        url: null,
        apiKey: null,
        pollIntervalSeconds: 120,
        graceIntervalSeconds: 10,
      }),
    );
  });

  test.each([
    ["an Error", new Error("Server said no"), "Server said no"],
    ["not an Error", "nope", "Failed to save settings."],
  ])(
    "on failure (%s) shows the error, keeps the entered values and re-enables Save",
    async (_, thrown, message) => {
      await renderLoaded();
      vi.spyOn(services.workItemServerService, "update").mockRejectedValue(thrown);

      fireEvent.change(urlField(), { target: { value: "https://typed.example.com" } });
      fireEvent.change(keyField(), { target: { value: "typed-key" } });
      fireEvent.change(pollField(), { target: { value: "33" } });
      fireEvent.change(graceField(), { target: { value: "4" } });
      fireEvent.click(screen.getByRole("button", { name: "Save" }));

      expect(await screen.findByText(message)).toBeTruthy();
      expect(screen.queryByText("Settings saved.")).toBeNull();
      expect(urlField().value).toBe("https://typed.example.com");
      expect(keyField().value).toBe("typed-key");
      expect(pollField().value).toBe("33");
      expect(graceField().value).toBe("4");
      expect((screen.getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled).toBe(
        false,
      );
    },
  );
});
