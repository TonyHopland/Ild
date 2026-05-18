using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations;

public sealed class RemoteProviderTypeCatalog : IRemoteProviderTypeCatalog
{
    private readonly IReadOnlyList<string> _availableTypes;

    public RemoteProviderTypeCatalog(IEnumerable<IRemoteGitProviderAdapter> adapters)
    {
        _availableTypes = adapters
            .Select(a => a.ProviderType)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> GetAvailableTypes() => _availableTypes;

    public bool IsSupported(string providerType)
        => _availableTypes.Contains(providerType, StringComparer.OrdinalIgnoreCase);
}