using ILD.Core.Services.Implementations.Network;
using ILD.Data.Enums;
using Moq;

namespace ILD.Tests;

internal static class EgressPolicies
{
    /// <summary>A policy whose snapshot carries <paramref name="mode"/> and no list entries.</summary>
    public static IEgressPolicy Reporting(NetworkMode mode)
    {
        var policy = new Mock<IEgressPolicy>();
        policy.Setup(p => p.GetAsync(It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<EgressPolicySnapshot>(new EgressPolicySnapshot(mode, [])));
        return policy.Object;
    }
}
