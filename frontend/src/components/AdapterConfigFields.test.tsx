import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup } from "@testing-library/react";
import { ConfigFieldType } from "../types";
import AdapterConfigFields from "../components/AdapterConfigFields";

const sampleSchema = [
  {
    name: "model",
    type: ConfigFieldType.Text,
    label: "Model",
    required: true,
    defaultValue: "gpt-4o",
    description: "Model identifier",
    options: null,
  },
  {
    name: "temperature",
    type: ConfigFieldType.Number,
    label: "Temperature",
    required: false,
    defaultValue: 0.7,
    description: "Controls randomness",
    options: null,
  },
  {
    name: "enableTools",
    type: ConfigFieldType.Toggle,
    label: "Enable Tools",
    required: false,
    defaultValue: true,
    description: "Whether tools are enabled",
    options: null,
  },
  {
    name: "systemPrompt",
    type: ConfigFieldType.Textarea,
    label: "System Prompt",
    required: false,
    defaultValue: null,
    description: "A system prompt",
    options: null,
  },
  {
    name: "region",
    type: ConfigFieldType.Select,
    label: "Region",
    required: false,
    defaultValue: "us-east-1",
    description: "Deployment region",
    options: ["us-east-1", "eu-west-1", "ap-south-1"],
  },
];

describe("AdapterConfigFields", () => {
  afterEach(() => {
    cleanup();
  });

  test("renders fields dynamically based on schema", () => {
    const handleChange = vi.fn();

    render(<AdapterConfigFields schema={sampleSchema} values={{}} onChange={handleChange} />);

    // Labels should be visible
    expect(screen.getByText("Model")).toBeTruthy();
    expect(screen.getByText("Temperature")).toBeTruthy();
    expect(screen.getByText("Enable Tools")).toBeTruthy();
    expect(screen.getByText("System Prompt")).toBeTruthy();
    expect(screen.getByText("Region")).toBeTruthy();
  });

  test("renders text input for Text field type", () => {
    render(
      <AdapterConfigFields
        schema={sampleSchema}
        values={{ model: "claude-3" }}
        onChange={vi.fn()}
      />,
    );

    const modelInput = screen.getByDisplayValue("claude-3");
    expect(modelInput).toBeTruthy();
    expect(modelInput.tagName).toBe("INPUT");
  });

  test("renders number input for Number field type", () => {
    render(
      <AdapterConfigFields
        schema={sampleSchema}
        values={{ temperature: 0.9 }}
        onChange={vi.fn()}
      />,
    );

    const tempInput = screen.getByDisplayValue("0.9");
    expect(tempInput).toBeTruthy();
    expect((tempInput as HTMLInputElement).type).toBe("number");
  });

  test("renders checkbox for Toggle field type", () => {
    render(
      <AdapterConfigFields
        schema={sampleSchema}
        values={{ enableTools: true }}
        onChange={vi.fn()}
      />,
    );

    const checkbox = screen.getByLabelText("Enable Tools") as HTMLInputElement;
    expect(checkbox.type).toBe("checkbox");
    expect(checkbox.checked).toBe(true);
  });

  test("renders textarea for Textarea field type", () => {
    render(
      <AdapterConfigFields
        schema={sampleSchema}
        values={{ systemPrompt: "You are helpful." }}
        onChange={vi.fn()}
      />,
    );

    const textarea = screen.getByDisplayValue("You are helpful.");
    expect(textarea.tagName).toBe("TEXTAREA");
  });

  test("renders select for Select field type with options", () => {
    render(
      <AdapterConfigFields
        schema={sampleSchema}
        values={{ region: "eu-west-1" }}
        onChange={vi.fn()}
      />,
    );

    const select = screen.getByRole("combobox") as HTMLSelectElement;
    expect(select).toBeTruthy();
    expect(select.value).toBe("eu-west-1");
  });

  test("calls onChange with updated value when field changes", () => {
    const handleChange = vi.fn();

    render(
      <AdapterConfigFields
        schema={sampleSchema}
        values={{ model: "gpt-4o" }}
        onChange={handleChange}
      />,
    );

    const modelInput = screen.getByDisplayValue("gpt-4o");
    fireEvent.change(modelInput, { target: { value: "claude-3" } });

    expect(handleChange).toHaveBeenCalledWith("model", "claude-3");
  });

  test("calls onChange with number value for number fields", () => {
    const handleChange = vi.fn();

    render(
      <AdapterConfigFields
        schema={sampleSchema}
        values={{ temperature: 0.7 }}
        onChange={handleChange}
      />,
    );

    const tempInput = screen.getByDisplayValue("0.7");
    fireEvent.change(tempInput, { target: { value: "1.5" } });

    expect(handleChange).toHaveBeenCalledWith("temperature", 1.5);
  });

  test("calls onChange with boolean value for toggle fields", () => {
    const handleChange = vi.fn();

    render(
      <AdapterConfigFields
        schema={sampleSchema}
        values={{ enableTools: false }}
        onChange={handleChange}
      />,
    );

    const checkbox = screen.getByLabelText("Enable Tools");
    fireEvent.click(checkbox);

    expect(handleChange).toHaveBeenCalledWith("enableTools", true);
  });
});
