import { useState, useEffect } from "react";
import { RemoteProvider } from "../types";
import { remoteProviderService } from "../services/auth";

const REMOTE_PROVIDER_TYPES = ["Forgejo", "GitHub", "GitLab"];

export default function RemoteProviders() {
  const [providers, setProviders] = useState<RemoteProvider[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [showModal, setShowModal] = useState(false);
  const [editingProvider, setEditingProvider] = useState<RemoteProvider | null>(null);
  const [name, setName] = useState("");
  const [type, setType] = useState("");
  const [baseUrl, setBaseUrl] = useState("");
  const [apiKey, setApiKey] = useState("");
  const [webhookSecret, setWebhookSecret] = useState("");

  useEffect(() => {
    void loadData();
  }, []);

  const loadData = async () => {
    try {
      const result = await remoteProviderService.getAll();
      setProviders(result);
    } catch (error) {
      console.error("Failed to load remote providers:", error);
    } finally {
      setIsLoading(false);
    }
  };

  const openEdit = (provider: RemoteProvider) => {
    setEditingProvider(provider);
    setName(provider.name);
    setType(provider.type);
    setBaseUrl(provider.baseUrl);
    setShowModal(true);
  };

  const openCreate = () => {
    setShowModal(true);
    setEditingProvider(null);
    setName("");
    setType("");
    setBaseUrl("");
    setApiKey("");
    setWebhookSecret("");
  };

  const handleSave = async () => {
    const data: Partial<RemoteProvider> = {
      name,
      type,
      baseUrl,
    };

    if (editingProvider) {
      if (apiKey) {
        data.apiKey = apiKey;
      }
      if (webhookSecret) {
        data.webhookSecret = webhookSecret;
      }
      await remoteProviderService.update(editingProvider.id, data);
    } else {
      data.apiKey = apiKey;
      data.webhookSecret = webhookSecret;
      await remoteProviderService.create(data);
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
      <div className="rp-header">
        <h1 className="page-title">Remote Providers</h1>
        <button className="btn btn-primary" onClick={openCreate}>
          + New Provider
        </button>
      </div>

      <div className="rp-list">
        {providers.map((provider) => (
          <div key={provider.id} className="rp-card">
            <div className="rp-card-header">
              <div className="rp-name">{provider.name}</div>
              <div className="rp-actions">
                <button className="btn btn-secondary btn-small" onClick={() => openEdit(provider)}>
                  Edit
                </button>
              </div>
            </div>
            <div className="rp-card-body">
              <div className="rp-field">
                <span className="rp-label">Type</span>
                <span className="rp-value">{provider.type}</span>
              </div>
              <div className="rp-field">
                <span className="rp-label">URL</span>
                <span className="rp-value">{provider.baseUrl}</span>
              </div>
              <div className="rp-field">
                <span className="rp-label">API Key</span>
                <span className="rp-value rp-masked">
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
                <label htmlFor="rpName">Name</label>
                <input
                  id="rpName"
                  type="text"
                  value={name}
                  onChange={(e) => setName(e.target.value)}
                  required
                />
              </div>
              <div className="form-group">
                <label htmlFor="rpType">Type</label>
                <select id="rpType" value={type} onChange={(e) => setType(e.target.value)} required>
                  <option value="">Select type...</option>
                  {REMOTE_PROVIDER_TYPES.map((t) => (
                    <option key={t} value={t}>
                      {t}
                    </option>
                  ))}
                </select>
              </div>
              <div className="form-group">
                <label htmlFor="rpBaseUrl">Base URL</label>
                <input
                  id="rpBaseUrl"
                  type="text"
                  value={baseUrl}
                  onChange={(e) => setBaseUrl(e.target.value)}
                  required
                />
              </div>
              <div className="form-group">
                <label htmlFor="rpApiKey">API Key</label>
                <input
                  id="rpApiKey"
                  type="password"
                  value={apiKey}
                  onChange={(e) => setApiKey(e.target.value)}
                />
              </div>
              <div className="form-group">
                <label htmlFor="rpWebhookSecret">Webhook Secret</label>
                <input
                  id="rpWebhookSecret"
                  type="password"
                  value={webhookSecret}
                  onChange={(e) => setWebhookSecret(e.target.value)}
                />
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
        .rp-header {
          display: flex;
          justify-content: space-between;
          align-items: center;
          margin-bottom: 1rem;
        }

        .rp-list {
          display: flex;
          flex-direction: column;
          gap: 0.75rem;
        }

        .rp-card {
          background-color: #1e1e30;
          border-radius: 0.5rem;
          border: 1px solid #2d2d44;
          overflow: hidden;
        }

        .rp-card-header {
          display: flex;
          justify-content: space-between;
          align-items: center;
          padding: 0.75rem 1rem;
          border-bottom: 1px solid #2d2d44;
        }

        .rp-name {
          font-size: 0.9rem;
          font-weight: 600;
          color: #e0e0e0;
        }

        .rp-actions {
          display: flex;
          gap: 0.5rem;
          align-items: center;
        }

        .rp-card-body {
          padding: 0.75rem 1rem;
          display: flex;
          flex-direction: column;
          gap: 0.5rem;
        }

        .rp-field {
          display: flex;
          gap: 0.5rem;
          font-size: 0.8rem;
        }

        .rp-label {
          color: #707090;
          min-width: 8rem;
        }

        .rp-value {
          color: #c0c0d0;
        }

        .rp-masked {
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

        .modal-footer {
          display: flex;
          justify-content: flex-end;
          gap: 0.5rem;
        }
      `}</style>
    </div>
  );
}
