using ILD.Core.Services.Interfaces;
using Moq;

namespace ILD.Tests;

internal static class DeclaredTraitsMocks
{
    /// <summary>
    /// Has a Moq-made <paramref name="registry"/> answer <c>GetTraits</c> with what the
    /// real adapters declare (<see cref="DeclaredTraits"/>); Moq would otherwise
    /// answer null for the record.
    /// </summary>
    public static IAgentAdapterRegistry WithDeclaredTraits(this IAgentAdapterRegistry registry)
    {
        Mock.Get(registry)
            .Setup(r => r.GetTraits(It.IsAny<string?>()))
            .Returns((string? type) => DeclaredTraits.For(type));
        return registry;
    }
}
