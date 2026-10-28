import { expect, test } from "vite-plus/test";
import { phonePaneOf } from "./phonePane";

test.each([
  ["a phone showing the list", "list", true, true],
  ["a phone that has opened a chat", "chat", true, false],
  ["a wide screen with the list flag set", "chat", false, true],
  ["a wide screen", "chat", false, false],
])("%s: the %s pane shows", (_, pane, isMobile, listShown) => {
  expect(phonePaneOf(isMobile, listShown)).toBe(pane);
});
