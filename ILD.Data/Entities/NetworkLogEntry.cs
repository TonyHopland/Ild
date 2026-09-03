using System.ComponentModel.DataAnnotations;
using ILD.Data.Enums;

namespace ILD.Data.Entities;

/// <summary>
/// One destination an agent asked the egress proxy for, recorded whatever the
/// outcome. Not linked to the provider row: a log line must outlive the
/// provider it was made under.
/// </summary>
public class NetworkLogEntry
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(253)]
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; }

    public DateTime Timestamp { get; set; }

    public NetworkDecision Decision { get; set; }

    public Guid? AiProviderId { get; set; }
}
