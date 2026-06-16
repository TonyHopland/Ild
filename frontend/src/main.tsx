import React from "react";
import ReactDOM from "react-dom/client";
import App from "./App";
import "./index.css";
// App-wide button, modal and form styling shared across pages and dialogs.
import "./components/shared.css";

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>,
);
