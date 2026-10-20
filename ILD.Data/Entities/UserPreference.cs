using System.ComponentModel.DataAnnotations;

namespace ILD.Data.Entities;

/// <summary>
/// One setting a user has chosen for how the UI looks to them, such as which
/// taskboard columns are folded. It follows the user to every device they sign
/// in on. The server keeps the value as text and reads nothing into it.
/// </summary>
public class UserPreference : IHasUpdatedAt
{
    [Key]
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public User? User { get; set; }

    [Required]
    [MaxLength(64)]
    public string Key { get; set; } = string.Empty;

    [Required]
    [MaxLength(4096)]
    public string Value { get; set; } = string.Empty;

    public DateTime? UpdatedAt { get; set; }
}
