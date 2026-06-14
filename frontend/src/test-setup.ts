// Global test setup. Runs once per test file before any tests.
//
// React Flow (@xyflow/react) instantiates ResizeObserver asynchronously, which
// jsdom does not provide. Define a permanent polyfill on globalThis so it is
// always available — including across `vi.unstubAllGlobals()` calls and stray
// async callbacks that fire between tests. Defining it per-test in beforeEach
// caused intermittent "ResizeObserver is not defined" failures in CI.
class ResizeObserverStub {
  private callback: ResizeObserverCallback | null = null;
  constructor(callback: ResizeObserverCallback) {
    this.callback = callback;
  }
  observe() {
    if (this.callback) {
      this.callback([], this as unknown as ResizeObserver);
    }
  }
  unobserve() {}
  disconnect() {}
}

globalThis.ResizeObserver = ResizeObserverStub as unknown as typeof ResizeObserver;
