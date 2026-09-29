import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup } from "@testing-library/react";
import PackageFeedsSettings from "./PackageFeedsSettings";
import * as services from "../../../services/auth";

const feed = (credentialProviderMissing: boolean) => ({
  id: "f1",
  name: "company",
  feedUrl: "https://pkgs.dev.azure.com/example-org/_packaging/company",
  patHint: "••••3fa9",
  createdAt: "2026-09-01T10:00:00Z",
  updatedAt: "2026-09-01T10:00:00Z",
  credentialProviderMissing,
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("Package feeds credential provider warning", () => {
  test("warns that NuGet restores won't be authenticated when the server cannot find the provider", async () => {
    vi.spyOn(services.packageFeedService, "list").mockResolvedValue([feed(true)] as any);
    render(<PackageFeedsSettings />);

    const banner = await screen.findByRole("status");
    expect(banner.textContent).toContain("NuGet restores won't be authenticated");
    expect(banner.textContent).toContain("npm feeds are unaffected");
  });

  test("says nothing when the provider is there", async () => {
    vi.spyOn(services.packageFeedService, "list").mockResolvedValue([feed(false)] as any);
    render(<PackageFeedsSettings />);

    await screen.findByText("company");
    expect(screen.queryByText(/NuGet restores won't be authenticated/)).toBeNull();
  });
});
