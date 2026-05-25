using System.Net.WebSockets;
using System.Text.Json;
using ILD.Data.Entities;
using Porta.Pty;

namespace ILD.Api.Services;

/// <summary>
/// Runs an AI provider's interactive TUI in a PTY rooted at a fresh scratch
/// directory. Hands the WebSocket↔PTY transport off to
/// <see cref="PtyWebSocketBridge"/>; this class just resolves the binary
/// from the provider config and manages the scratch dir lifecycle.
/// </summary>
public sealed class InteractiveProviderSessionService
{
    private readonly ILogger<InteractiveProviderSessionService> _logger;

    public InteractiveProviderSessionService(ILogger<InteractiveProviderSessionService> logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(
        WebSocket socket,
        AiProvider provider,
        int initialCols,
        int initialRows,
        CancellationToken cancellationToken)
    {
        var binaryPath = ResolveBinaryPath(provider);
        if (string.IsNullOrWhiteSpace(binaryPath))
        {
            await PtyWebSocketBridge.SendErrorAndCloseAsync(
                socket, "Provider has no binaryPath configured.", cancellationToken);
            return;
        }

        var sessionId = Guid.NewGuid();
        var sessionRoot = Path.Combine(Path.GetTempPath(), "ild-sessions", sessionId.ToString("N"));
        Directory.CreateDirectory(sessionRoot);

        try
        {
            var options = new PtyOptions
            {
                Name = $"ild-{provider.Type}",
                Cols = Math.Clamp(initialCols, 20, 500),
                Rows = Math.Clamp(initialRows, 5, 200),
                Cwd = sessionRoot,
                App = binaryPath,
                CommandLine = Array.Empty<string>(),
                Environment = new Dictionary<string, string>(),
            };

            await PtyWebSocketBridge.RunAsync(socket, options, _logger, cancellationToken);
        }
        finally
        {
            try { Directory.Delete(sessionRoot, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Maps an AiProvider <c>Type</c> to the binary name shipped by its
    /// vendor's CLI. For most adapters the type matches the binary name
    /// (<c>opencode</c>, <c>pi</c>), but <c>claude-code</c> ships as
    /// <c>claude</c>.
    /// </summary>
    private static string DefaultBinaryForType(string? providerType)
    {
        if (string.IsNullOrWhiteSpace(providerType))
            return string.Empty;

        return providerType.ToLowerInvariant() switch
        {
            "claude-code" => "claude",
            var other => other,
        };
    }

    private static string ResolveBinaryPath(AiProvider provider)
    {
        var fallback = DefaultBinaryForType(provider.Type);
        if (string.IsNullOrEmpty(provider.Config)) return fallback;

        try
        {
            using var doc = JsonDocument.Parse(provider.Config);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("binaryPath", out var bp)
                && bp.ValueKind == JsonValueKind.String)
            {
                var value = bp.GetString();
                if (!string.IsNullOrWhiteSpace(value)) return value!;
            }
        }
        catch { }

        return fallback;
    }
}
