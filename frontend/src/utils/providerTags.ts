import type { AiProvider } from "../types";

/**
 * Upper-cases one character at a time, as the backend's ToUpperInvariant does:
 * a letter whose upper case is several characters ("ß" → "SS") stays as it is.
 */
const normalizeTag = (tag: string) =>
  Array.from(tag.trim(), (char) => {
    const upper = char.toUpperCase();
    return Array.from(upper).length === 1 ? upper : char;
  }).join("");

/** Provider tags compare trimmed and case-insensitively, as the backend matches them. */
export function sameTag(a: string, b: string): boolean {
  return normalizeTag(a) === normalizeTag(b);
}

export interface ProviderForTag {
  provider: AiProvider | null;
  /** True when the tag itself picked the provider rather than the default fallback. */
  byTag: boolean;
}

/**
 * The provider an AI node with `tag` runs on, by the backend's rule: the
 * provider holding the tag, else the default provider, else none.
 */
export function resolveProviderForTag(providers: AiProvider[], tag: string): ProviderForTag {
  const tagged = tag.trim()
    ? providers.find((provider) => (provider.tags ?? []).some((held) => sameTag(held, tag)))
    : undefined;
  if (tagged) return { provider: tagged, byTag: true };
  return { provider: providers.find((provider) => provider.isDefault) ?? null, byTag: false };
}
