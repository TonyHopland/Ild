using ILD.Core.Services.Implementations.PackageFeeds;
using Microsoft.Extensions.Logging.Abstractions;

namespace ILD.Tests;

public sealed class PackageFeedFileShareTests
{
    private static PackageFeedFileShare Share() => new(
        [PackageFeedCredentialFilesTests.Feed("company", "https://pkgs.dev.azure.com/example-org/_packaging/company", "pat-share-0001")],
        "share-test", NullLogger.Instance);

    [Fact]
    public async Task A_lease_disposed_twice_at_once_has_removed_the_file_when_either_call_returns()
    {
        // A service's exit and its stop both let go of its lease. Whichever call comes second must not
        // return while the first is still deleting the file: the stop that returns is the one that promises the file is gone.
        for (var attempt = 0; attempt < 300; attempt++)
        {
            var lease = Share().Acquire();
            var path = lease.Path!;
            using var start = new Barrier(2);

            var other = Task.Run(() => { start.SignalAndWait(TestContext.Current.CancellationToken); lease.Dispose(); }, TestContext.Current.CancellationToken);
            start.SignalAndWait(TestContext.Current.CancellationToken);
            lease.Dispose();

            Assert.False(File.Exists(path), "the file was still there when Dispose returned");
            await other;
        }
    }

    [Fact]
    public void The_file_goes_with_the_last_lease_only()
    {
        var share = Share();
        var first = share.Acquire();
        var second = share.Acquire();
        var path = first.Path!;

        first.Dispose();
        first.Dispose();
        Assert.True(File.Exists(path));

        second.Dispose();
        Assert.False(File.Exists(path));
    }
}
