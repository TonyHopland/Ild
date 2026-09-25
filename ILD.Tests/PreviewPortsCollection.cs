using Xunit;

namespace ILD.Tests;

/// <summary>
/// Preview-service tests that start services on loopback ports. A port is picked
/// by finding one free and binding it afterwards, so two of these starting at
/// once could be handed the same port; they run one at a time among themselves,
/// in parallel with the rest of the suite.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PreviewPortsCollection
{
    public const string Name = "PreviewPorts";
}
