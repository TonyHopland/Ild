import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, createEvent, cleanup, act } from "@testing-library/react";
import AttachmentPicker from "./AttachmentPicker";
import { useAttachmentStaging } from "./useAttachmentStaging";
import { settingsService, workItemService } from "../../services/auth";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

const LIMITS = {
  maxBytesPerFile: 25 * 1024 * 1024,
  maxFilesPerRequest: 10,
  maxTotalBytesPerWorkItem: 250 * 1024 * 1024,
};

/** The picker as it is mounted: inside a form that owns the paste handler. */
function Harness() {
  const staging = useAttachmentStaging("wi-1", LIMITS);
  return (
    <form onPaste={staging.handlePaste}>
      <textarea aria-label="Notes" />
      <AttachmentPicker staging={staging} inputId="test-attachments" />
      <button type="button" onClick={() => void staging.uploadAll("wi-1")}>
        Upload
      </button>
    </form>
  );
}

async function renderPicker() {
  vi.spyOn(settingsService, "getAttachmentLimits").mockResolvedValue({
    maxBytesPerFile: 25 * 1024 * 1024,
    maxFilesPerRequest: 10,
    maxTotalBytesPerWorkItem: 250 * 1024 * 1024,
  });
  render(<Harness />);
  await act(async () => {
    await Promise.resolve();
  });
}

const notes = () => screen.getByLabelText("Notes");

describe("staging through the picker", () => {
  test("names a pasted file that arrives without a name of its own", async () => {
    await renderPicker();

    await act(async () => {
      fireEvent.paste(notes(), {
        clipboardData: { files: [new File(["x"], "", { type: "image/png" })], types: ["Files"] },
      });
      await Promise.resolve();
    });

    // The server's own fallback is the literal "attachment", which would make
    // every pasted screenshot on an item indistinguishable from the next.
    const removeButton = screen.getByRole("button", { name: /^Remove pasted-/ });
    expect(removeButton.getAttribute("aria-label")).toMatch(/^Remove pasted-\d{8}-\d{6}\.png$/);
  });

  test("leaves a paste that carries no file alone", async () => {
    await renderPicker();

    const paste = createEvent.paste(notes(), {
      clipboardData: { files: [], items: [], types: ["text/plain"] },
    });
    await act(async () => {
      fireEvent(notes(), paste);
      await Promise.resolve();
    });

    expect(paste.defaultPrevented).toBe(false);
    expect(screen.queryByRole("button", { name: /^Remove / })).toBeNull();
  });

  test("a file already uploaded is no longer the staging list's to remove", async () => {
    await renderPicker();
    vi.spyOn(workItemService, "uploadAttachment").mockImplementation((_id: string, file: File) =>
      file.name === "b.pdf"
        ? Promise.reject({ status: 503, message: "WorkItemServer unreachable" })
        : Promise.resolve([]),
    );

    await act(async () => {
      fireEvent.change(document.querySelector('input[type="file"]') as HTMLInputElement, {
        target: {
          files: [
            new File(["x"], "a.png", { type: "image/png" }),
            new File(["x"], "b.pdf", { type: "application/pdf" }),
          ],
        },
      });
      await Promise.resolve();
    });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Upload" }));
      await Promise.resolve();
    });

    // a.png is on the work item now; taking it off is the overview's job, and
    // dropping it here would only stop an answer's note from naming it.
    expect(screen.queryByRole("button", { name: "Remove a.png" })).toBeNull();
    expect(screen.getByRole("button", { name: "Remove b.pdf" })).toBeTruthy();
    expect(document.body.textContent).toContain("WorkItemServer unreachable");
  });
});
