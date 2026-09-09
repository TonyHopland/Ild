using System.Security.Claims;
using ILD.Api.Controllers;
using ILD.Core.Services.Attachments;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The stop-a-turn endpoint (POST /api/v1/chat/{id}/interrupt): cancelling is
/// only ever allowed on a chat the caller owns, and is safe to call when the turn
/// has already finished.
/// </summary>
public class ChatControllerTests
{
    private readonly Mock<IChatService> _chat = new();
    private readonly Mock<IChatTurnRunner> _runner = new();

    private ChatController CreateController(string? username = "tony")
    {
        var http = new DefaultHttpContext();
        // What the authentication handler puts on the request for a signed-in
        // operator. Agents cannot reach this controller at all — the user-only
        // fallback policy stops them before MVC, covered in
        // ILD.Tests/Integration/RepositoriesIntegrationTests.cs.
        if (username is not null)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, username), new Claim(ClaimTypes.Role, "user")],
                "ILD",
                ClaimTypes.Name,
                ClaimTypes.Role));
        }

        return new ChatController(_chat.Object, _runner.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    [Fact]
    public async Task Interrupt_cancels_the_turn_of_a_chat_the_caller_owns()
    {
        var id = Guid.NewGuid();
        _chat.Setup(c => c.ExistsForUserAsync("tony", id, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await CreateController().Interrupt(id, CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
        _runner.Verify(r => r.InterruptAsync(id), Times.Once);
    }

    [Fact]
    public async Task Interrupt_of_a_chat_the_caller_does_not_own_is_NotFound_and_cancels_nothing()
    {
        var id = Guid.NewGuid();
        _chat.Setup(c => c.ExistsForUserAsync("tony", id, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CreateController().Interrupt(id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        _runner.Verify(r => r.InterruptAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Interrupt_without_a_signed_in_user_is_Unauthorized()
    {
        var result = await CreateController(username: null).Interrupt(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
        _runner.Verify(r => r.InterruptAsync(It.IsAny<Guid>()), Times.Never);
    }

    // -- attaching files to a turn --------------------------------------------

    private static FormFile File(string name, string content = "x")
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "files", name)
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png",
        };
    }

    [Fact]
    public async Task Attached_files_are_stored_before_the_turn_is_submitted()
    {
        var id = Guid.NewGuid();
        var stored = new[] { new AttachmentRef("a1", "sketch.png", "/scratch/uploads/sketch.png", "image/png", 6) };
        _chat.Setup(c => c.SaveAttachmentsAsync("tony", id, It.IsAny<IReadOnlyList<UploadedFile>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(stored);

        var form = new ChatMessageForm { Content = "look at this", Files = [File("sketch.png")] };
        var result = await CreateController().SendMessageWithAttachments(id, form, CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
        _runner.Verify(r => r.SubmitAsync(id, "look at this", null, null, stored), Times.Once);
    }

    [Fact]
    public async Task A_turn_of_files_alone_needs_no_message_text()
    {
        var id = Guid.NewGuid();
        _chat.Setup(c => c.SaveAttachmentsAsync("tony", id, It.IsAny<IReadOnlyList<UploadedFile>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AttachmentRef>());

        var result = await CreateController()
            .SendMessageWithAttachments(id, new ChatMessageForm { Files = [File("sketch.png")] }, CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
    }

    [Fact]
    public async Task A_turn_with_neither_text_nor_files_is_rejected()
    {
        var result = await CreateController()
            .SendMessageWithAttachments(Guid.NewGuid(), new ChatMessageForm(), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        _runner.Verify(r => r.SubmitAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<AttachmentRef>?>()), Times.Never);
    }

    [Fact]
    public async Task Attaching_to_a_chat_the_caller_does_not_own_is_NotFound_and_runs_nothing()
    {
        var id = Guid.NewGuid();
        _chat.Setup(c => c.SaveAttachmentsAsync("tony", id, It.IsAny<IReadOnlyList<UploadedFile>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<AttachmentRef>?)null);

        var form = new ChatMessageForm { Content = "hi", Files = [File("sketch.png")] };
        var result = await CreateController().SendMessageWithAttachments(id, form, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
        _runner.Verify(r => r.SubmitAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<AttachmentRef>?>()), Times.Never);
    }

    [Fact]
    public async Task An_upload_the_store_refuses_comes_back_as_a_message_not_a_500()
    {
        var id = Guid.NewGuid();
        _chat.Setup(c => c.SaveAttachmentsAsync("tony", id, It.IsAny<IReadOnlyList<UploadedFile>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AttachmentRejectedException("'huge.bin' is larger than the 25 MB limit."));

        var form = new ChatMessageForm { Content = "hi", Files = [File("huge.bin")] };
        var result = await CreateController().SendMessageWithAttachments(id, form, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("25 MB", bad.Value!.ToString());
    }
}
