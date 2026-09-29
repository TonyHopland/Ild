namespace ILD.Core.Services.Implementations.PackageFeeds;

/// <summary>A selected feed as a run's package managers are handed it.</summary>
public sealed record PackageFeedCredential(string Name, AzureFeedUrl Url, string Pat)
{
    public override string ToString() => $"{Name} ({Url})";
}
