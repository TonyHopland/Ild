using System.Text.Json;

namespace ILD.Core.Services.Implementations.Adapters;

/// <summary>
/// Typed view over the free-form JSON blob stored on <c>AiProvider.Config</c>.
/// Mirrors <c>NodeConfig.Parse&lt;T&gt;</c>: a single tolerant parser keeps
/// string-keyed JSON lookups out of the adapters and the interactive-session
/// service, and gives the known config keys one source of truth. Blank values
/// are normalized to <c>null</c> so callers can use <c>?? fallback</c> uniformly.
/// </summary>
public sealed record AiProviderConfig
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    private static readonly AiProviderConfig Empty = new();

    public string? BinaryPath { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? ApiKey { get; init; }
    public string? Api { get; init; }

    /// <summary>Parse a provider config blob. Returns an empty config when the JSON is null, blank, or malformed.</summary>
    public static AiProviderConfig Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;
        try
        {
            return (JsonSerializer.Deserialize<AiProviderConfig>(json, Options) ?? Empty).Normalized();
        }
        catch
        {
            return Empty;
        }
    }

    /// <summary>The configured binary path, or <paramref name="fallback"/> when none is set.</summary>
    public string BinaryPathOr(string fallback) => BinaryPath ?? fallback;

    private AiProviderConfig Normalized() => new()
    {
        BinaryPath = Blank(BinaryPath),
        Provider = Blank(Provider),
        Model = Blank(Model),
        ApiKey = Blank(ApiKey),
        Api = Blank(Api),
    };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
