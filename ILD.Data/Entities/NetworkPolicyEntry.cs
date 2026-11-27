using System.ComponentModel.DataAnnotations;
using ILD.Data.Enums;

namespace ILD.Data.Entities;

/// <summary>
/// One host pattern on the whitelist or the blacklist. A pattern is either an
/// exact host (<c>api.example.com</c>) or a leading-dot suffix
/// (<c>.example.com</c>, which covers the domain and every subdomain). A null
/// <see cref="AiProviderId"/> applies to every agent launch; a set one applies
/// only to launches of that provider. <see cref="Installers"/> makes it the installers' scope: it applies to the toolchain
/// installers (ADR-0035) alone, and global entries apply to them as well.
/// </summary>
public class NetworkPolicyEntry
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(253)]
    public string Host { get; set; } = string.Empty;

    public NetworkListKind ListKind { get; set; }

    public Guid? AiProviderId { get; set; }

    public AiProvider? AiProvider { get; set; }

    /// <summary>The entry applies to the connections of toolchain installers and to no agent launch.</summary>
    public bool Installers { get; set; }

    public DateTime CreatedAt { get; set; }
}
