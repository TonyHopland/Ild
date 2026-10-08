import { lazy, Suspense } from "react";
import { BrowserRouter, Routes, Route, Navigate } from "react-router";
import { AuthContext, useProvideAuth, useAuth } from "./hooks/useAuth";
import Header from "./components/Header";
import Footer from "./components/Footer";
import ChatBubble from "./components/ChatBubble";
import { ChatInboxProvider } from "./components/ChatInbox";
import { ErrorBoundary } from "./components/ErrorBoundary";
import Login from "./pages/Login";
import Taskboard from "./pages/Taskboard";
const LoopEditor = lazy(() => import("./pages/LoopEditor"));
import Analytics from "./pages/Analytics";
import Chat from "./pages/Chat";
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
    <ChatInboxProvider>
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
              path="/taskboard/:workItemId"
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
                  <Suspense
                    fallback={
                      <div className="page-container">
                        <p>Loading...</p>
                      </div>
                    }
                  >
                    <LoopEditor />
                  </Suspense>
                </ProtectedRoute>
              }
            />
            <Route
              path="/loop-editor/:templateId"
              element={
                <ProtectedRoute>
                  <Suspense
                    fallback={
                      <div className="page-container">
                        <p>Loading...</p>
                      </div>
                    }
                  >
                    <LoopEditor />
                  </Suspense>
                </ProtectedRoute>
              }
            />
            <Route
              path="/analytics"
              element={
                <ProtectedRoute>
                  <Analytics />
                </ProtectedRoute>
              }
            />
            <Route
              path="/chat"
              element={
                <ProtectedRoute>
                  <Chat />
                </ProtectedRoute>
              }
            />
            <Route
              path="/chat/schedules"
              element={
                <ProtectedRoute>
                  <Chat view="schedules" />
                </ProtectedRoute>
              }
            />
            <Route
              path="/chat/:chatId"
              element={
                <ProtectedRoute>
                  <Chat />
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
              path="/settings/:section"
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
            <Route
              path="/work-item-server"
              element={<Navigate to="/settings/work-item-server" replace />}
            />
            <Route path="*" element={<Navigate to="/taskboard" replace />} />
          </Routes>
        </main>
        <Footer />
        <ChatBubble />
      </div>
    </ChatInboxProvider>
  );
}

export default function App() {
  const auth = useProvideAuth();

  return (
    <AuthContext.Provider value={auth}>
      <ErrorBoundary>
        <BrowserRouter>
          <AppRoutes />
        </BrowserRouter>
      </ErrorBoundary>
    </AuthContext.Provider>
  );
}
