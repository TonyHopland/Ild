import { useState, useEffect } from "react";
import { AiProvider, ManagedAgentStatus, ApiError } from "../../types";
import { aiProviderService, agentAdapterService, managedAgentService } from "../../services/auth";
import ProviderTerminal from "../../components/ProviderTerminal";

export default function AiProviders() {
  const [providers, setProviders] = useState<AiProvider[]>([]);
  const [providerTypes, setProviderTypes] = useState<string[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [showModal, setShowModal] = useState(false);
  const [editingProvider, setEditingProvider] = useState<AiProvider | null>(null);
  const [terminalProvider, setTerminalProvider] = useState<AiProvider | null>(null);
  const [name, setName] = useState("");
  const [type, setType] = useState("");
  const [baseUrl, setBaseUrl] = useState("");
  const [apiKey, setApiKey] = useState("");
  const [model, setModel] = useState("");
  const [isDefault, setIsDefault] = useState(false);
  const [parallelism, setParallelism] = useState<number>(0);
  const [agents, setAgents] = useState<ManagedAgentStatus[]>([]);
  const [updatingAgent, setUpdatingAgent] = useState<string | null>(null);
  const [agentErrors, setAgentErrors] = useState<Record<string, string>>({});

  useEffect(() => {
    void loadData();
    void loadAgents();
  }, []);

  const loadData = async () => {
    try {
      const [providersResult, typesResult] = await Promise.all([
        aiProviderService.getAll(),
        agentAdapterService.getSupportedProviderTypes(),
      ]);
      setProviders(providersResult);
      setProviderTypes(typesResult);
    } catch (error) {
      console.error("Failed to load AI providers:", error);
    } finally {
      setIsLoading(false);
    }
  };

  const loadAgents = async () => {
    try {
      const result = await managedAgentService.getAll();
      setAgents(result ?? []);
    } catch (error) {
      console.error("Failed to load coding agents:", error);
    }
  };

  const handleUpdateAgent = async (agent: ManagedAgentStatus) => {
    setUpdatingAgent(agent.key);
    setAgentErrors((prev) => {
      const next = { ...prev };
      delete next[agent.key];
      return next;
    });
    try {
      const updated = await managedAgentService.update(agent.key);
      setAgents((prev) => prev.map((a) => (a.key === updated.key ? updated : a)));
    } catch (error) {
      const message = (error as ApiError)?.message ?? "Update failed.";
      setAgentErrors((prev) => ({ ...prev, [agent.key]: message }));
    } finally {
      setUpdatingAgent(null);
    }
  };

  const openEdit = (provider: AiProvider) => {
    setEditingProvider(provider);
    setName(provider.name);
    setType(provider.type);
    setBaseUrl(provider.baseUrl);
    setModel(provider.model);
    setIsDefault(provider.isDefault);
    setParallelism(provider.parallelism ?? 0);
    setShowModal(true);
  };

  const openCreate = () => {
    setShowModal(true);
    setEditingProvider(null);
    setName("");
    setType("");
    setBaseUrl("");
    setApiKey("");
    setModel("");
    setIsDefault(false);
    setParallelism(0);
  };

  // Provider types whose auth is handled by the CLI itself (e.g. claude-code
  // logs in via the `/login` slash command inside its TUI and stores creds in
  // ~/.claude), so the BaseUrl / API key / model fields are not applicable.
  const isCliAuthProvider = (t: string) => t === "claude-code";

  const handleSetDefault = async (provider: AiProvider) => {
    await aiProviderService.setDefault(provider.id);
    await loadData();
  };

  const handleSave = async () => {
    const cliAuth = isCliAuthProvider(type);
    const data: Partial<AiProvider> = {
      name,
      type,
      baseUrl: cliAuth ? "" : baseUrl,
      model: cliAuth ? "" : model,
      isDefault,
      parallelism,
    };

    if (editingProvider) {
      if (!cliAuth && apiKey) {
        data.apiKey = apiKey;
      }
      await aiProviderService.update(editingProvider.id, data);
    } else {
      data.apiKey = cliAuth ? "" : apiKey;
      await aiProviderService.create(data);
    }

    await loadData();
    setShowModal(false);
    setEditingProvider(null);
  };

  if (isLoading) {
    return (
      <div className="page-container">
        <p>Loading...</p>
      </div>
    );
  }

  return (
    <div className="page-container">
      <div className="ap-header">
        <h1 className="page-title">AI Providers</h1>
        <button className="btn btn-primary" onClick={openCreate}>
          + New Provider
        </button>
      </div>

      {agents.length > 0 && (
        <div className="ap-agents">
          <h2 className="ap-section-title">Coding agents</h2>
          <p className="ap-section-note">
            Pi, OpenCode and Claude Code are installed onto the persistent <code>/data</code> volume
            and updated on demand — no container rebuild needed. After updating, you are responsible
            for verifying the new version works.
          </p>
          <div className="ap-agent-list">
            {agents.map((agent) => {
              const updating = updatingAgent === agent.key;
              const notInstalled = agent.installedVersion == null;
              const label = updating
                ? notInstalled
                  ? "Installing…"
                  : "Updating…"
                : agent.updateAvailable
                  ? notInstalled
                    ? `Install ${agent.latestVersion}`
                    : `Update ${agent.installedVersion} → ${agent.latestVersion}`
                  : notInstalled
                    ? "Unavailable"
                    : "Up to date";
              const title = agent.updateAvailable
                ? notInstalled
                  ? `Install ${agent.displayName} ${agent.latestVersion} onto /data`
                  : `Update ${agent.displayName} to ${agent.latestVersion} on /data`
                : notInstalled
                  ? "Latest version unavailable — check registry connectivity"
                  : "Already up to date";
              return (
                <div key={agent.key} className="ap-agent-card">
                  <div className="ap-agent-info">
                    <span className="ap-agent-name">{agent.displayName}</span>
                    <span className="ap-agent-versions">
                      <span className="ap-label">Installed</span>
                      <span className="ap-value">{agent.installedVersion ?? "not installed"}</span>
                      <span className="ap-label">Latest</span>
                      <span className="ap-value">{agent.latestVersion ?? "unknown"}</span>
                    </span>
                    {agent.error && <span className="ap-agent-warn">{agent.error}</span>}
                    {agentErrors[agent.key] && (
                      <span className="ap-agent-error">{agentErrors[agent.key]}</span>
                    )}
                  </div>
                  <button
                    className="btn btn-primary btn-small"
                    disabled={!agent.updateAvailable || updating}
                    onClick={() => handleUpdateAgent(agent)}
                    title={title}
                  >
                    {label}
                  </button>
                </div>
              );
            })}
          </div>
        </div>
      )}

      <div className="ap-list">
        {providers.map((provider) => (
          <div key={provider.id} className="ap-card">
            <div className="ap-card-header">
              <div className="ap-name">
                {provider.name}
                {provider.isDefault && <span className="ap-default-badge">Default</span>}
              </div>
              <div className="ap-actions">
                {!provider.isDefault && (
                  <button
                    className="btn btn-secondary btn-small"
                    onClick={() => handleSetDefault(provider)}
                    title="Promote this provider so AI nodes without an explicit provider use it"
                  >
                    Set as default
                  </button>
                )}
                <button
                  className="btn btn-secondary btn-small"
                  onClick={() => setTerminalProvider(provider)}
                  title="Open an interactive terminal session with this provider"
                >
                  Open terminal
                </button>
                <button className="btn btn-secondary btn-small" onClick={() => openEdit(provider)}>
                  Edit
                </button>
              </div>
            </div>
            <div className="ap-card-body">
              <div className="ap-field">
                <span className="ap-label">Type</span>
                <span className="ap-value">{provider.type}</span>
              </div>
              {provider.baseUrl && (
                <div className="ap-field">
                  <span className="ap-label">URL</span>
                  <span className="ap-value">{provider.baseUrl}</span>
                </div>
              )}
              {provider.model && (
                <div className="ap-field">
                  <span className="ap-label">Model</span>
                  <span className="ap-value">{provider.model}</span>
                </div>
              )}
              {!isCliAuthProvider(provider.type) && (
                <div className="ap-field">
                  <span className="ap-label">API Key</span>
                  <span className="ap-value ap-masked">
                    {provider.apiKey ? "••••••••" : "(not set)"}
                  </span>
                </div>
              )}
            </div>
          </div>
        ))}
      </div>

      {terminalProvider && (
        <ProviderTerminal
          providerId={terminalProvider.id}
          providerName={terminalProvider.name}
          onClose={() => setTerminalProvider(null)}
        />
      )}

      {showModal && (
        <div
          className="modal-overlay"
          onMouseDown={() => {
            setShowModal(false);
            setEditingProvider(null);
          }}
        >
          <div className="modal-content" onMouseDown={(e) => e.stopPropagation()}>
            <div className="modal-header">
              <h2>{editingProvider ? "Edit Provider" : "New Provider"}</h2>
              <button
                className="btn-close"
                onClick={() => {
                  setShowModal(false);
                  setEditingProvider(null);
                }}
              >
                &times;
              </button>
            </div>
            <div className="modal-body">
              <div className="form-group">
                <label htmlFor="apName">Name</label>
                <input
                  id="apName"
                  type="text"
                  value={name}
                  onChange={(e) => setName(e.target.value)}
                  required
                />
              </div>
              <div className="form-group">
                <label htmlFor="apType">Type</label>
                <select id="apType" value={type} onChange={(e) => setType(e.target.value)} required>
                  <option value="">Select type...</option>
                  {providerTypes.map((t) => (
                    <option key={t} value={t}>
                      {t}
                    </option>
                  ))}
                </select>
              </div>
              {isCliAuthProvider(type) ? (
                <div className="ap-cli-note">
                  This provider type authenticates via the locally-installed Claude Code CLI. The
                  first time you set it up, save the provider and then use{" "}
                  <strong>Open terminal</strong> to launch the Claude Code TUI. Inside it, run the{" "}
                  <code>/login</code> slash command to sign in (use a Max subscription). Until you
                  complete that one-time login, this provider will not work. Base URL, API key and
                  model are configured by the CLI itself and are not needed here.
                </div>
              ) : (
                <>
                  <div className="form-group">
                    <label htmlFor="apBaseUrl">Base URL</label>
                    <input
                      id="apBaseUrl"
                      type="text"
                      value={baseUrl}
                      onChange={(e) => setBaseUrl(e.target.value)}
                      required
                    />
                  </div>
                  <div className="form-group">
                    <label htmlFor="apModel">Model</label>
                    <input
                      id="apModel"
                      type="text"
                      value={model}
                      onChange={(e) => setModel(e.target.value)}
                      required
                    />
                  </div>
                  <div className="form-group">
                    <label htmlFor="apApiKey">API Key</label>
                    <input
                      id="apApiKey"
                      type="password"
                      value={apiKey}
                      onChange={(e) => setApiKey(e.target.value)}
                    />
                  </div>
                </>
              )}
              <div className="form-group">
                <label htmlFor="apParallelism">Parallelism (0 = unlimited)</label>
                <input
                  id="apParallelism"
                  type="number"
                  min={0}
                  max={1000}
                  value={parallelism}
                  onChange={(e) => setParallelism(parseInt(e.target.value, 10) || 0)}
                />
              </div>
              <div className="form-group form-checkbox">
                <label>
                  <input
                    type="checkbox"
                    checked={isDefault}
                    onChange={(e) => setIsDefault(e.target.checked)}
                  />
                  Set as default provider
                </label>
                <div className="ap-default-hint">
                  Only one provider can be the default at a time. Promoting this one will demote any
                  other default.
                </div>
              </div>
            </div>
            <div className="modal-footer">
              <button
                className="btn btn-secondary"
                onClick={() => {
                  setShowModal(false);
                  setEditingProvider(null);
                }}
              >
                Cancel
              </button>
              <button className="btn btn-primary" onClick={handleSave}>
                {editingProvider ? "Update" : "Create"}
              </button>
            </div>
          </div>
        </div>
      )}

      <style>{`
        .ap-header {
          display: flex;
          justify-content: space-between;
          align-items: center;
          margin-bottom: 1rem;
        }

        .ap-list {
          display: flex;
          flex-direction: column;
          gap: 0.75rem;
        }

        .ap-agents {
          margin-bottom: 1.5rem;
        }

        .ap-section-title {
          font-size: 0.9rem;
          font-weight: 600;
          color: #e0e0e0;
          margin: 0 0 0.25rem;
        }

        .ap-section-note {
          font-size: 0.75rem;
          color: #707090;
          margin: 0 0 0.75rem;
          line-height: 1.4;
        }

        .ap-section-note code {
          background-color: #1e1e30;
          padding: 0 0.25rem;
          border-radius: 0.25rem;
        }

        .ap-agent-list {
          display: flex;
          flex-direction: column;
          gap: 0.5rem;
        }

        .ap-agent-card {
          display: flex;
          justify-content: space-between;
          align-items: center;
          gap: 1rem;
          background-color: #1e1e30;
          border-radius: 0.5rem;
          border: 1px solid #2d2d44;
          padding: 0.75rem 1rem;
        }

        .ap-agent-info {
          display: flex;
          flex-direction: column;
          gap: 0.25rem;
        }

        .ap-agent-name {
          font-size: 0.85rem;
          font-weight: 600;
          color: #e0e0e0;
        }

        .ap-agent-versions {
          display: flex;
          align-items: baseline;
          gap: 0.5rem;
          font-size: 0.8rem;
        }

        .ap-agent-warn {
          font-size: 0.7rem;
          color: #d9a441;
        }

        .ap-agent-error {
          font-size: 0.7rem;
          color: #ef4444;
        }

        .ap-card {
          background-color: #1e1e30;
          border-radius: 0.5rem;
          border: 1px solid #2d2d44;
          overflow: hidden;
        }

        .ap-card-header {
          display: flex;
          justify-content: space-between;
          align-items: center;
          padding: 0.75rem 1rem;
          border-bottom: 1px solid #2d2d44;
        }

        .ap-name {
          font-size: 0.9rem;
          font-weight: 600;
          color: #e0e0e0;
          display: flex;
          align-items: center;
          gap: 0.5rem;
        }

        .ap-default-badge {
          font-size: 0.65rem;
          font-weight: 600;
          padding: 0.125rem 0.375rem;
          border-radius: 0.25rem;
          background-color: #1a3a2a;
          color: #22c55e;
        }

        .ap-actions {
          display: flex;
          gap: 0.5rem;
          align-items: center;
        }

        .ap-card-body {
          padding: 0.75rem 1rem;
          display: flex;
          flex-direction: column;
          gap: 0.5rem;
        }

        .ap-field {
          display: flex;
          gap: 0.5rem;
          font-size: 0.8rem;
        }

        .ap-label {
          color: #707090;
          min-width: 8rem;
        }

        .ap-value {
          color: #c0c0d0;
        }

        .ap-masked {
          letter-spacing: 0.125rem;
          user-select: none;
        }

        .btn {
          padding: 0.5rem 1rem;
          border: none;
          border-radius: 0.375rem;
          cursor: pointer;
          font-size: 0.875rem;
        }

        .btn-primary {
          background-color: #6366f1;
          color: #fff;
        }

        .btn-secondary {
          background-color: #2d2d44;
          color: #a0a0b0;
        }

        .btn-small {
          padding: 0.25rem 0.5rem;
          font-size: 0.75rem;
        }

        .btn:disabled {
          opacity: 0.5;
          cursor: not-allowed;
        }

        .modal-overlay {
          position: fixed;
          top: 0;
          left: 0;
          right: 0;
          bottom: 0;
          background-color: rgba(0, 0, 0, 0.6);
          display: flex;
          align-items: center;
          justify-content: center;
          z-index: 1000;
        }

        .modal-content {
          background-color: #1e1e30;
          border-radius: 0.5rem;
          border: 1px solid #2d2d44;
          width: 100%;
          max-width: 480px;
          padding: 1rem;
        }

        .modal-header {
          display: flex;
          justify-content: space-between;
          align-items: center;
          margin-bottom: 1rem;
        }

        .modal-header h2 {
          font-size: 1rem;
          color: #e0e0e0;
          margin: 0;
        }

        .btn-close {
          background: none;
          border: none;
          color: #a0a0b0;
          font-size: 1.25rem;
          cursor: pointer;
        }

        .modal-body {
          display: flex;
          flex-direction: column;
          gap: 0.75rem;
          margin-bottom: 1rem;
        }

        .modal-body .form-group {
          display: flex;
          flex-direction: column;
          gap: 0.25rem;
        }

        .modal-body label {
          font-size: 0.75rem;
          color: #a0a0b0;
        }

        .modal-body input,
        .modal-body select {
          padding: 0.5rem;
          background-color: #2a2a40;
          border: 1px solid #3a3a5c;
          border-radius: 0.375rem;
          color: #e0e0e0;
          font-size: 0.875rem;
        }

        .form-checkbox label {
          flex-direction: row;
          align-items: center;
          gap: 0.5rem;
          cursor: pointer;
        }

        .modal-footer {
          display: flex;
          justify-content: flex-end;
          gap: 0.5rem;
        }

        .ap-cli-note {
          font-size: 0.8rem;
          color: #a0a0b0;
          background-color: #2a2a40;
          border: 1px solid #3a3a5c;
          padding: 0.5rem 0.75rem;
          border-radius: 0.375rem;
          line-height: 1.4;
        }

        .ap-cli-note code {
          background-color: #1e1e30;
          padding: 0 0.25rem;
          border-radius: 0.25rem;
        }

        .ap-default-hint {
          font-size: 0.7rem;
          color: #707090;
          margin-top: 0.25rem;
          line-height: 1.4;
        }
      `}</style>
    </div>
  );
}
