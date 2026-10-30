import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup } from "@testing-library/react";
import StorageSettings from "./StorageSettings";
import * as services from "../../../services/auth";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("StorageSettings analytics card", () => {
  test("shows an Analytics card after the Disk usage card without asking the server", async () => {
    vi.spyOn(services.storageService, "getOverview").mockResolvedValue({
      databases: [],
      categories: [],
    } as never);
    const clear = vi.spyOn(services.analyticsService, "clear").mockResolvedValue();
    render(<StorageSettings />);

    const disk = screen.getByRole("heading", { name: "Disk usage" });
    const analytics = screen.getByRole("heading", { name: "Analytics" });
    expect(disk.compareDocumentPosition(analytics) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(clear).not.toHaveBeenCalled();
  });
});
