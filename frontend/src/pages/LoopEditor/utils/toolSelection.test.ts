import { describe, expect, test } from "vite-plus/test";
import type { AiProvider, AiToolDefinition } from "../../../types";
import { resolveToolSelection } from "./toolSelection";

const tool = (key: string): AiToolDefinition => ({
  key,
  label: key,
  description: "",
  defaultEnabled: true,
});

const provider = (type: string, supportedTools: AiToolDefinition[]): AiProvider => ({
  id: `${type}-1`,
  name: type,
  type,
  baseUrl: "",
  apiKey: "",
  model: "",
  isDefault: false,
  parallelism: 1,
  supportedTools,
  createdAt: "2026-09-14T00:00:00Z",
});

const opencode = provider("opencode", [tool("read"), tool("write"), tool("execute"), tool("ild")]);

// Copilot and Codex: their only tool is "ild", so a saved list without it means ILD off.
describe.each([
  provider("copilot", [tool("ild")]),
  provider("codex", [tool("ild")]),
  provider(" Codex ", [tool("ild")]),
])("resolveToolSelection for the ild-only provider $type", (ildOnly) => {
  test("a saved empty list keeps ILD unticked", () => {
    expect(resolveToolSelection(ildOnly, [])).toEqual([]);
  });

  test("a saved list with no ILD entry keeps ILD unticked", () => {
    expect(resolveToolSelection(ildOnly, ["read"])).toEqual([]);
  });

  test("no saved list starts with ILD ticked", () => {
    expect(resolveToolSelection(ildOnly, undefined)).toEqual(["ild"]);
    expect(resolveToolSelection(ildOnly, null)).toEqual(["ild"]);
  });

  test("a saved ILD selection stays ticked", () => {
    expect(resolveToolSelection(ildOnly, ["ild"])).toEqual(["ild"]);
  });
});

describe("resolveToolSelection for the other providers", () => {
  test("an empty list still falls back to the provider defaults", () => {
    expect(resolveToolSelection(opencode, [])).toEqual(["read", "write", "execute", "ild"]);
  });

  test("a fully-filtered list still falls back to the provider defaults", () => {
    expect(resolveToolSelection(opencode, ["bogus"])).toEqual(["read", "write", "execute", "ild"]);
  });

  test("an explicit selection is kept", () => {
    expect(resolveToolSelection(opencode, ["read", "ild"])).toEqual(["read", "ild"]);
  });

  test("no provider means no tools", () => {
    expect(resolveToolSelection(null, ["read"])).toEqual([]);
  });
});
