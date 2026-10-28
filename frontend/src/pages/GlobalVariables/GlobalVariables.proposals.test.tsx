import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, fireEvent, cleanup, within } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { AuthContext } from "../../hooks/useAuth";
import * as signalRHook from "../../hooks/useSignalR";
import GlobalVariables from "./index";
import {
  fakeHub,
  makeGlobalVariableProposal,
  reply,
  stubFetch,
  type Call,
} from "../../test-support";
import type { GlobalVariableProposal } from "../../types";
import { WorkItemHubProvider } from "../../hooks/useWorkItemHub";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const rules = {
  id: "g-1",
  name: "review_rules",
  value: "Keep reviews short.\nName the file.",
  updatedAt: "2026-10-01T10:00:00Z",
  usedBy: [],
};

const writing = {
  id: "g-2",
  name: "writing",
  value: "Plain words.",
  updatedAt: "2026-10-02T10:00:00Z",
  usedBy: [],
};

function suggestion(
  id: string,
  name: string,
  rationale: string,
  source: Pick<GlobalVariableProposal["source"], "kind"> &
    Partial<GlobalVariableProposal["source"]>,
  overrides: Partial<GlobalVariableProposal> = {},
) {
  const existing = [rules, writing].find((v) => v.name.toLowerCase() === name.toLowerCase());
  return makeGlobalVariableProposal({
    id,
    name,
    value: `Suggested text from ${id}.`,
    rationale,
    createdAt: "2026-10-03T10:00:00Z",
    snapshotUpdatedAt: existing?.updatedAt ?? null,
    currentValue: existing?.value ?? null,
    variableExists: !!existing,
    createdByLoopRunId: source.kind === "chat" ? null : "run-1",
    createdByRunNodeId: null,
    createdByChatSessionId: source.kind === "chat" ? (source.chatSessionId ?? null) : null,
    source: { workItemId: null, chatSessionId: null, linkable: false, ...source },
    ...overrides,
  });
}

/**
 * Answers the page's requests by method and path from `server`, which a test
 * changes to model what happened elsewhere. The proposals listing honours a
 * `status` filter. `write` answers every POST, PUT and DELETE.
 */
function stubApi(
  server: { variables: unknown[]; proposals: { status: string }[] },
  write: (call: Call) => Response = () => reply(404, { error: "not stubbed" }),
) {
  return stubFetch((call) => {
    const { method, path, query } = call;
    if (method === "GET" && path === "/api/v1/global-variables")
      return reply(200, server.variables);
    if (method === "GET" && path === "/api/v1/global-variables/proposals") {
      const status = query.get("status");
      return reply(
        200,
        server.proposals.filter((p) => !status || p.status === status),
      );
    }
    if (method === "GET" && path === "/api/v1/global-variables/proposals/pending-count")
      return reply(200, {
        count: server.proposals.filter((p) => p.status === "Pending").length,
      });
    if (method === "GET" && path === "/api/v1/settings/globalVariables.maxLength")
      return reply(200, { key: "globalVariables.maxLength", value: "4000" });
    if (method !== "GET") return write(call);
    return reply(404, { error: "not stubbed" });
  });
}

function mockHub() {
  const hub = fakeHub();
  vi.spyOn(signalRHook, "useSignalR").mockReturnValue(hub.connection);
  return hub;
}

function renderPage() {
  const authValue = {
    user: { id: "1", username: "test", createdAt: "" },
    token: "test-token",
    isAuthenticated: true,
    isLoading: false,
    login: vi.fn(),
    logout: vi.fn(),
  };
  return render(
    <MemoryRouter>
      <AuthContext.Provider value={authValue}>
        <GlobalVariables />
      </AuthContext.Provider>
    </MemoryRouter>,
    { wrapper: WorkItemHubProvider },
  );
}

/** The smallest element holding the suggestion's rationale and its decision buttons. */
async function cardOf(rationale: string) {
  let card = (await screen.findByText(rationale, { exact: false })).parentElement!;
  while (!within(card).queryByRole("button", { name: "Approve" }) && card.parentElement) {
    card = card.parentElement;
  }
  return card;
}

function follows(a: Element, b: Element) {
  return !!(a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING);
}

const reads = (calls: Call[], path: string) =>
  calls.filter((c) => c.method === "GET" && c.path === path).length;

