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

  test.each([
    ["Text", { model: "claude-3" }, () => screen.getByDisplayValue("claude-3"), "INPUT", "text"],
    ["Number", { temperature: 0.9 }, () => screen.getByDisplayValue("0.9"), "INPUT", "number"],
    [
      "Toggle",
      { enableTools: true },
      () => screen.getByLabelText("Enable Tools"),
      "INPUT",
      "checkbox",
    ],
    [
      "Textarea",
      { systemPrompt: "You are helpful." },
      () => screen.getByDisplayValue("You are helpful."),
      "TEXTAREA",
      "textarea",
    ],
    ["Select", { region: "eu-west-1" }, () => screen.getByRole("combobox"), "SELECT", "select-one"],
  ] as const)("renders the control of the %s field type", (_type, values, find, tag, type) => {
    render(<AdapterConfigFields schema={sampleSchema} values={values} onChange={vi.fn()} />);

    const control = find() as HTMLInputElement;
    expect(control.tagName).toBe(tag);
    expect(control.type).toBe(type);
    if (type === "checkbox") expect(control.checked).toBe(true);
    else expect(control.value).toBe(String(Object.values(values)[0]));
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

  test("renders field descriptions as help text", () => {
    render(<AdapterConfigFields schema={sampleSchema} values={{}} onChange={vi.fn()} />);

    // Help text lets a schema like the "Custom MCP servers (JSON)" field explain
    // its expected shape to the user.
    expect(screen.getByText("Model identifier")).toBeTruthy();
    expect(screen.getByText("A system prompt")).toBeTruthy();
  });

  test("round-trips a Textarea value through onChange", () => {
    const handleChange = vi.fn();

    render(
      <AdapterConfigFields
        schema={sampleSchema}
        values={{ systemPrompt: "" }}
        onChange={handleChange}
      />,
    );

    const textarea = screen.getByLabelText("System Prompt");
    fireEvent.change(textarea, {
      target: { value: '{"chrome-devtools":{"command":["npx"]}}' },
    });

    expect(handleChange).toHaveBeenCalledWith(
      "systemPrompt",
      '{"chrome-devtools":{"command":["npx"]}}',
    );
  });
});
