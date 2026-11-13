import { describe, expect, test } from "vite-plus/test";
import { vscodeAttachUri } from "../vscodeAttachUri";

const attachedAbc =
  "vscode://vscode-remote/attached-container+7b22636f6e7461696e65724e616d65223a222f696c642d72756e2d616263227d";

describe("vscodeAttachUri", () => {
  test("names the container by the hex of its JSON description and opens the folder", () => {
    expect(vscodeAttachUri("ild-run-abc", "/ild-run/abc/repo")).toBe(
      `${attachedAbc}/ild-run/abc/repo`,
    );
  });

  test("keeps a single leading slash on a name that already has one", () => {
    expect(vscodeAttachUri("/ild-run-abc", "/ild-run/abc/repo")).toBe(
      `${attachedAbc}/ild-run/abc/repo`,
    );
  });

  test("escapes a folder with spaces", () => {
    expect(vscodeAttachUri("ild-run-abc", "/work/my repo")).toBe(`${attachedAbc}/work/my%20repo`);
  });
});
