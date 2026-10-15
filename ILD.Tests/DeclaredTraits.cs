using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Data.DTOs;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests;

/// <summary>
/// What the real adapters declare about their provider type, looked up the way
/// production looks it up: through an <see cref="AgentAdapterRegistry"/> holding
/// the five real adapters. Registry doubles that mirror the real types answer
/// <c>GetTraits</c> with this, so a test never keeps its own copy of the traits.
/// </summary>
internal static class DeclaredTraits
{
    private static readonly AgentAdapterRegistry Registry = new(
        new ServiceCollection().BuildServiceProvider(),
        [new OpenCodeAdapter(), new PiAdapter(), new ClaudeCodeAdapter(), new CopilotAdapter(), new CodexAdapter()]);

    public static AgentAdapterTraits For(string? providerType) => Registry.GetTraits(providerType);
}
