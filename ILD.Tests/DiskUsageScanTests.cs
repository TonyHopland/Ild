using System.Runtime.Versioning;
using ILD.Core.Services.Implementations.Storage;
using static ILD.Tests.Integration.StorageApiTests;

namespace ILD.Tests;

[SupportedOSPlatform("linux")]
public class DiskUsageScanTests
{
    [Fact]
    public void A_measured_path_that_is_a_link_is_not_followed_even_written_with_a_trailing_slash()
    {
        using var scratch = new TempDir();
        WriteFile(Path.Combine(scratch.Path, "target", "big.bin"), 1 * MiB);
        var link = Path.Combine(scratch.Path, "link");
        Directory.CreateSymbolicLink(link, Path.Combine(scratch.Path, "target"));
        var own = new UsageTally();

        var scan = new DiskUsageScan();
        scan.Add(link + "/", own, new UsageTally());
        scan.Run(TestContext.Current.CancellationToken);

        Assert.True(own.Bytes < 64 * KiB, $"measured {own.Bytes} bytes through the link");
    }
}
