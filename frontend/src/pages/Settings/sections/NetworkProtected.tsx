import { useState } from "react";
import type { NetworkProtectedDestination } from "../../../types";
import ConfirmModal from "../../../components/ConfirmModal";
import { Switch } from "../controls";

/** What an agent could do with the destination once it is reachable, shown while its protection is off. */
export const PROTECTED_WARNINGS: Record<string, string> = {
  ild: "agents can reach ILD's own addresses through the proxy, including services that trust anything on ILD's network",
  database: "agents can reach ILD's database and try whatever credentials they hold",
  "workitem-server":
    "agents can reach the WorkItem Server directly, outside ILD's own access rules",
  "kubernetes-api": "agents can reach the Kubernetes API with any credentials they hold",
  host: "agents can reach every service listening on the machine ILD's container runs on, including the ports ILD's own stack publishes there",
  "link-local":
    "agents can reach link-local and cloud metadata addresses, which can hold the credentials of the machine ILD runs on",
  private:
    "agents can reach every private and cluster-internal address: your LAN, other pods, and storage such as an NFS export",
};

export const PROTECTED_ANCHOR = "network-protected";

interface Props {
  destinations: NetworkProtectedDestination[];
  onToggle: (id: string, enabled: boolean) => Promise<void>;
  error: string | null;
}

export function ProtectedDestinations({ destinations, onToggle, error }: Props) {
  const [switchingOff, setSwitchingOff] = useState<NetworkProtectedDestination | null>(null);
  const off = destinations.filter((d) => !d.enabled);

  return (
    <section className="settings-card" id={PROTECTED_ANCHOR}>
      <div className="settings-card-header">
        <h3 className="settings-card-title">Protected destinations</h3>
      </div>
      <p className="settings-card-note">
        ILD&apos;s own infrastructure and private networks. While a protection is on, agents cannot
        reach these through the proxy in any mode, and a whitelist entry does not override it.
        Loopback is always refused. A declared forward to one of them is still allowed, judged by
        the mode and lists, and is how runs reach a LAN or in-cluster service.
      </p>
      {error && <div className="settings-error">{error}</div>}
      {off.length > 0 && (
        <div className="net-banner" role="alert">
          <strong>Protection is off.</strong>{" "}
          {off.map((d) => PROTECTED_WARNINGS[d.id] ?? `agents can reach ${d.name}`).join("; ")}.
        </div>
      )}
      {destinations.length === 0 ? (
        <p className="settings-card-note">ILD has no protected destinations to list.</p>
      ) : (
        <table className="net-table">
          <thead>
            <tr>
              <th>Destination</th>
              <th>Addresses</th>
              <th>Protected</th>
            </tr>
          </thead>
          <tbody>
            {destinations.map((d) => (
              <tr key={d.id}>
                <td>
                  <strong>{d.name}</strong>
                  <span className="net-sub"> {d.hosts.join(", ")}</span>
                </td>
                <td className="net-sub">{[...d.addresses, ...d.ranges].join(", ") || "—"}</td>
                <td>
                  <Switch
                    checked={d.enabled}
                    label={`Protect ${d.name}`}
                    onChange={(checked) => {
                      if (checked) void onToggle(d.id, true);
                      else setSwitchingOff(d);
                    }}
                  />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <ConfirmModal
        isOpen={switchingOff !== null}
        title={`Stop protecting ${switchingOff?.name ?? ""}?`}
        message={`${PROTECTED_WARNINGS[switchingOff?.id ?? ""] ?? "agents can reach it"}. It applies to the next connection, and the mode and lists then decide.`}
        confirmText="Switch off"
        onCancel={() => setSwitchingOff(null)}
        onConfirm={() => {
          const target = switchingOff;
          setSwitchingOff(null);
          if (target) void onToggle(target.id, false);
        }}
      />
    </section>
  );
}
