using ILD.Api.Controllers;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Stores.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// POST /api/v1/workitems/{id}/attachments is the one leg that hands a
/// client-supplied file name to another HTTP service, where it becomes a
/// multipart part name. A name the browser is perfectly happy with — a quote is
/// legal on Linux and macOS — makes an invalid Content-Disposition header and
/// throws, so the name is sanitized at this trust boundary rather than forwarded
/// raw. See <see cref="AttachmentIntakeTests"/> for the sanitizer's own rules.
/// </summary>
public class WorkItemsControllerAttachmentTests
{
    private static (WorkItemsController Controller, Mock<IWorkItemManager> Manager) Build()
    {
        var manager = new Mock<IWorkItemManager>();
        manager.Setup(m => m.AddAttachmentAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteWorkItemAttachment("a1", "clean.png", "image/png", 1, DateTime.UtcNow));

        var controller = new WorkItemsController(
            manager.Object,
            new Mock<ILoopEngine>().Object,
            new Mock<IWorktreePreviewService>().Object,
            new Mock<IRepositoryManager>().Object,
            new Mock<ILoopRunStore>().Object,
            new Mock<IProviderStore>().Object,
            new Mock<IBranchNameOverrideService>().Object,
            NullLogger<WorkItemsController>.Instance);

        return (controller, manager);
    }

    private static FormFile File(string name, int length = 4)
    {
        var bytes = new byte[length];
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name)
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png",
        };
    }

    [Theory]
    [InlineData("my\"file\".png", "my_file_.png")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("plain.png", "plain.png")]
    public async Task The_file_name_reaching_the_work_item_server_is_sanitized(string sent, string expected)
    {
        var (controller, manager) = Build();

        var result = await controller.AddAttachment("47", File(sent), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        manager.Verify(m => m.AddAttachmentAsync(
            "47", expected, "image/png", It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Losing every retry for the item's attachment list is worth retrying, so it
    /// has to survive the hop from the WorkItem server to this API. Left to
    /// itself the client's failure path turns the server's 409 into an opaque
    /// 500, and a conflicted delete into a 404 — telling the caller an attachment
    /// that is still there has gone.
    /// </summary>
    [Fact]
    public async Task A_conflicted_upload_is_reported_as_retryable_rather_than_as_a_server_error()
    {
        var (controller, manager) = Build();
        manager.Setup(m => m.AddAttachmentAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RemoteAttachmentConflictException("47"));

        var result = await controller.AddAttachment("47", File("sketch.png"), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Contains("Retry", conflict.Value!.ToString());
    }

    [Fact]
    public async Task A_conflicted_removal_is_not_reported_as_a_missing_attachment()
    {
        var (controller, manager) = Build();
        manager.Setup(m => m.DeleteAttachmentAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RemoteAttachmentConflictException("47"));

        var result = await controller.DeleteAttachment("47", "a1", CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task A_file_at_exactly_the_per_file_limit_is_accepted()
    {
        var (controller, manager) = Build();
        // The transport ceilings sit above the per-file one precisely so this case
        // reaches the guard below rather than ASP.NET's generic body-too-large.
        Assert.True(
            ILD.Core.Services.Attachments.AttachmentIntake.MaxSingleFileRequestBytes
            > ILD.Core.Services.Attachments.AttachmentIntake.MaxBytesPerFile,
            "the request ceiling must leave room for multipart framing");

        var atLimit = new FormFile(
            Stream.Null, 0, ILD.Core.Services.Attachments.AttachmentIntake.MaxBytesPerFile, "file", "big.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png",
        };

        var result = await controller.AddAttachment("47", atLimit, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        manager.Verify(m => m.AddAttachmentAsync(
            "47", "big.png", "image/png", It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_file_over_the_per_file_limit_is_refused_by_name()
    {
        var (controller, manager) = Build();
        var tooBig = new FormFile(
            Stream.Null, 0, ILD.Core.Services.Attachments.AttachmentIntake.MaxBytesPerFile + 1, "file", "huge.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png",
        };

        var result = await controller.AddAttachment("47", tooBig, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("25 MB", bad.Value!.ToString());
        manager.Verify(m => m.AddAttachmentAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task A_request_with_no_file_is_refused()
    {
        var (controller, _) = Build();

        Assert.IsType<BadRequestObjectResult>(
            await controller.AddAttachment("47", null, CancellationToken.None));
    }
}
