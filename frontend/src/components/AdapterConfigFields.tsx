import { ConfigFieldDescriptor, ConfigFieldType } from "../types";

interface AdapterConfigFieldsProps {
  schema: ConfigFieldDescriptor[];
  values: Record<string, string | number | boolean>;
  onChange: (name: string, value: string | number | boolean) => void;
}

export default function AdapterConfigFields({
  schema,
  values,
  onChange,
}: AdapterConfigFieldsProps) {
  return (
    <>
      {schema.map((field) => (
        <div key={field.name} className="config-field">
          {field.type === ConfigFieldType.Toggle ? (
            <label className="checkbox-label">
              <input
                id={`adapter-${field.name}`}
                type="checkbox"
                checked={(values[field.name] as boolean) ?? false}
                onChange={(e) => onChange(field.name, e.target.checked)}
              />
              {field.label}
            </label>
          ) : (
            <>
              <label htmlFor={`adapter-${field.name}`}>{field.label}</label>
              {field.type === ConfigFieldType.Text && (
                <input
                  id={`adapter-${field.name}`}
                  type="text"
                  value={(values[field.name] as string) ?? ""}
                  onChange={(e) => onChange(field.name, e.target.value)}
                />
              )}
              {field.type === ConfigFieldType.Number && (
                <input
                  id={`adapter-${field.name}`}
                  type="number"
                  step="any"
                  value={(values[field.name] as number) ?? ""}
                  onChange={(e) => onChange(field.name, parseFloat(e.target.value))}
                />
              )}
              {field.type === ConfigFieldType.Textarea && (
                <textarea
                  id={`adapter-${field.name}`}
                  rows={3}
                  value={(values[field.name] as string) ?? ""}
                  onChange={(e) => onChange(field.name, e.target.value)}
                />
              )}
              {field.type === ConfigFieldType.Select && field.options && (
                <select
                  id={`adapter-${field.name}`}
                  value={(values[field.name] as string) ?? field.options[0] ?? ""}
                  onChange={(e) => onChange(field.name, e.target.value)}
                >
                  {field.options.map((option) => (
                    <option key={option} value={option}>
                      {option}
                    </option>
                  ))}
                </select>
              )}
            </>
          )}
        </div>
      ))}
    </>
  );
}
