using System.ComponentModel.DataAnnotations;

namespace ILD.Data.Entities;

/// <summary>
/// One declared TCP forward: the orchestrator answers on
/// <c>127.0.0.1:<see cref="LocalPort"/></c> and relays to
/// <see cref="Host"/>:<see cref="Port"/>.
///
/// <para>
/// <see cref="Host"/> is one concrete host name or IP literal, never a
/// <see cref="NetworkPolicyEntry"/>-style pattern: this is somewhere to connect
/// to rather than something to match, and it is re-resolved per connection so a
/// rotated address needs no edit. The forward is transport only — whether the
/// connection is allowed is still the Network Policy's answer.
/// </para>
/// </summary>
public class NetworkForwardEntry
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(253)]
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; }

    /// <summary>The loopback port this forward answers on; one forward per port.</summary>
    public int LocalPort { get; set; }

    public DateTime CreatedAt { get; set; }
}
