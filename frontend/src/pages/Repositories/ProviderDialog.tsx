import { useEffect, useState } from "react";
import { RemoteProvider, RemoteProviderTypeOption } from "../../types";
import { remoteProviderService } from "../../services/auth";

function generateWebhookSecret(): string {
  const bytes = new Uint8Array(32);

  if (typeof globalThis.crypto?.getRandomValues === "function") {
    globalThis.crypto.getRandomValues(bytes);
  } else {
    for (let i = 0; i < bytes.length; i += 1) {
      bytes[i] = Math.floor(Math.random() * 256);
    }
  }

  return Array.from(bytes, (byte) => byte.toString(16).padStart(2, "0")).join("");
}

interface ProviderDialogProps {
  /** The provider being edited, or null to create one. */
  provider: RemoteProvider | null;
  onClose: () => void;
  /** Called once the provider is stored; the dialog closes itself through `onClose`. */
  onSaved: () => Promise<void>;
}

/** The form for creating or editing a remote provider. */
export default function ProviderDialog({ provider, onClose, onSaved }: ProviderDialogProps) {
  const [providerTypes, setProviderTypes] = useState<RemoteProviderTypeOption[]>([]);
  const [name, setName] = useState(provider?.name ?? "");
  const [type, setType] = useState(provider?.type ?? "");
  const [baseUrl, setBaseUrl] = useState(provider?.baseUrl ?? "");
  const [apiKey, setApiKey] = useState("");
  const [webhookSecret, setWebhookSecret] = useState(() =>
    provider ? "" : generateWebhookSecret(),
  );

  useEffect(() => {
    remoteProviderService
      .getAvailableTypes()
      .then(setProviderTypes)
      .catch((error) => console.error("Failed to load remote provider types:", error));
  }, []);

  const handleSave = async () => {
    const data: Partial<RemoteProvider> = {
      name,
      type,
      baseUrl,
    };

    if (provider) {
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
  };

  const selectableProviderTypes =
    type && !providerTypes.some((option) => option.type === type)
      ? [{ type }, ...providerTypes]
      : providerTypes;

  return (
    <div className="modal-overlay" onMouseDown={onClose}>
      <div className="modal-content" onMouseDown={(e) => e.stopPropagation()}>
        <div className="modal-header">
          <h2>{provider ? "Edit Provider" : "New Provider"}</h2>
          <button className="btn-close" onClick={onClose}>
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
        </div>
        <div className="modal-footer">
          <button className="btn btn-secondary" onClick={onClose}>
            Cancel
          </button>
          <button className="btn btn-primary" onClick={handleSave}>
            {provider ? "Update" : "Create"}
          </button>
        </div>
      </div>
    </div>
  );
}
