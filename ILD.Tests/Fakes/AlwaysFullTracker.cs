using ILD.Core.Services.Interfaces;

namespace ILD.Tests.Fakes;

/// <summary>A provider concurrency tracker whose every provider is at its limit.</summary>
internal sealed class AlwaysFullTracker : IAiProviderConcurrencyTracker
{
    public bool HasCapacity(Guid providerId, int parallelism) => false;
    public bool TryEnter(Guid providerId, int parallelism) => false;
    public void Exit(Guid providerId) { }
    public int ActiveCount(Guid providerId) => int.MaxValue;
}
