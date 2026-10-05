import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, waitFor, fireEvent, act } from "@testing-library/react";
import ChatTitlesSettings from "./ChatTitlesSettings";
import * as authServices from "../../../services/auth";
import type { AiProvider } from "../../../types";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

const main = { id: "Main", name: "Main", type: "claude-code", isDefault: true, tags: [] };

function switchedOn() {
  vi.spyOn(authServices.settingsService, "get").mockImplementation(async (key: string) => ({
    key,
    value: key === authServices.ChatTitleSettingKeys.SmartTitles ? "true" : "Fast",
  }));
}

function field() {
  return {
    tag: screen.getByLabelText("Provider tag") as HTMLInputElement,
    save: screen.getByRole("button", { name: "Save" }) as HTMLButtonElement,
  };
}

/** Whatever the field would say about where titles run. */
function providerStatement() {
  return screen.queryByText(/runs on|no default provider|no provider has this tag/i);
}

describe("Chat titles card", () => {
  test("says nothing about providers, and keeps the tag shut, until the providers are read", async () => {
    switchedOn();
    let answer!: (providers: AiProvider[]) => void;
    vi.spyOn(authServices.aiProviderService, "getAll").mockReturnValue(
      new Promise((resolve) => {
        answer = resolve;
      }),
    );

    render(<ChatTitlesSettings />);

    await waitFor(() =>
      expect(
        (screen.getByRole("checkbox", { name: /smart session titles/i }) as HTMLInputElement)
          .checked,
      ).toBe(true),
    );
    await waitFor(() => expect(field().tag.value).toBe("Fast"));
    expect(providerStatement()).toBeNull();
    expect(field().tag.disabled).toBe(true);
    expect(field().save.disabled).toBe(true);

    answer([main as unknown as AiProvider]);
    await screen.findByText("No provider has this tag — runs on the default provider (Main)");
    expect(field().tag.disabled).toBe(false);
  });

  test("a provider list that cannot be read is reported, not stated as having no default", async () => {
    switchedOn();
    vi.spyOn(authServices.aiProviderService, "getAll").mockRejectedValue(
      new Error("Service unavailable"),
    );

    render(<ChatTitlesSettings />);

    await screen.findByText("Could not load the AI providers: Service unavailable");
    await waitFor(() => expect(field().tag.value).toBe("Fast"));
    expect(providerStatement()).toBeNull();
    expect(field().tag.disabled).toBe(true);
    expect(field().save.disabled).toBe(true);
  });

  test("a stored tag that cannot be read is reported, and the tag stays shut", async () => {
    vi.spyOn(authServices.settingsService, "get").mockImplementation(async (key: string) => {
      if (key === authServices.ChatTitleSettingKeys.TitleProviderTag) {
        throw new Error("database down");
      }
      return { key, value: "true" };
    });
    vi.spyOn(authServices.aiProviderService, "getAll").mockResolvedValue([
      main as unknown as AiProvider,
    ]);

    render(<ChatTitlesSettings />);

    await screen.findByText("Could not load the stored provider tag: database down");
    expect(field().tag.disabled).toBe(true);
    expect(field().save.disabled).toBe(true);
  });

  describe("with a Copilot default, an unread tag is not taken for the empty one", () => {
    const providers = [
      {
        id: "Gh",
        name: "Gh",
        type: "copilot",
        isDefault: true,
        tags: [],
      } as unknown as AiProvider,
      {
        id: "Fast",
        name: "Fast",
        type: "claude-code",
        isDefault: false,
        tags: ["Fast"],
      } as unknown as AiProvider,
    ];

    test("while the tag is being read, and once it lands", async () => {
      let answer!: (value: { key: string; value: string }) => void;
      vi.spyOn(authServices.settingsService, "get").mockImplementation((key: string) =>
        key === authServices.ChatTitleSettingKeys.TitleProviderTag
          ? new Promise((resolve) => {
              answer = resolve;
            })
          : Promise.resolve({ key, value: "true" }),
      );
      const providersRead = vi
        .spyOn(authServices.aiProviderService, "getAll")
        .mockResolvedValue(providers);

      render(<ChatTitlesSettings />);
      await waitFor(() => expect(providersRead).toHaveBeenCalled());
      await act(async () => {});

      expect(providerStatement()).toBeNull();
      expect(screen.queryByText(/is a Copilot provider/)).toBeNull();

      await act(async () =>
        answer({ key: authServices.ChatTitleSettingKeys.TitleProviderTag, value: "Fast" }),
      );

      await screen.findByText("Runs on Fast");
      expect(screen.queryByText(/is a Copilot provider/)).toBeNull();
    });

    test("when the tag cannot be read", async () => {
      vi.spyOn(authServices.settingsService, "get").mockImplementation(async (key: string) => {
        if (key === authServices.ChatTitleSettingKeys.TitleProviderTag) {
          throw new Error("database down");
        }
        return { key, value: "true" };
      });
      vi.spyOn(authServices.aiProviderService, "getAll").mockResolvedValue(providers);

      render(<ChatTitlesSettings />);

      await screen.findByText("Could not load the stored provider tag: database down");
      await act(async () => {});
      expect(providerStatement()).toBeNull();
      expect(screen.queryByText(/is a Copilot provider/)).toBeNull();
    });
  });

  test("a provider the tag lands on that cannot make titles is said to make none", async () => {
    switchedOn();
    vi.spyOn(authServices.aiProviderService, "getAll").mockResolvedValue([
      main as unknown as AiProvider,
      {
        id: "Gh",
        name: "Gh",
        type: "copilot",
        isDefault: false,
        tags: ["Fast"],
      } as unknown as AiProvider,
    ]);

    render(<ChatTitlesSettings />);

    await screen.findByText("Runs on Gh");
    expect(screen.getByText(/Gh is a Copilot provider/)).toBeTruthy();

    fireEvent.change(field().tag, { target: { value: "" } });
    await screen.findByText("Runs on the default provider (Main)");
    expect(screen.queryByText(/is a Copilot provider/)).toBeNull();
  });
});

