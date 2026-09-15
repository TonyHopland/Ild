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
  createdAt: "2026-09-15T00:00:00Z",
});

describe("resolveToolSelection matches saved keys like the backend does", () => {
  test("a saved key in another case ticks the tool under its canonical key", () => {
    expect(resolveToolSelection(provider("copilot", [tool("ild")]), ["ILD"])).toEqual(["ild"]);
  });

  test("the same key saved twice in different cases is listed once", () => {
    const opencode = provider("opencode", [
      tool("read"),
      tool("write"),
      tool("execute"),
      tool("ild"),
    ]);
    expect(resolveToolSelection(opencode, ["Read", "read", "ILD"])).toEqual(["read", "ild"]);
  });

  test("a Copilot provider type in another case or with spaces still keeps an empty selection off", () => {
    expect(resolveToolSelection(provider(" Copilot ", [tool("ild")]), [])).toEqual([]);
  });
});
