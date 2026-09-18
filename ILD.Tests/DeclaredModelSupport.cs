using ILD.Data.DTOs;

namespace ILD.Tests;

/// <summary>
/// What the real adapters declare about a model, for the registry doubles to
/// answer with. Types are matched ordinal-ignore-case, the way the production
/// <c>AgentAdapterRegistry</c> matches them, so a double never accepts a
/// mixed-case type the real registry would have rejected.
/// </summary>
internal static class DeclaredModelSupport
{
    private static readonly HashSet<string> RequiresModel =
        new(StringComparer.OrdinalIgnoreCase) { "opencode", "pi" };

    /// <summary>
    /// <see cref="AdapterModelSupport.Required"/> for the BYO-endpoint types,
    /// <see cref="AdapterModelSupport.Optional"/> for everything else. The
    /// doubles are only asked about types their registry already reports as
    /// supported, so there is no <see cref="AdapterModelSupport.Unsupported"/>
    /// case to mirror here.
    /// </summary>
    public static AdapterModelSupport For(string providerType)
        => RequiresModel.Contains(providerType)
            ? AdapterModelSupport.Required
            : AdapterModelSupport.Optional;
}
