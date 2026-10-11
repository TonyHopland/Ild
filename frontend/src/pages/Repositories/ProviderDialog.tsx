import { FormEvent, useEffect, useState } from "react";
import { ApiError, RemoteProvider, RemoteProviderTypeOption } from "../../types";
import { remoteProviderService } from "../../services/auth";

function generateWebhookSecret(): string {
  const bytes = new Uint8Array(32);
  globalThis.crypto.getRandomValues(bytes);
  return Array.from(bytes, (byte) => byte.toString(16).padStart(2, "0")).join("");
}

const sameType = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();

interface ProviderDialogProps {
  provider: RemoteProvider | null;
  onClose: () => void;
  onSaved: () => Promise<void>;
}

export default function ProviderDialog({ provider, onClose, onSaved }: ProviderDialogProps) {
  const [providerTypes, setProviderTypes] = useState<RemoteProviderTypeOption[]>([]);
  const [name, setName] = useState(provider?.name ?? "");
  const [type, setType] = useState(provider?.type ?? "");
  const [baseUrl, setBaseUrl] = useState(provider?.baseUrl ?? "");
  const [apiKey, setApiKey] = useState("");
  const [webhookSecret, setWebhookSecret] = useState(() =>
    provider ? "" : generateWebhookSecret(),
  );
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    remoteProviderService
      .getAvailableTypes()
      .then(setProviderTypes)
      .catch((error) => console.error("Failed to load remote provider types:", error));
  }, []);

  // The server matches types ignoring case, so a stored `github` is the catalog's `GitHub`.
  const selectedType = providerTypes.find((option) => sameType(option.type, type))?.type ?? type;
  const selectableProviderTypes =
    selectedType && !providerTypes.some((option) => option.type === selectedType)
      ? [{ type: selectedType }, ...providerTypes]
      : providerTypes;

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    const data: Partial<RemoteProvider> = {
      name,
      type: selectedType,
      baseUrl,
    };

    setSaving(true);
    setError(null);
    try {
      if (provider) {
        // The update overwrites the default flag, so it carries the stored one.
        data.isDefault = provider.isDefault;
        if (apiKey) {
          data.apiKey = apiKey;
        }
        if (webhookSecret) {
          data.webhookSecret = webhookSecret;
        }
        await remoteProviderService.update(provider.id, data);
      } else {
        data.apiKey = apiKey;
        data.webhookSecret = webhookSecret;
        await remoteProviderService.create(data);
      }
      await onSaved();
      onClose();
    } catch (e) {
      setError((e as ApiError | Error).message);
    } finally {
      setSaving(false);
    }
  };

  const title = provider ? "Edit Provider" : "New Provider";
  return (
    <div className="modal-overlay" onMouseDown={onClose}>
      <div
        className="modal-content"
        onMouseDown={(e) => e.stopPropagation()}
        role="dialog"
        aria-modal="true"
        aria-labelledby="rpDialogTitle"
        onKeyDown={(e) => {
          if (e.key !== "Escape") return;
          e.stopPropagation();
          onClose();
        }}
      >
        <div className="modal-header">
          <h2 id="rpDialogTitle">{title}</h2>
          <button type="button" className="btn-close" aria-label="Close" onClick={onClose}>
            &times;
          </button>
        </div>
        <form onSubmit={(e) => void handleSubmit(e)}>
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
              <select
                id="rpType"
                value={selectedType}
                onChange={(e) => setType(e.target.value)}
                required
              >
                <option value="">Select type...</option>
                {selectableProviderTypes.map((option) => (
                  <option key={option.type} value={option.type}>
                    {option.type}
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
              <div className="rp-secret-input-row">
                <input
                  id="rpApiKey"
                  type="password"
                  placeholder={provider?.hasApiKey ? "(unchanged)" : ""}
                  value={apiKey}
                  onChange={(e) => setApiKey(e.target.value)}
                />
              </div>
            </div>
            <div className="form-group">
              <label htmlFor="rpWebhookSecret">Webhook Secret</label>
              <div className="rp-secret-input-row">
                <input
                  id="rpWebhookSecret"
                  type="password"
                  placeholder={provider?.webhookSecret ? "(unchanged)" : ""}
                  value={webhookSecret}
                  onChange={(e) => setWebhookSecret(e.target.value)}
                />
                <button
                  type="button"
                  className="btn btn-secondary btn-small"
                  onClick={() => setWebhookSecret(generateWebhookSecret())}
                >
                  {provider ? "Regenerate" : "Generate"}
                </button>
              </div>
            </div>
            {error && (
              <span className="repo-hint repo-hint-error" role="alert">
                {`Couldn't save the provider: ${error}`}
              </span>
            )}
          </div>
          <div className="modal-footer">
            <button type="button" className="btn btn-secondary" onClick={onClose}>
              Cancel
            </button>
            <button type="submit" className="btn btn-primary" disabled={saving}>
              {provider ? "Update" : "Create"}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
