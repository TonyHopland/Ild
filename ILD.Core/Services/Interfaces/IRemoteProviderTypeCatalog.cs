namespace ILD.Core.Services.Interfaces;

public interface IRemoteProviderTypeCatalog
{
    IReadOnlyList<string> GetAvailableTypes();
    bool IsSupported(string providerType);
}