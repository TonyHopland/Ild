export function opensInNewTab(href: string | undefined, appUrl: string): boolean {
  if (!href || href.startsWith("#")) {
    return false;
  }
  try {
    const resolved = new URL(href, appUrl);
    return (
      (resolved.protocol === "http:" || resolved.protocol === "https:") &&
      resolved.host !== new URL(appUrl).host
    );
  } catch {
    return false;
  }
}
