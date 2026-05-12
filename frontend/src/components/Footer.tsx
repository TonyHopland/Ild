import { useEffect, useState } from "react";

interface HealthVersion {
  version: string;
}

export default function Footer() {
  const [version, setVersion] = useState<string>("");

  useEffect(() => {
    fetch("/api/v1/health")
      .then((res) => res.json() as Promise<HealthVersion>)
      .then((data) => setVersion(data.version ?? ""))
      .catch(() => setVersion(""));
  }, []);

  return (
    <footer className="app-footer">
      <span className="footer-version">ILD v{version || "0.1.0"}-beta</span>
      <style>{`
        .app-footer {
          text-align: center;
          padding: 0.75rem 1rem;
          border-top: 1px solid #2d2d44;
          background-color: #1a1a2e;
          font-size: 0.75rem;
          color: #6b6b80;
        }

        .footer-version {
          font-family: monospace;
        }
      `}</style>
    </footer>
  );
}
