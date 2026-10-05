using System.Security.Claims;
using ILD.Api.Controllers;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Every chat title is written by a conditional update, which the save-time NUL
/// scrub never sees, and PostgreSQL refuses a NUL in text. So none may carry one:
/// not the first-message fallback, not a model's title, not a rename.
/// </summary>
public class ChatTitleNulTests
{
    [Theory]
    [InlineData("Fix the\0 login page", "Fix the login page")]
    [InlineData("\0\0", "New chat")]
    [InlineData("## \0", "New chat")]
    public void The_fallback_carries_no_nul(string firstMessage, string expected)
        => Assert.Equal(expected, ChatTitles.Fallback(firstMessage));

    [Fact]
    public void A_generated_title_carries_no_nul()
        => Assert.Equal("Login page fix", ChatTitles.CleanGenerated("\"Login\0 page fix\""));

    [Fact]
    public void A_generated_title_of_only_nul_is_no_title()
        => Assert.Null(ChatTitles.CleanGenerated("\0 \0"));

    private readonly Mock<IChatService> _chat = new();

    private ChatController Controller()
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "tony")], "ILD")),
        };
        return new ChatController(_chat.Object, Mock.Of<IChatTurnRunner>())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    [Fact]
    public async Task A_rename_is_stored_without_its_nul_characters()
    {
        var id = Guid.NewGuid();
        _chat.Setup(c => c.RenameAsync("tony", id, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await Controller().Rename(id, new RenameChatRequest { Name = " Deploy\0 loop " }, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        _chat.Verify(c => c.RenameAsync("tony", id, "Deploy loop", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_rename_that_is_nothing_but_nul_is_refused()
    {
        var result = await Controller().Rename(Guid.NewGuid(), new RenameChatRequest { Name = "\0 \0" }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        _chat.Verify(
            c => c.RenameAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task The_length_limit_is_judged_without_the_nul_characters()
    {
        var id = Guid.NewGuid();
        _chat.Setup(c => c.RenameAsync("tony", id, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var name = new string('n', RenameChatRequest.MaxNameLength);

        var result = await Controller().Rename(id, new RenameChatRequest { Name = name + "\0\0" }, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        _chat.Verify(c => c.RenameAsync("tony", id, name, It.IsAny<CancellationToken>()), Times.Once);
    }
}
