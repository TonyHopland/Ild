import { useState, useEffect } from "react";
import { AiProvider } from "../types";
import { aiProviderService, agentAdapterService } from "../services/auth";

export default function AiProviders() {
  const [providers, setProviders] = useState<AiProvider[]>([]);
  const [providerTypes, setProviderTypes] = useState<string[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [showModal, setShowModal] = useState(false);
  const [editingProvider, setEditingProvider] = useState<AiProvider | null>(null);
  const [name, setName] = useState("");
  const [type, setType] = useState("");
  const [baseUrl, setBaseUrl] = useState("");
  const [apiKey, setApiKey] = useState("");
  const [model, setModel] = useState("");
  const [isDefault, setIsDefault] = useState(false);

  useEffect(() => {
    void loadData();
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

  const openEdit = (provider: AiProvider) => {
    setEditingProvider(provider);
    setName(provider.name);
    setType(provider.type);
    setBaseUrl(provider.baseUrl);
    setModel(provider.model);
    setIsDefault(provider.isDefault);
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
  };

  const handleSave = async () => {
    const data: Partial<AiProvider> = {
      name,
      type,
      baseUrl,
      model,
      isDefault,
    };

    if (editingProvider) {
      if (apiKey) {
        data.apiKey = apiKey;
      }
      await aiProviderService.update(editingProvider.id, data);
    } else {
      data.apiKey = apiKey;
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

      <div className="ap-list">
        {providers.map((provider) => (
          <div key={provider.id} className="ap-card">
            <div className="ap-card-header">
              <div className="ap-name">
                {provider.name}
                {provider.isDefault && <span className="ap-default-badge">Default</span>}
              </div>
              <div className="ap-actions">
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
              <div className="ap-field">
                <span className="ap-label">URL</span>
                <span className="ap-value">{provider.baseUrl}</span>
              </div>
              <div className="ap-field">
                <span className="ap-label">Model</span>
                <span className="ap-value">{provider.model}</span>
              </div>
              <div className="ap-field">
                <span className="ap-label">API Key</span>
                <span className="ap-value ap-masked">
                  {provider.apiKey ? "••••••••" : "(not set)"}
                </span>
              </div>
            </div>
          </div>
        ))}
      </div>

      {showModal && (
        <div
          className="modal-overlay"
          onClick={() => {
            setShowModal(false);
            setEditingProvider(null);
          }}
        >
          <div className="modal-content" onClick={(e) => e.stopPropagation()}>
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
              <div className="form-group form-checkbox">
                <label>
                  <input
                    type="checkbox"
                    checked={isDefault}
                    onChange={(e) => setIsDefault(e.target.checked)}
                  />
                  Set as default provider
                </label>
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
      `}</style>
    </div>
  );
}
