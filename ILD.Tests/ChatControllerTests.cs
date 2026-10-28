using System.Security.Claims;
using ILD.Api.Controllers;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Stores;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
    public async Task History_marks_busy_exactly_the_chats_whose_turn_is_in_flight()
    {
        var busy = Guid.NewGuid();
        var idle = Guid.NewGuid();
        var created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var updated = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        _chat.Setup(c => c.ListForUserAsync("tony", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ChatSessionSummaryView>
            {
                new(busy, "Running", created, updated, HasUnread: true, IsFavorite: false),
                new(idle, "Quiet", created, null, HasUnread: false, IsFavorite: true),
            });
        _runner.Setup(r => r.ActiveTurnId(busy)).Returns(Guid.NewGuid());
        _runner.Setup(r => r.ActiveTurnId(idle)).Returns((Guid?)null);

        var result = await CreateController().History(CancellationToken.None);

        // As the client reads it: the JSON the endpoint answers with.
        var rows = System.Text.Json.JsonSerializer.SerializeToElement(
            Assert.IsType<OkObjectResult>(result).Value,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal(2, rows.GetArrayLength());

        var running = rows[0];
        Assert.Equal(busy, running.GetProperty("id").GetGuid());
        Assert.True(running.GetProperty("isBusy").GetBoolean());
        Assert.Equal("Running", running.GetProperty("name").GetString());
        Assert.Equal(updated, running.GetProperty("updatedAt").GetDateTime());
        Assert.True(running.GetProperty("hasUnread").GetBoolean());
        Assert.False(running.GetProperty("isFavorite").GetBoolean());

        var quiet = rows[1];
        Assert.Equal(idle, quiet.GetProperty("id").GetGuid());
        Assert.False(quiet.GetProperty("isBusy").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, quiet.GetProperty("updatedAt").ValueKind);
        Assert.False(quiet.GetProperty("hasUnread").GetBoolean());
        Assert.True(quiet.GetProperty("isFavorite").GetBoolean());
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

    private static int? StatusOf(IActionResult result)
        => Assert.IsAssignableFrom<Microsoft.AspNetCore.Mvc.Infrastructure.IStatusCodeActionResult>(result).StatusCode;

    private void VerifyNothingMarked()
        => _chat.Verify(
            c => c.MarkReadAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task MarkRead_raises_the_marker_of_a_chat_the_caller_owns(int sequence)
    {
        var id = Guid.NewGuid();
        _chat.Setup(c => c.ExistsForUserAsync("tony", id, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _chat.Setup(c => c.MarkReadAsync("tony", id, sequence, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await CreateController()
            .MarkRead(id, new MarkChatReadRequest { Sequence = sequence }, CancellationToken.None);

        Assert.Equal(StatusCodes.Status204NoContent, StatusOf(result));
        _chat.Verify(c => c.MarkReadAsync("tony", id, sequence, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MarkRead_of_a_chat_the_caller_does_not_own_or_that_does_not_exist_is_NotFound_and_marks_nothing()
    {
        var id = Guid.NewGuid();
        _chat.Setup(c => c.ExistsForUserAsync("tony", id, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CreateController()
            .MarkRead(id, new MarkChatReadRequest { Sequence = 3 }, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
        VerifyNothingMarked();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    public async Task MarkRead_without_a_usable_sequence_is_BadRequest_and_marks_nothing(int? sequence)
    {
        var id = Guid.NewGuid();
        _chat.Setup(c => c.ExistsForUserAsync("tony", id, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await CreateController()
            .MarkRead(id, new MarkChatReadRequest { Sequence = sequence }, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        VerifyNothingMarked();
    }

    [Fact]
    public async Task MarkRead_without_a_signed_in_user_is_Unauthorized()
    {
        var result = await CreateController(username: null)
            .MarkRead(Guid.NewGuid(), new MarkChatReadRequest { Sequence = 1 }, CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
        VerifyNothingMarked();
    }

    public static TheoryData<string, string> UsableNames => new()
    {
        { "  Deploy loop wiring  ", "Deploy loop wiring" },
        { new string('n', 120), new string('n', 120) },
        { "  " + new string('n', 120) + "  ", new string('n', 120) },
    };

    [Theory]
    [MemberData(nameof(UsableNames))]
    public async Task Rename_names_a_chat_the_caller_owns_with_the_trimmed_name(string sent, string stored)
    {
        var id = Guid.NewGuid();
        _chat.Setup(c => c.ExistsForUserAsync("tony", id, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _chat.Setup(c => c.RenameAsync("tony", id, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await CreateController().Rename(id, new RenameChatRequest { Name = sent }, CancellationToken.None);

        Assert.Equal(StatusCodes.Status204NoContent, StatusOf(result));
        _chat.Verify(c => c.RenameAsync("tony", id, stored, It.IsAny<CancellationToken>()), Times.Once);
    }

    public static TheoryData<string?> UnusableNames => new() { (string?)null, "", "   ", new string('n', 121) };

    [Theory]
    [MemberData(nameof(UnusableNames))]
    public async Task Rename_to_an_empty_or_over_long_name_is_BadRequest_and_renames_nothing(string? sent)
    {
        var id = Guid.NewGuid();
        _chat.Setup(c => c.ExistsForUserAsync("tony", id, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await CreateController().Rename(id, new RenameChatRequest { Name = sent }, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        _chat.Verify(
            c => c.RenameAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DeleteMany_removes_only_the_listed_chats_the_caller_owns()
    {
        using var db = new TestDb();
        var scratchRoot = Path.Combine(Path.GetTempPath(), "ild-chat-controller-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Guid Seed(string owner)
            {
                var id = Guid.NewGuid();
                var scratch = Path.Combine(scratchRoot, id.ToString("N"));
                Directory.CreateDirectory(scratch);
                using var ctx = db.Fresh();
                ctx.ChatSessions.Add(new ChatSession
                {
                    Id = id,
                    UserId = owner,
                    AiProviderId = Guid.NewGuid(),
                    ProviderType = "claude-code",
                    ToolAllowlistCsv = "ild",
                    ScratchPath = scratch,
                    CreatedAt = DateTime.UtcNow,
                });
                ctx.SaveChanges();
                return id;
            }
            var listed = new[] { Seed("tony"), Seed("tony") };
            var unlisted = Seed("tony");
            var others = Seed("alice");

            var notifier = Mock.Of<IChatNotifier>();
            var chat = new ChatService(db.Context, new ProviderStore(db.Context), Mock.Of<IAgentAdapterRegistry>(), notifier,
                new ChatOptions { ScratchRoot = scratchRoot }, new LoopRunStore(db.Context), new ChatLoopScratchpad(), new ChatCloneCoordinator(), claudeProjects: TestClaudeProjectFolders.Instance);
            var runnerServices = new ServiceCollection().AddScoped<IChatService>(_ => chat).BuildServiceProvider();
            var runner = ActivatorUtilities.CreateInstance<ChatTurnRunner>(runnerServices, notifier, NullLogger<ChatTurnRunner>.Instance);
            var http = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "tony")], "ILD", ClaimTypes.Name, ClaimTypes.Role)),
            };
            var controller = new ChatController(chat, runner) { ControllerContext = new ControllerContext { HttpContext = http } };

            var result = await controller.DeleteMany(
                new DeleteChatsRequest { Ids = [listed[0], listed[1], listed[0], others, Guid.NewGuid()] },
                TestContext.Current.CancellationToken);

            Assert.Equal(StatusCodes.Status204NoContent, StatusOf(result));
            using var after = db.Fresh();
            Assert.Equal(new[] { unlisted, others }.Order(), after.ChatSessions.AsNoTracking().Select(c => c.Id).ToList().Order());
            Assert.All(listed, id => Assert.False(Directory.Exists(Path.Combine(scratchRoot, id.ToString("N"))), "scratch directories should be removed"));
            Assert.True(Directory.Exists(Path.Combine(scratchRoot, unlisted.ToString("N"))));
            Assert.True(Directory.Exists(Path.Combine(scratchRoot, others.ToString("N"))));
        }
        finally
        {
            try { if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, true); } catch { }
        }
    }

    public static TheoryData<Guid[]?> NoIds => new() { (Guid[]?)null, Array.Empty<Guid>() };

    [Theory]
    [MemberData(nameof(NoIds))]
    public async Task DeleteMany_without_ids_is_BadRequest_and_deletes_nothing(Guid[]? ids)
    {
        var result = await CreateController().DeleteMany(new DeleteChatsRequest { Ids = ids }, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        _chat.Verify(c => c.DeleteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _runner.Verify(r => r.DeleteAsync(It.IsAny<Guid>(), It.IsAny<Func<Task>>()), Times.Never);
    }

    [Fact]
    public async Task DeleteMany_without_a_body_is_BadRequest()
    {
        var result = await CreateController().DeleteMany(null, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
    }

    [Fact]
    public async Task DeleteMany_without_a_signed_in_user_is_Unauthorized()
    {
        var result = await CreateController(username: null).DeleteMany(new DeleteChatsRequest { Ids = [Guid.NewGuid()] }, CancellationToken.None);

        Assert.Equal(StatusCodes.Status401Unauthorized, StatusOf(result));
        _chat.Verify(c => c.DeleteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
