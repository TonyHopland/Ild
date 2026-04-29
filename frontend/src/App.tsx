import { BrowserRouter, Routes, Route, Navigate } from "react-router-dom";
import { AuthContext, useProvideAuth, useAuth } from "./hooks/useAuth";
import Header from "./components/Header";
import Login from "./pages/Login";
import Taskboard from "./pages/Taskboard";
import LoopEditor from "./pages/LoopEditor";
import LoopRunMonitor from "./pages/LoopRunMonitor";
import EventLogViewer from "./pages/EventLogViewer";
import Settings from "./pages/Settings";
import Repositories from "./pages/Repositories";
import RemoteProviders from "./pages/RemoteProviders";
import AiProviders from "./pages/AiProviders";
import "./App.css";

function ProtectedRoute({ children }: { children: React.ReactNode }) {
  const { isAuthenticated } = useAuth();
  if (!isAuthenticated) {
    return <Navigate to="/login" replace />;
  }
  return <>{children}</>;
}

function AppRoutes() {
  const { isAuthenticated, isLoading } = useAuth();

  if (isLoading) {
    return (
      <div className="page-container">
        <p>Loading...</p>
      </div>
    );
  }

  if (!isAuthenticated) {
    return (
      <Routes>
        <Route path="/login" element={<Login />} />
        <Route path="*" element={<Navigate to="/login" replace />} />
      </Routes>
    );
  }

  return (
    <div className="app">
      <Header />
      <main className="app-main">
        <Routes>
          <Route path="/" element={<Navigate to="/taskboard" replace />} />
          <Route
            path="/taskboard"
            element={
              <ProtectedRoute>
                <Taskboard />
              </ProtectedRoute>
            }
          />
          <Route
            path="/loop-editor"
            element={
              <ProtectedRoute>
                <LoopEditor />
              </ProtectedRoute>
            }
          />
          <Route
            path="/loop-runs"
            element={
              <ProtectedRoute>
                <LoopRunMonitor />
              </ProtectedRoute>
            }
          />
          <Route
            path="/loop-runs/:runId/events"
            element={
              <ProtectedRoute>
                <EventLogViewer />
              </ProtectedRoute>
            }
          />
          <Route
            path="/settings"
            element={
              <ProtectedRoute>
                <Settings />
              </ProtectedRoute>
            }
          />
          <Route
            path="/repositories"
            element={
              <ProtectedRoute>
                <Repositories />
              </ProtectedRoute>
            }
          />
          <Route
            path="/remote-providers"
            element={
              <ProtectedRoute>
                <RemoteProviders />
              </ProtectedRoute>
            }
          />
          <Route
            path="/ai-providers"
            element={
              <ProtectedRoute>
                <AiProviders />
              </ProtectedRoute>
            }
          />
          <Route path="*" element={<Navigate to="/taskboard" replace />} />
        </Routes>
      </main>
    </div>
  );
}

export default function App() {
  const auth = useProvideAuth();

  return (
    <AuthContext.Provider value={auth}>
      <BrowserRouter>
        <AppRoutes />
      </BrowserRouter>
    </AuthContext.Provider>
  );
}
