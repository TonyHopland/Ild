interface ErrorBannerProps {
  message: string;
  onDismiss: () => void;
}

export default function ErrorBanner({ message, onDismiss }: ErrorBannerProps) {
  if (!message) return null;
  return (
    <div
      role="alert"
      style={{
        backgroundColor: "#7f1d1d",
        color: "#fee2e2",
        padding: "0.75rem 1rem",
        borderRadius: "0.375rem",
        margin: "0.5rem 0",
        display: "flex",
        alignItems: "center",
        justifyContent: "space-between",
        gap: "1rem",
      }}
    >
      <span>{message}</span>
      <button
        type="button"
        aria-label="Dismiss error"
        onClick={onDismiss}
        style={{
          background: "transparent",
          color: "inherit",
          border: "1px solid currentColor",
          borderRadius: "0.25rem",
          padding: "0.25rem 0.5rem",
          cursor: "pointer",
        }}
      >
        Dismiss
      </button>
    </div>
  );
}
