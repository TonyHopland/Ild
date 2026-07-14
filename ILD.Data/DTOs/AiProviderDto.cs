using System.ComponentModel.DataAnnotations;

namespace ILD.Data.DTOs;

public class AiProviderDto
{
    public string Id { get; set; } = string.Empty;

    [Required]
    [StringLength(128, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [StringLength(64, MinimumLength = 1)]
    public string Type { get; set; } = string.Empty;

    // Optional at the DTO level. Some provider types (e.g. claude-code, which
    // authenticates via the locally-installed CLI's stored credentials) don't
    // need a base URL or model. The controller enforces them for the types
    // that do require them.
    [StringLength(512)]
    public string BaseUrl { get; set; } = string.Empty;

    [StringLength(128)]
    public string Model { get; set; } = string.Empty;

    [StringLength(4096)]
    public string? ApiKey { get; set; }

    public bool IsDefault { get; set; }

    /// <summary>0 = unlimited.</summary>
    [Range(0, 1000)]
    public int Parallelism { get; set; }

    public string? Config { get; set; }

    /// <summary>
    /// The UI-managed "Custom MCP servers (JSON)" value. Non-secret, so it is the
    /// only part of <see cref="Config"/> surfaced to and accepted from the AI
    /// Providers form. The controller folds it into <see cref="Config"/> on
    /// create/update, preserving any other keys already stored there.
    /// </summary>
    public string? CustomMcpServersJson { get; set; }

    public DateTime CreatedAt { get; set; }
}
