import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, act } from "@testing-library/react";
import ContainerStats, { STATS_POLL_MS, formatBytes } from "./ContainerStats";
import * as authServices from "../../services/auth";
import type { ContainerStats as Stats } from "../../types";

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.restoreAllMocks();
});

const GIB = 1024 ** 3;

function mockStats(...answers: Array<Stats | Error>) {
  const spy = vi.spyOn(authServices.loopRunService, "getContainerStats");
  for (const answer of answers)
    if (answer instanceof Error) spy.mockRejectedValueOnce(answer);
    else spy.mockResolvedValueOnce(answer);
  return spy;
}

async function flush() {
  await act(async () => {
    await Promise.resolve();
    await Promise.resolve();
  });
}

describe("ContainerStats", () => {
  test("shows each usage against its limit as a bar, a percentage and absolute values", async () => {
    mockStats({
      state: "running",
      cpuCores: 1.2,
      cpuLimitCores: 4,
      memoryBytes: 2.1 * GIB,
      memoryLimitBytes: 8 * GIB,
    });
    render(<ContainerStats loopRunId="run-1" active />);

    expect(await screen.findByText("1.2 / 4 CPU · 30%")).toBeTruthy();
    expect(screen.getByText("2.1 GiB / 8.0 GiB · 26%")).toBeTruthy();
    expect(screen.getByRole("progressbar", { name: "CPU" }).getAttribute("aria-valuenow")).toBe(
      "30",
    );
    expect(screen.getByRole("progressbar", { name: "Memory" }).getAttribute("aria-valuenow")).toBe(
      "26",
    );
  });

  test("shows only the usage, with no bar, when there is no limit", async () => {
    mockStats({ state: "running", cpuCores: 0.5, cpuLimitCores: null, memoryBytes: GIB });
    render(<ContainerStats loopRunId="run-1" active />);

    expect((await screen.findAllByText("no limit")).length).toBe(2);
    expect(screen.queryByRole("progressbar")).toBeNull();
  });

  test("shows network and disk only when the engine exposes them", async () => {
    mockStats({ state: "running", cpuCores: 1, cpuLimitCores: 2 });
    const { unmount } = render(<ContainerStats loopRunId="run-1" active />);
    await screen.findByText("1 / 2 CPU · 50%");
    expect(screen.queryByText("Network")).toBeNull();
    expect(screen.queryByText("Disk")).toBeNull();
    unmount();

    mockStats({
      state: "running",
      networkRxBytes: 2048,
      networkTxBytes: 512,
      diskReadBytes: 1024 ** 2,
      diskWriteBytes: 0,
    });
    render(<ContainerStats loopRunId="run-1" active />);
    expect(await screen.findByText("↓ 2.0 KiB · ↑ 512 B")).toBeTruthy();
    expect(screen.getByText("read 1.0 MiB · written 0 B")).toBeTruthy();
  });

  test("says the container is not running when it is stopped", async () => {
    mockStats({ state: "stopped" });
    render(<ContainerStats loopRunId="run-1" active />);

    expect(await screen.findByText("Container not running")).toBeTruthy();
  });

  test.each([
    ["the host has no container to ask", { state: "unavailable" } as Stats],
    ["the request fails", new Error("boom")],
  ])("says the stats are unavailable when %s", async (_name, answer) => {
    mockStats(answer);
    render(<ContainerStats loopRunId="run-1" active />);

    expect(await screen.findByText("Stats unavailable")).toBeTruthy();
  });

  test("polls while active and stops once the tab is hidden", async () => {
    vi.useFakeTimers();
    const spy = mockStats();
    spy.mockResolvedValue({ state: "running", cpuCores: 1, cpuLimitCores: 2 });
    const { rerender } = render(<ContainerStats loopRunId="run-1" active />);
    await flush();
    expect(spy).toHaveBeenCalledTimes(1);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(STATS_POLL_MS);
    });
    expect(spy).toHaveBeenCalledTimes(2);

    rerender(<ContainerStats loopRunId="run-1" active={false} />);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(STATS_POLL_MS * 5);
    });
    expect(spy).toHaveBeenCalledTimes(2);

    rerender(<ContainerStats loopRunId="run-1" active />);
    await flush();
    expect(spy).toHaveBeenCalledTimes(3);
  });

  test("formats byte counts in binary units", () => {
    expect(formatBytes(0)).toBe("0 B");
    expect(formatBytes(1536)).toBe("1.5 KiB");
    expect(formatBytes(8 * GIB)).toBe("8.0 GiB");
    expect(formatBytes(12 * GIB)).toBe("12 GiB");
  });
});
