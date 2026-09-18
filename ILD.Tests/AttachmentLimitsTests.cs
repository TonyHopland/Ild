using CoreLimits = ILD.Core.Services.Attachments.AttachmentLimits;
using ServerLimits = ILD.WorkItemServer.Attachments.AttachmentLimits;

namespace ILD.Tests;

/// <summary>
/// The one attachment-size setting, read independently by the two processes that
/// enforce it. ADR-0001 keeps the WorkItem server reference-free, so the class is
/// mirrored rather than shared — which only works if both halves answer the same
/// numbers for the same environment, so every case below asserts both.
/// </summary>
public class AttachmentLimitsTests
{
    private const long Megabyte = 1024 * 1024;
    private const string PerFileVariable = "ILD_MAX_ATTACHMENT_MB";
    private const string TotalVariable = "ILD_MAX_ATTACHMENTS_TOTAL_MB";

    private static Func<string, string?> Environment(string? perFile = null, string? total = null)
        => name => name switch
        {
            PerFileVariable => perFile,
            TotalVariable => total,
            _ => null,
        };

    [Fact]
    public void An_unconfigured_deployment_allows_25_MB_per_file_and_250_MB_per_work_item()
    {
        var server = ServerLimits.FromEnvironment(Environment());
        var core = CoreLimits.FromEnvironment(Environment());

        Assert.Equal(25 * Megabyte, server.MaxBytesPerFile);
        Assert.Equal(250 * Megabyte, server.MaxTotalBytesPerWorkItem);
        Assert.Equal(10, server.MaxFilesPerRequest);

        Assert.Equal(server.MaxBytesPerFile, core.MaxBytesPerFile);
        Assert.Equal(server.MaxTotalBytesPerWorkItem, core.MaxTotalBytesPerWorkItem);
        Assert.Equal(server.MaxFilesPerRequest, core.MaxFilesPerRequest);
    }

    [Fact]
    public void The_configured_values_are_whole_megabytes_of_1024_by_1024()
    {
        var server = ServerLimits.FromEnvironment(Environment(perFile: "3", total: "7"));
        var core = CoreLimits.FromEnvironment(Environment(perFile: "3", total: "7"));

        Assert.Equal(3 * Megabyte, server.MaxBytesPerFile);
        Assert.Equal(7 * Megabyte, server.MaxTotalBytesPerWorkItem);
        Assert.Equal(server.MaxBytesPerFile, core.MaxBytesPerFile);
        Assert.Equal(server.MaxTotalBytesPerWorkItem, core.MaxTotalBytesPerWorkItem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("twenty")]
    [InlineData("25MB")]
    [InlineData("0")]
    [InlineData("-5")]
    public void A_malformed_or_non_positive_setting_falls_back_to_the_default(string configured)
    {
        var server = ServerLimits.FromEnvironment(Environment(perFile: configured, total: configured));
        var core = CoreLimits.FromEnvironment(Environment(perFile: configured, total: configured));

        Assert.Equal(25 * Megabyte, server.MaxBytesPerFile);
        Assert.Equal(250 * Megabyte, server.MaxTotalBytesPerWorkItem);
        Assert.Equal(server.MaxBytesPerFile, core.MaxBytesPerFile);
        Assert.Equal(server.MaxTotalBytesPerWorkItem, core.MaxTotalBytesPerWorkItem);
    }

    [Fact]
    public void The_request_ceiling_follows_the_per_file_maximum_rather_than_a_constant()
    {
        var small = ServerLimits.FromEnvironment(Environment(perFile: "2"));
        var large = ServerLimits.FromEnvironment(Environment(perFile: "40"));
        var core = CoreLimits.FromEnvironment(Environment(perFile: "40"));

        Assert.Equal(small.MaxBytesPerFile * small.MaxFilesPerRequest + Megabyte, small.MaxRequestBytes);
        Assert.Equal(large.MaxBytesPerFile * large.MaxFilesPerRequest + Megabyte, large.MaxRequestBytes);
        Assert.Equal(large.MaxRequestBytes, core.MaxRequestBytes);
        Assert.True(large.MaxRequestBytes > small.MaxRequestBytes);
    }
}
