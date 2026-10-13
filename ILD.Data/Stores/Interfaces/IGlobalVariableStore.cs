using ILD.Data.Entities;

namespace ILD.Data.Stores.Interfaces;

/// <summary>The instance's global variables.</summary>
public interface IGlobalVariableStore
{
    /// <summary>Ordered by name.</summary>
    Task<IReadOnlyList<GlobalVariable>> GetAllAsync(CancellationToken ct = default);

    Task<GlobalVariable?> GetAsync(Guid id, CancellationToken ct = default);

    Task AddAsync(GlobalVariable variable, CancellationToken ct = default);
    Task UpdateAsync(GlobalVariable variable, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}
