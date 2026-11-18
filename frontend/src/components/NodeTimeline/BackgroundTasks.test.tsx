// @vitest-environment jsdom
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, test } from "vite-plus/test";
import BackgroundTasks from "./BackgroundTasks";

afterEach(cleanup);

describe("BackgroundTasks", () => {
  test("renders nothing when the CLI waits for no background work", () => {
    const { container } = render(<BackgroundTasks tasks={[]} />);
    expect(container.childElementCount).toBe(0);
  });

  test("lists each task with its kind and description, and a subagent with its type", () => {
    render(
      <BackgroundTasks
        tasks={[
          { kind: "local_bash", description: "Run the dev server" },
          { kind: "local_agent", description: "Review the diff", subagentType: "general-purpose" },
        ]}
      />,
    );
    expect(screen.getByText("Run the dev server")).toBeTruthy();
    expect(screen.getByText("local_bash")).toBeTruthy();
    expect(screen.getByText("local_agent: general-purpose")).toBeTruthy();
  });
});
