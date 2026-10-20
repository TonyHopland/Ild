import React from "react";
import ReactDOM from "react-dom/client";
import App from "./App";
// Design tokens first: every stylesheet after it reads these variables.
import "./tokens.css";
import "./index.css";
// App-wide button, modal and form styling shared across pages and dialogs.
import "./components/shared.css";

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>,
);
