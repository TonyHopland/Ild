import { Navigate, Route } from "react-router";
import { ROUTES } from "./constants";

/** Global Variables used to be a page of its own; old bookmarks land on its Settings section. */
export const globalVariablesRedirect = (
  <Route path="/global-variables" element={<Navigate to={ROUTES.GLOBAL_VARIABLES} replace />} />
);
