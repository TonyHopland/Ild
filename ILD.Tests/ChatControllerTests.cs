using System.Security.Claims;
using ILD.Api.Controllers;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The stop-a-turn endpoint (POST /api/v1/chat/{id}/interrupt): cancelling is
/// only ever allowed on a chat the caller owns, and is safe to call when the turn
/// has already finished. Alongside it, the read a client uses to learn whether
/// that chat has a turn in flight (GET /api/v1/chat/{id}) — the answer a freshly
/// loaded or reconnected bubble has no other way to get — under the same
/// ownership rules.
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
    public async Task Start_passes_omitted_tools_through_as_null_so_provider_defaults_apply()
    {
        var providerId = Guid.NewGuid();

        await CreateController().Start(new StartChatRequest { AiProviderId = providerId.ToString() }, CancellationToken.None);

        _chat.Verify(c => c.StartAsync(
            "tony", providerId, It.Is<IReadOnlyList<string>?>(t => t == null), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Start_passes_an_explicit_empty_tool_list_through_as_empty()
    {
        var providerId = Guid.NewGuid();

        await CreateController().Start(
            new StartChatRequest { AiProviderId = providerId.ToString(), Tools = [] }, CancellationToken.None);

        _chat.Verify(c => c.StartAsync(
            "tony", providerId, It.Is<IReadOnlyList<string>?>(t => t != null && t.Count == 0), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Interrupt_without_a_signed_in_user_is_Unauthorized()
    {
        var result = await CreateController(username: null).Interrupt(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
        _runner.Verify(r => r.InterruptAsync(It.IsAny<Guid>()), Times.Never);
    }

    private static ChatSessionView SessionView(Guid id) => new(
        id,
        "Past chat",
        Guid.NewGuid(),
        "claude-code",
        ["ild"],
        DateTime.UtcNow,
        [new ChatMessageView(Guid.NewGuid(), "user", "hi", false, 0, DateTime.UtcNow)]);

    [Fact]
    public async Task SendMessage_answers_with_the_turn_it_started()
    {
        var id = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        _chat.Setup(c => c.ExistsForUserAsync("tony", id, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _runner.Setup(r => r.SubmitAsync(id, "hi", null, null)).ReturnsAsync(turnId);

        var result = await CreateController()
            .SendMessage(id, new ChatMessageRequest { Content = "hi" }, CancellationToken.None);

        // The sender learns its turn from the answer, so it never has to guess which
        // of two turns an event belongs to while waiting to be told.
        var accepted = Assert.IsType<AcceptedResult>(result);
        Assert.Equal(turnId, Assert.IsType<ChatSendAcceptedView>(accepted.Value).TurnId);
    }

    [Fact]
    public async Task SendMessage_to_a_chat_the_caller_does_not_own_is_NotFound_and_starts_nothing()
    {
        var id = Guid.NewGuid();
        _chat.Setup(c => c.ExistsForUserAsync("tony", id, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CreateController()
            .SendMessage(id, new ChatMessageRequest { Content = "hi" }, CancellationToken.None);

        // Naming a turn is still an answer about a chat, so ownership is settled first.
        Assert.IsType<NotFoundObjectResult>(result);
        _runner.Verify(
            r => r.SubmitAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task Get_names_the_turn_in_flight_for_a_chat_the_caller_owns()
    {
        var id = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        _chat.Setup(c => c.GetByIdAsync("tony", id, It.IsAny<CancellationToken>())).ReturnsAsync(SessionView(id));
        _runner.Setup(r => r.ActiveTurnId(id)).Returns(turnId);

        var result = await CreateController().Get(id, CancellationToken.None);

        var view = Assert.IsType<ChatSessionView>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(turnId, view.ActiveTurnId);
        // The transcript the caller came for is still there.
        Assert.Equal(id, view.Id);
        Assert.Single(view.Messages);
    }

    [Fact]
    public async Task Get_reports_no_turn_in_flight_for_an_idle_chat()
    {
        var id = Guid.NewGuid();
        _chat.Setup(c => c.GetByIdAsync("tony", id, It.IsAny<CancellationToken>())).ReturnsAsync(SessionView(id));
        _runner.Setup(r => r.ActiveTurnId(id)).Returns((Guid?)null);

        var result = await CreateController().Get(id, CancellationToken.None);

        var view = Assert.IsType<ChatSessionView>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Null(view.ActiveTurnId);
    }

    [Fact]
    public async Task Get_of_a_chat_the_caller_does_not_own_or_that_does_not_exist_is_NotFound()
    {
        // The read is scoped by user, so another user's chat and an id that was
        // never a chat come back the same way — both 404, neither an error and
        // neither an answer about someone else's turn.
        _chat.Setup(c => c.GetByIdAsync("tony", It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ChatSessionView?)null);
        var controller = CreateController();

        Assert.IsType<NotFoundResult>(await controller.Get(Guid.NewGuid(), CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.Get(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Get_without_a_signed_in_user_is_Unauthorized()
    {
        var result = await CreateController(username: null).Get(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
        _chat.Verify(c => c.GetByIdAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
