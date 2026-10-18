import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { renderHook, act, waitFor } from "@testing-library/react";
import { boardHint, useProposalList } from "./useProposalList";
import { usePendingProposalCount } from "./usePendingProposalCount";
import { fakeHub, makeLoopTemplateProposal } from "../test-support";

// The generic proposal hooks take their description of the kind (the read, the
// empty value, the kind) by value: a caller that writes it inline makes a new
// one every render, and that must not join or read again.

/** Lets React run whatever a settled read set off. */
const settle = () =>
  act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 20));
  });

afterEach(() => {
  vi.restoreAllMocks();
});

describe("passed inline, every render", () => {
  test("the pending count's read and empty value join and read once", async () => {
    const hub = fakeHub();
    const read = vi.fn(async (): Promise<Record<string, number>> => ({ "tpl-1": 2 }));

    const { result, rerender } = renderHook(() =>
      usePendingProposalCount(() => read(), "LoopTemplateProposalsChanged", {}, hub.connection),
    );
    await waitFor(() => expect(result.current).toEqual({ "tpl-1": 2 }));
    rerender();
    rerender();
    await settle();

    expect(hub.invoke).toHaveBeenCalledTimes(1);
    expect(read).toHaveBeenCalledTimes(1);
    expect(result.current).toEqual({ "tpl-1": 2 });
  });

  test("the proposal list's kind joins and reads once", async () => {
    const hub = fakeHub();
    const read = vi.fn(async () => [makeLoopTemplateProposal({ id: "p-1" })]);

    const { result, rerender } = renderHook(() =>
      useProposalList(
        {
          read: () => read(),
          hint: () => boardHint("LoopTemplateProposalsChanged"),
        },
        "template",
        "tpl-1",
        hub.connection,
      ),
    );
    await waitFor(() => expect(result.current.proposals?.map((p) => p.id)).toEqual(["p-1"]));
    rerender();
    rerender();
    await settle();

    expect(hub.invoke).toHaveBeenCalledTimes(1);
    expect(read).toHaveBeenCalledTimes(1);
    expect(result.current.proposals?.map((p) => p.id)).toEqual(["p-1"]);
  });
});
