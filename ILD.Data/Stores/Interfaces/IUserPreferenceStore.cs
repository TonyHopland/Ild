namespace ILD.Data.Stores.Interfaces;

public interface IUserPreferenceStore
{
    /// <summary>Every preference the user has set, by key.</summary>
    Task<IReadOnlyDictionary<string, string>> GetAllAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Sets one of the user's preferences, adding it or replacing its value.</summary>
    Task UpsertAsync(Guid userId, string key, string value, CancellationToken ct = default);
}
