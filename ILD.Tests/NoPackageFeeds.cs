using ILD.Core.Services.Implementations.PackageFeeds;
using Moq;

namespace ILD.Tests;

/// <summary>A resolver for executor tests whose repository selects no package feed.</summary>
internal static class NoPackageFeeds
{
    public static IPackageFeedResolver Resolver { get; } = Mock.Of<IPackageFeedResolver>(r =>
        r.ResolveAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()) == Task.FromResult(ResolvedPackageFeeds.None));
}
