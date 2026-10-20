import { useMediaQuery } from "./useMediaQuery";

/** The phone layout's breakpoint; the CSS uses the same `max-width: 767px`. */
export const MOBILE_QUERY = "(max-width: 767px)";

/** Whether the viewport is phone-sized, kept live as it resizes. */
export function useIsMobile(): boolean {
  return useMediaQuery(MOBILE_QUERY);
}
