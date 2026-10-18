using ILD.Data.Entities;

namespace ILD.Data.Stores.Interfaces;

/// <summary>
/// The instance's global variables. Every write makes the variable's pending
/// suggestions stale in the same transaction, since each was made against the
/// value the write replaced (ADR-0027).
/// </summary>
public interface IGlobalVariableStore
{
    /// <summary>Ordered by name.</summary>
    Task<IReadOnlyList<GlobalVariable>> GetAllAsync(CancellationToken ct = default);

    Task<GlobalVariable?> GetAsync(Guid id, CancellationToken ct = default);

    Task<StaledProposals> AddAsync(GlobalVariable variable, CancellationToken ct);
    Task<StaledProposals> UpdateAsync(GlobalVariable variable, CancellationToken ct);

    /// <summary>Null when there was no such variable.</summary>
    Task<StaledProposals?> DeleteAsync(Guid id, CancellationToken ct);
}
