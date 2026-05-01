import { describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent } from "@testing-library/react";
import ErrorBanner from "./ErrorBanner";

describe("ErrorBanner", () => {
  test("renders the message", () => {
    render(<ErrorBanner message="Something failed" onDismiss={() => {}} />);
    expect(screen.getByRole("alert").textContent).toContain("Something failed");
  });

  test("renders nothing when message is empty", () => {
    const { container } = render(<ErrorBanner message="" onDismiss={() => {}} />);
    expect(container.firstChild).toBeNull();
  });

  test("calls onDismiss when the dismiss button is clicked", () => {
    const onDismiss = vi.fn();
    const { container } = render(<ErrorBanner message="Boom" onDismiss={onDismiss} />);
    const btn = container.querySelector("button[aria-label='Dismiss error']");
    if (btn) fireEvent.click(btn);
    expect(onDismiss).toHaveBeenCalledTimes(1);
  });
});
