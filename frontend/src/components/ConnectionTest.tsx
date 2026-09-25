import { useEffect, useRef, useState } from "react";
import { ApiError, ConnectionTestResult } from "../types";
import "./ConnectionTest.css";

interface ConnectionTestProps {
  run: () => Promise<ConnectionTestResult>;
}

/**
 * A Test button and what the test found, for one saved entity. Key it by the
 * entity and its version: a result describes the entity as it was when tested,
 * so a save (or another entity) starts from a fresh instance, and a response
 * that lands after this instance is gone is dropped.
 */
export default function ConnectionTest({ run }: ConnectionTestProps) {
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<ConnectionTestResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const mounted = useRef(false);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const test = async () => {
    setBusy(true);
    setResult(null);
    setError(null);
    try {
      const answer = await run();
      if (mounted.current) setResult(answer);
    } catch (e) {
      if (mounted.current) setError((e as ApiError | Error).message);
    } finally {
      if (mounted.current) setBusy(false);
    }
  };

  return (
    <div className="connection-test">
      <button
        type="button"
        className="btn btn-secondary btn-small"
        disabled={busy}
        onClick={() => void test()}
      >
        {busy ? "Testing…" : "Test"}
      </button>
      {result && (
        <div
          className={`connection-test-result ${result.ok ? "is-ok" : "is-failed"}`}
          role={result.ok ? "status" : "alert"}
        >
          <div className="connection-test-summary">
            <span className="connection-test-marker">{result.ok ? "✓ OK" : "✗ Failed"}</span>
            <span>{result.message}</span>
          </div>
          {result.detail !== null && <pre className="connection-test-detail">{result.detail}</pre>}
        </div>
      )}
      {error !== null && (
        <div
          className="connection-test-result is-failed"
          role="alert"
        >{`Couldn't run the test: ${error}`}</div>
      )}
    </div>
  );
}