describe("suggestions on the Global Variables page", () => {
  test("lists each pending suggestion under its variable, new ones as new variables, each linking to where it came from", async () => {
    mockHub();
    stubApi({
      variables: [rules, writing],
      proposals: [
        suggestion("gp-run", "review_rules", "From the loop run.", {
          kind: "run",
          workItemId: "wi-7",
          linkable: true,
        }),
        suggestion("gp-chat", "REVIEW_RULES", "From my chat.", {
          kind: "chat",
          chatSessionId: "c-1",
          linkable: true,
        }),
        suggestion("gp-gone", "review_rules", "From a deleted run.", { kind: "deleted" }),
        suggestion("gp-others", "writing", "From someone else's chat.", {
          kind: "chat",
          chatSessionId: "c-2",
          linkable: false,
        }),
        suggestion("gp-new", "commit_style", "Nothing says this yet.", {
          kind: "run",
          workItemId: "wi-8",
          linkable: true,
        }),
        suggestion(
          "gp-done",
          "writing",
          "Already approved.",
          { kind: "run", workItemId: "wi-9", linkable: true },
          { status: "Approved", decidedAt: "2026-10-03T11:00:00Z" },
        ),
      ],
    });
    renderPage();

    const fromRun = await cardOf("From the loop run.");
    const [editRules, editWriting] = await screen.findAllByRole("button", { name: /edit/i });
    for (const rationale of ["From the loop run.", "From my chat.", "From a deleted run."]) {
      const card = await cardOf(rationale);
      expect(follows(editRules, card)).toBe(true);
      expect(follows(card, editWriting)).toBe(true);
    }
    expect(follows(editWriting, await cardOf("From someone else's chat."))).toBe(true);

    expect(fromRun.textContent).toContain("Suggested text from gp-run.");
    expect(within(fromRun).getByRole("link").getAttribute("href")).toBe(
      "/taskboard/wi-7?globalProposal=gp-run",
    );
    expect(
      within(await cardOf("From my chat."))
        .getByRole("link")
        .getAttribute("href"),
    ).toBe("/chat/c-1?globalProposal=gp-chat");
    const gone = await cardOf("From a deleted run.");
    expect(gone.textContent).toMatch(/source deleted/i);
    expect(within(gone).queryAllByRole("link")).toHaveLength(0);
    expect(within(await cardOf("From someone else's chat.")).queryAllByRole("link")).toHaveLength(
      0,
    );

    const fresh = await cardOf("Nothing says this yet.");
    expect(fresh.textContent).toMatch(/new variable/i);
    expect(fresh.textContent).toContain("commit_style");
    expect(within(fresh).getByRole("link").getAttribute("href")).toBe(
      "/taskboard/wi-8?globalProposal=gp-new",
    );

    expect(screen.queryByText("Already approved.", { exact: false })).toBeNull();
  });

  test("approving a suggestion on the page re-reads the variables and the suggestions", async () => {
    mockHub();
    const server = {
      variables: [rules] as unknown[],
      proposals: [
        suggestion("gp-run", "review_rules", "From the loop run.", {
          kind: "run",
          workItemId: "wi-7",
          linkable: true,
        }),
      ],
    };
    const calls = stubApi(server, (call) => {
      server.variables = [{ ...rules, value: "Suggested text from gp-run." }];
      server.proposals = [{ ...server.proposals[0], status: "Approved" }];
      return call.path.endsWith("/approve")
        ? reply(200, { proposal: server.proposals[0], variable: server.variables[0] })
        : reply(404);
    });
    renderPage();
    const card = await cardOf("From the loop run.");
    const variableReads = reads(calls, "/api/v1/global-variables");
    const proposalReads = reads(calls, "/api/v1/global-variables/proposals");

    fireEvent.click(within(card).getByRole("button", { name: "Approve" }));

    await waitFor(() =>
      expect(screen.queryByText("From the loop run.", { exact: false })).toBeNull(),
    );
    expect(calls.filter((c) => c.method === "POST").map((c) => c.path)).toEqual([
      "/api/v1/global-variables/proposals/gp-run/approve",
    ]);
    expect(reads(calls, "/api/v1/global-variables")).toBeGreaterThan(variableReads);
    expect(reads(calls, "/api/v1/global-variables/proposals")).toBeGreaterThan(proposalReads);
    expect(document.body.textContent).toContain("Suggested text from gp-run.");
  });

  test("a hint that suggestions changed brings in a new one and the variable's value without a reload", async () => {
    const { emit: hint } = mockHub();
    const server = { variables: [rules] as unknown[], proposals: [] as { status: string }[] };
    stubApi(server);
    renderPage();
    await screen.findByRole("button", { name: /edit/i });
    expect(screen.queryByRole("button", { name: "Approve" })).toBeNull();

    server.variables = [{ ...rules, value: "Edited elsewhere." }];
    server.proposals = [
      suggestion("gp-late", "review_rules", "Made just now.", {
        kind: "run",
        workItemId: "wi-7",
        linkable: true,
      }),
    ];
    await hint("GlobalVariableProposalsChanged", { name: "review_rules" });

    await cardOf("Made just now.");
    await waitFor(() => expect(document.body.textContent).toContain("Edited elsewhere."));
  });

  test("leaving the page takes its listener off the shared hub", async () => {
    const hub = mockHub();
    stubApi({ variables: [rules] as unknown[], proposals: [] as { status: string }[] });
    const { unmount } = renderPage();
    await waitFor(() => expect(hub.handlers["GlobalVariableProposalsChanged"]?.size).toBe(1));

    unmount();

    expect(hub.handlers["GlobalVariableProposalsChanged"]?.size ?? 0).toBe(0);
  });
});
