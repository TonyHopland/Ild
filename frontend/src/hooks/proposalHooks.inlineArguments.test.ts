// @vitest-environment jsdom
import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { renderHook, act, waitFor } from "@testing-library/react";
import { boardHint, useProposalList } from "./useProposalList";
import { usePendingProposalCount, type ProposalCountKind } from "./usePendingProposalCount";
import * as signalRHook from "./useSignalR";
import { WorkItemHubProvider } from "./useWorkItemHub";
import {
  fakeHub,
  makeLoopTemplateProposal,
  routeHubs,
  settle,
  type FakeHub,
} from "../test-support";

// The generic proposal hooks take their description of the kind by value. A
// caller that writes it inline makes a new one every render, and that must not
// join or read again; a kind with another key is a different kind, and must.

/** The hub the shared work-item connection is, for hooks rendered under the provider. */
function sharedHub(): FakeHub {
  const hub = fakeHub();
  routeHubs(signalRHook, { "/hubs/work-item": hub });
  return hub;
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe("passed inline, every render", () => {
  test("the pending count's kind joins and reads once", async () => {
    const hub = sharedHub();
    const read = vi.fn(async (): Promise<Record<string, number>> => ({ "tpl-1": 2 }));

    const { result, rerender } = renderHook(
      () =>
        usePendingProposalCount({
          key: "loop-template",
          read: () => read(),
          empty: {},
          hint: "LoopTemplateProposalsChanged",
        }),
      { wrapper: WorkItemHubProvider },
    );
    await waitFor(() => expect(result.current).toEqual({ "tpl-1": 2 }));
    const listening = [...(hub.handlers.LoopTemplateProposalsChanged ?? [])];
    rerender();
    rerender();
    await settle();

    expect(hub.invoke).toHaveBeenCalledTimes(1);
    expect(read).toHaveBeenCalledTimes(1);
    expect(result.current).toEqual({ "tpl-1": 2 });
    expect([...(hub.handlers.LoopTemplateProposalsChanged ?? [])]).toEqual(listening);

    await hub.emit("LoopTemplateProposalsChanged", { templateId: "tpl-1" });
    await settle();
    expect(read).toHaveBeenCalledTimes(2);
  });

  test("the proposal list's kind joins and reads once", async () => {
    const hub = sharedHub();
    const read = vi.fn(async () => [makeLoopTemplateProposal({ id: "p-1" })]);

    const { result, rerender } = renderHook(
      () =>
        useProposalList(
          {
            key: "loop-template",
            read: () => read(),
            hint: () => boardHint("LoopTemplateProposalsChanged"),
          },
          "template",
          "tpl-1",
        ),
      { wrapper: WorkItemHubProvider },
    );
    await waitFor(() => expect(result.current.proposals?.map((p) => p.id)).toEqual(["p-1"]));
    const listening = [...(hub.handlers.LoopTemplateProposalsChanged ?? [])];
    rerender();
    rerender();
    await settle();

    expect(hub.invoke).toHaveBeenCalledTimes(1);
    expect(read).toHaveBeenCalledTimes(1);
    expect(result.current.proposals?.map((p) => p.id)).toEqual(["p-1"]);
    expect([...(hub.handlers.LoopTemplateProposalsChanged ?? [])]).toEqual(listening);

    await hub.emit("LoopTemplateProposalsChanged", { templateId: "tpl-1" });
    await settle();
    expect(read).toHaveBeenCalledTimes(2);
  });
});

describe("another kind for the same target", () => {
  test("the pending count drops the old kind's hint, listens for the new one's and reads it", async () => {
    const hub = sharedHub();
    const readTemplates = vi.fn(async (): Promise<number> => 2);
    const readVariables = vi.fn(async (): Promise<number> => 5);
    const templates: ProposalCountKind<number> = {
      key: "loop-template",
      read: () => readTemplates(),
      empty: 0,
      hint: "LoopTemplateProposalsChanged",
    };
    const variables: ProposalCountKind<number> = {
      key: "global-variable",
      read: () => readVariables(),
      empty: 0,
      hint: "GlobalVariableProposalsChanged",
    };

    const { result, rerender } = renderHook(({ kind }) => usePendingProposalCount(kind), {
      wrapper: WorkItemHubProvider,
      initialProps: { kind: templates },
    });
    await waitFor(() => expect(result.current).toBe(2));
    expect(hub.handlers.LoopTemplateProposalsChanged?.size).toBe(1);

    rerender({ kind: variables });
    await waitFor(() => expect(result.current).toBe(5));

    expect(readVariables).toHaveBeenCalledTimes(1);
    expect(hub.handlers.LoopTemplateProposalsChanged?.size ?? 0).toBe(0);
    expect(hub.handlers.GlobalVariableProposalsChanged?.size).toBe(1);

    await hub.emit("LoopTemplateProposalsChanged", { templateId: "tpl-1" });
    await hub.emit("GlobalVariableProposalsChanged", {});
    await settle();
    expect(readTemplates).toHaveBeenCalledTimes(1);
    expect(readVariables).toHaveBeenCalledTimes(2);
  });

  test("the proposal list drops the old kind's hint, listens for the new one's and reads it", async () => {
    const hub = sharedHub();
    const readTemplates = vi.fn(async () => [makeLoopTemplateProposal({ id: "lt-1" })]);
    const readOthers = vi.fn(async () => [makeLoopTemplateProposal({ id: "other-1" })]);
    const templates = {
      key: "loop-template",
      read: () => readTemplates(),
      hint: () => boardHint("LoopTemplateProposalsChanged"),
    };
    const others = {
      key: "other",
      read: () => readOthers(),
      hint: () => boardHint("GlobalVariableProposalsChanged"),
    };

    const { result, rerender } = renderHook(({ kind }) => useProposalList(kind, "item", "wi-1"), {
      wrapper: WorkItemHubProvider,
      initialProps: { kind: templates },
    });
    await waitFor(() => expect(result.current.proposals?.map((p) => p.id)).toEqual(["lt-1"]));
    expect(hub.handlers.LoopTemplateProposalsChanged?.size).toBe(1);

    rerender({ kind: others });
    await waitFor(() => expect(result.current.proposals?.map((p) => p.id)).toEqual(["other-1"]));

    expect(readOthers).toHaveBeenCalledTimes(1);
    expect(hub.handlers.LoopTemplateProposalsChanged?.size ?? 0).toBe(0);
    expect(hub.handlers.GlobalVariableProposalsChanged?.size).toBe(1);

    await hub.emit("LoopTemplateProposalsChanged", { templateId: "tpl-1" });
    await hub.emit("GlobalVariableProposalsChanged", {});
    await settle();
    expect(readTemplates).toHaveBeenCalledTimes(1);
    expect(readOthers).toHaveBeenCalledTimes(2);
  });

  test("the pending count shows the new kind's empty value, and never the old kind's late answer", async () => {
    sharedHub();
    let answerTemplates: (count: number) => void = () => {};
    let answerVariables: (count: number) => void = () => {};
    const readTemplates = vi.fn(
      () => new Promise<number>((resolve) => (answerTemplates = resolve)),
    );
    const readVariables = vi.fn(
      () => new Promise<number>((resolve) => (answerVariables = resolve)),
    );
    const templates: ProposalCountKind<number> = {
      key: "loop-template",
      read: () => readTemplates(),
      empty: 0,
      hint: "LoopTemplateProposalsChanged",
    };
    const variables: ProposalCountKind<number> = {
      key: "global-variable",
      read: () => readVariables(),
      empty: -1,
      hint: "GlobalVariableProposalsChanged",
    };

    const { result, rerender } = renderHook(({ kind }) => usePendingProposalCount(kind), {
      wrapper: WorkItemHubProvider,
      initialProps: { kind: templates },
    });
    await waitFor(() => expect(readTemplates).toHaveBeenCalledTimes(1));
    rerender({ kind: variables });
    await waitFor(() => expect(readVariables).toHaveBeenCalledTimes(1));
    expect(result.current).toBe(-1);

    await act(async () => answerTemplates(2));
    await settle();
    expect(result.current).toBe(-1);

    await act(async () => answerVariables(5));
    await waitFor(() => expect(result.current).toBe(5));
  });

  test("the pending count keeps the new kind's count when the old kind's read answers after it", async () => {
    sharedHub();
    let answerTemplates: (count: number) => void = () => {};
    let answerVariables: (count: number) => void = () => {};
    const readTemplates = vi.fn(
      () => new Promise<number>((resolve) => (answerTemplates = resolve)),
    );
    const readVariables = vi.fn(
      () => new Promise<number>((resolve) => (answerVariables = resolve)),
    );
    const templates: ProposalCountKind<number> = {
      key: "loop-template",
      read: () => readTemplates(),
      empty: 0,
      hint: "LoopTemplateProposalsChanged",
    };
    const variables: ProposalCountKind<number> = {
      key: "global-variable",
      read: () => readVariables(),
      empty: -1,
      hint: "GlobalVariableProposalsChanged",
    };

    const { result, rerender } = renderHook(({ kind }) => usePendingProposalCount(kind), {
      wrapper: WorkItemHubProvider,
      initialProps: { kind: templates },
    });
    await waitFor(() => expect(readTemplates).toHaveBeenCalledTimes(1));
    rerender({ kind: variables });
    await waitFor(() => expect(readVariables).toHaveBeenCalledTimes(1));

    await act(async () => answerVariables(5));
    await waitFor(() => expect(result.current).toBe(5));

    await act(async () => answerTemplates(2));
    await settle();
    expect(result.current).toBe(5);
  });
});
