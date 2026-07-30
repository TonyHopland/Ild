using ILD.Api.Controllers;
using ILD.Core.Services.Interfaces;
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

    private ChatController CreateController(string? username = "tony", bool isAgent = false)
    {
        var http = new DefaultHttpContext();
        if (username is not null) http.Items["Username"] = username;
        if (isAgent) http.Items["IsAgent"] = true;

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

    [Fact]
    public async Task Interrupt_from_an_agent_token_is_Forbidden()
    {
        var result = await CreateController(isAgent: true).Interrupt(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        _runner.Verify(r => r.InterruptAsync(It.IsAny<Guid>()), Times.Never);
    }
}
