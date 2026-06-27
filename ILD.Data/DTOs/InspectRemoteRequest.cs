using System.ComponentModel.DataAnnotations;

namespace ILD.Data.DTOs;

public class InspectRemoteRequest
{
    [Required]
    [Url]
    [StringLength(2048)]
    public string CloneUrl { get; set; } = string.Empty;

    /// <summary>
    /// Optional remote provider whose stored credentials are used to reach a
    /// private remote. When unset or unresolvable the inspection is anonymous.
    /// </summary>
    public string? RemoteProviderId { get; set; }
}

/// <summary>
/// Name and default branch inferred from a remote, used to pre-populate the
/// add-repository form. Either field is null when it can't be determined; the
/// user fills/overrides them before saving.
/// </summary>
public class InspectRemoteResponse
{
    public string? Name { get; set; }
    public string? DefaultBranch { get; set; }
}