describe("Smart session titles switch", () => {
  const toggle = () =>
    screen.getByRole("checkbox", { name: /smart session titles/i }) as HTMLInputElement;

  test("a flip is not undone by a first read that arrives after it", async () => {
    let answer!: (value: { key: string; value: string }) => void;
    vi.spyOn(authServices.settingsService, "get").mockImplementation((key: string) =>
      key === authServices.ChatTitleSettingKeys.SmartTitles
        ? new Promise((resolve) => {
            answer = resolve;
          })
        : Promise.resolve({ key, value: "" }),
    );
    vi.spyOn(authServices.settingsService, "put").mockImplementation(async (key, value) => ({
      key,
      value,
    }));
    vi.spyOn(authServices.aiProviderService, "getAll").mockResolvedValue([]);

    render(<ChatTitlesSettings />);
    fireEvent.click(toggle());
    await waitFor(() => expect(toggle().disabled).toBe(false));
    expect(toggle().checked).toBe(true);

    await act(async () =>
      answer({ key: authServices.ChatTitleSettingKeys.SmartTitles, value: "false" }),
    );

    expect(toggle().checked).toBe(true);
  });

  test("takes no second flip while a save is out", async () => {
    vi.spyOn(authServices.settingsService, "get").mockImplementation(async (key: string) => ({
      key,
      value: key === authServices.ChatTitleSettingKeys.SmartTitles ? "false" : "",
    }));
    let saved!: () => void;
    const put = vi.spyOn(authServices.settingsService, "put").mockImplementation(
      (key, value) =>
        new Promise((resolve) => {
          saved = () => resolve({ key, value });
        }),
    );
    vi.spyOn(authServices.aiProviderService, "getAll").mockResolvedValue([]);

    render(<ChatTitlesSettings />);
    await waitFor(() => expect(authServices.settingsService.get).toHaveBeenCalled());
    fireEvent.click(toggle());

    expect(toggle().disabled).toBe(true);
    fireEvent.click(toggle());
    expect(put).toHaveBeenCalledTimes(1);

    await act(async () => saved());
    expect(toggle().disabled).toBe(false);
    expect(toggle().checked).toBe(true);
  });

  test("a switch that cannot be read says so instead of looking off", async () => {
    vi.spyOn(authServices.settingsService, "get").mockImplementation(async (key: string) => {
      if (key === authServices.ChatTitleSettingKeys.SmartTitles) throw new Error("database down");
      return { key, value: "" };
    });
    vi.spyOn(authServices.aiProviderService, "getAll").mockResolvedValue([]);

    render(<ChatTitlesSettings />);

    await screen.findByText(/Could not load this setting: database down/);
    expect(screen.getByRole("alert").textContent).toMatch(/Could not load this setting/);
  });

  describe("a failed save puts back what the server stores, not the opposite of the flip", () => {
    function storedOnButSlow() {
      let answer!: () => void;
      vi.spyOn(authServices.settingsService, "get").mockImplementation((key: string) =>
        key === authServices.ChatTitleSettingKeys.SmartTitles
          ? new Promise((resolve) => {
              answer = () => resolve({ key, value: "true" });
            })
          : Promise.resolve({ key, value: "" }),
      );
      vi.spyOn(authServices.aiProviderService, "getAll").mockResolvedValue([]);
      return () => answer();
    }

    test("when the first read lands after the failure", async () => {
      const answer = storedOnButSlow();
      vi.spyOn(authServices.settingsService, "put").mockRejectedValue(new Error("database down"));

      render(<ChatTitlesSettings />);
      fireEvent.click(toggle());
      await screen.findByText("database down");
      await act(async () => answer());

      expect(toggle().checked).toBe(true);
    });

    test("when the first read lands while the save is out", async () => {
      const answer = storedOnButSlow();
      let fail!: () => void;
      vi.spyOn(authServices.settingsService, "put").mockImplementation(
        () =>
          new Promise((_, reject) => {
            fail = () => reject(new Error("database down"));
          }),
      );

      render(<ChatTitlesSettings />);
      fireEvent.click(toggle());
      fireEvent.click(toggle());
      await act(async () => answer());
      await act(async () => fail());

      await screen.findByText("database down");
      expect(toggle().checked).toBe(true);
    });
  });
});

