namespace ILD.Data.Entities;

/// <summary>
/// Marker interface for entities whose <c>UpdatedAt</c> column should be auto-set
/// by <see cref="AppDbContext"/> on every modification.
/// </summary>
public interface IHasUpdatedAt
{
    DateTime? UpdatedAt { get; set; }
}
