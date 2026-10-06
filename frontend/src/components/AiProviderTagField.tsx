import type { AiProvider } from "../types";
import { resolveProviderForTag } from "../utils/providerTags";

/** States the provider the backend will run on for `tag`. */
function describeProviderForTag(providers: AiProvider[], tag: string, cannotRun: string): string {
  const { provider, byTag } = resolveProviderForTag(providers, tag);
  const tagSet = tag.trim() !== "";
  if (byTag && provider) return `Runs on ${provider.name}`;
  if (provider) {
    return tagSet
      ? `No provider has this tag — runs on the default provider (${provider.name})`
      : `Runs on the default provider (${provider.name})`;
  }
  return tagSet
    ? `No provider has this tag and no default provider is configured, so ${cannotRun}.`
    : `No default provider is configured, so ${cannotRun}.`;
}

/** A provider tag, resolved as an AI node's is; with `providers` null it states nothing. */
export function AiProviderTagField({
  tag,
  providers,
  onChange,
  id = "ai-provider-tag",
  disabled = false,
  cannotRun = "this node cannot run",
}: {
  tag: string;
  providers: AiProvider[] | null;
  onChange: (value: string) => void;
  id?: string;
  disabled?: boolean;
  /** Ends the sentence shown when nothing can be resolved, e.g. "this node cannot run". */
  cannotRun?: string;
}) {
  const suggestions = [
    ...new Set((providers ?? []).flatMap((provider) => provider.tags ?? [])),
  ].sort((a, b) => a.localeCompare(b, undefined, { sensitivity: "base" }));
  return (
    <div className="config-field">
      <label htmlFor={id}>Provider tag</label>
      <input
        id={id}
        type="text"
        list={`${id}-suggestions`}
        value={tag}
        onChange={(event) => onChange(event.target.value)}
        placeholder="Default provider"
        disabled={disabled}
      />
      <datalist id={`${id}-suggestions`}>
        {suggestions.map((suggestion) => (
          <option key={suggestion} value={suggestion} />
        ))}
      </datalist>
      {providers && (
        <small className="config-help-text">
          {describeProviderForTag(providers, tag, cannotRun)}
        </small>
      )}
    </div>
  );
}