describe("Title attempts", () => {
  test("are shut while the switch is off, and save once it is on", async () => {
    vi.spyOn(authServices.settingsService, "get").mockImplementation(async (key: string) => ({
      key,
      value:
        key === authServices.ChatTitleSettingKeys.SmartTitles
          ? "false"
          : key === authServices.ChatTitleSettingKeys.TitleMaxAttempts
            ? "3"
            : "",
    }));
    const put = vi
      .spyOn(authServices.settingsService, "put")
      .mockImplementation(async (key: string, value: string) => ({ key, value }));
    vi.spyOn(authServices.aiProviderService, "getAll").mockResolvedValue([]);

    render(<ChatTitlesSettings />);

    const attempts = screen.getByLabelText("Title attempts") as HTMLInputElement;
    const save = () =>
      screen.getByRole("button", { name: "Save title attempts" }) as HTMLButtonElement;
    await waitFor(() => expect(attempts.value).toBe("3"));
    expect(attempts.disabled).toBe(true);
    expect(save().disabled).toBe(true);

    fireEvent.click(screen.getByRole("checkbox", { name: /smart session titles/i }));
    await waitFor(() => expect(attempts.disabled).toBe(false));

    fireEvent.change(attempts, { target: { value: "5" } });
    fireEvent.click(save());
    await waitFor(() =>
      expect(put).toHaveBeenCalledWith(authServices.ChatTitleSettingKeys.TitleMaxAttempts, "5"),
    );
  });

  test("keep a value saved before their first read arrives", async () => {
    let answer!: () => void;
    vi.spyOn(authServices.settingsService, "get").mockImplementation((key: string) =>
      key === authServices.ChatTitleSettingKeys.TitleMaxAttempts
        ? new Promise((resolve) => {
            answer = () => resolve({ key, value: "3" });
          })
        : Promise.resolve({
            key,
            value: key === authServices.ChatTitleSettingKeys.SmartTitles ? "true" : "",
          }),
    );
    const put = vi
      .spyOn(authServices.settingsService, "put")
      .mockImplementation(async (key: string, value: string) => ({ key, value }));
    vi.spyOn(authServices.aiProviderService, "getAll").mockResolvedValue([]);

    render(<ChatTitlesSettings />);

    const attempts = screen.getByLabelText("Title attempts") as HTMLInputElement;
    await waitFor(() => expect(attempts.disabled).toBe(false));
    fireEvent.change(attempts, { target: { value: "5" } });
    fireEvent.click(screen.getByRole("button", { name: "Save title attempts" }));
    await waitFor(() =>
      expect(put).toHaveBeenCalledWith(authServices.ChatTitleSettingKeys.TitleMaxAttempts, "5"),
    );

    await act(async () => answer());

    expect(attempts.value).toBe("5");
    expect(
      (screen.getByRole("button", { name: "Save title attempts" }) as HTMLButtonElement).disabled,
    ).toBe(true);
  });
});
