using System.Security.Claims;
using ILD.Api.Controllers;
using ILD.Data.Entities;
using ILD.Data.Stores;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Tests;

public sealed class PreferencesControllerTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private async Task<Guid> AddUser(string username)
    {
        var user = new User { Id = Guid.NewGuid(), Username = username, PasswordHash = "x" };
        await _db.Auth.CreateUserAsync(user);
        return user.Id;
    }

    private PreferencesController As(string? username)
        => new(new UserPreferenceStore(_db.Fresh()), _db.Auth)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = username is null
                        ? new ClaimsPrincipal(new ClaimsIdentity())
                        : new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, username)], "test")),
                },
            },
        };

    private static Task<IActionResult> Put(PreferencesController controller, string key, string? value)
        => controller.Put(key, new PreferencesController.UpdatePreferenceRequest { Value = value }, default);

    private static IReadOnlyDictionary<string, string> Stated(IActionResult result)
        => Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(Assert.IsType<OkObjectResult>(result).Value);

    [Fact]
    public async Task A_user_with_none_set_has_no_preferences()
    {
        await AddUser("user-one");

        Assert.Empty(Stated(await As("user-one").GetAll(default)));
    }

    [Fact]
    public async Task A_preference_set_is_read_back_and_setting_it_again_replaces_it()
    {
        await AddUser("user-one");

        Assert.IsType<OkObjectResult>(await Put(As("user-one"), "taskboard.folded", """{"Done":true}"""));
        Assert.IsType<OkObjectResult>(await Put(As("user-one"), "taskboard.folded", """{"Done":false}"""));

        var stated = Stated(await As("user-one").GetAll(default));
        Assert.Equal("""{"Done":false}""", Assert.Single(stated).Value);
    }

    [Fact]
    public async Task Each_user_reads_only_their_own_preferences()
    {
        await AddUser("user-one");
        await AddUser("user-two");

        await Put(As("user-one"), "taskboard.folded", "user-one's");
        await Put(As("user-two"), "taskboard.folded", "user-two's");

        Assert.Equal("user-one's", Stated(await As("user-one").GetAll(default))["taskboard.folded"]);
        Assert.Equal("user-two's", Stated(await As("user-two").GetAll(default))["taskboard.folded"]);
    }

    [Theory]
    [InlineData("Taskboard.Folded")]
    [InlineData("taskboard folded")]
    [InlineData(".hidden")]
    [InlineData("a/b")]
    public async Task A_key_outside_the_allowed_shape_is_refused_and_stores_nothing(string key)
    {
        await AddUser("user-one");

        Assert.IsType<BadRequestObjectResult>(await Put(As("user-one"), key, "v"));
        Assert.Empty(Stated(await As("user-one").GetAll(default)));
    }

    [Fact]
    public async Task A_key_longer_than_64_characters_is_refused()
    {
        await AddUser("user-one");

        Assert.IsType<BadRequestObjectResult>(await Put(As("user-one"), new string('a', 65), "v"));
        Assert.IsType<OkObjectResult>(await Put(As("user-one"), new string('a', 64), "v"));
    }

    [Fact]
    public async Task A_too_long_value_is_refused_and_an_empty_one_is_kept()
    {
        await AddUser("user-one");

        Assert.IsType<BadRequestObjectResult>(
            await Put(As("user-one"), "k", new string('x', PreferencesController.MaxValueLength + 1)));
        Assert.IsType<OkObjectResult>(await Put(As("user-one"), "k", ""));

        Assert.Equal("", Stated(await As("user-one").GetAll(default))["k"]);
    }

    [Fact]
    public async Task A_caller_who_is_not_a_known_user_is_turned_away()
    {
        Assert.IsType<UnauthorizedResult>(await As(null).GetAll(default));
        Assert.IsType<UnauthorizedResult>(await As("ghost").GetAll(default));
        Assert.IsType<UnauthorizedResult>(await Put(As("ghost"), "k", "v"));
    }

    [Fact]
    public async Task A_deleted_user_takes_their_preferences_along()
    {
        var id = await AddUser("user-one");
        await Put(As("user-one"), "k", "v");

        using (var ctx = _db.Fresh())
        {
            ctx.Users.Remove(ctx.Users.Single(u => u.Id == id));
            await ctx.SaveChangesAsync();
        }

        using var after = _db.Fresh();
        Assert.Empty(after.UserPreferences);
    }

    [Fact]
    public async Task A_user_keeps_at_most_the_cap_of_keys_and_can_still_change_the_ones_they_have()
    {
        await AddUser("user-one");
        for (var i = 0; i < PreferencesController.MaxKeysPerUser; i++)
            Assert.IsType<OkObjectResult>(await Put(As("user-one"), $"k{i}", "v"));

        Assert.IsType<BadRequestObjectResult>(await Put(As("user-one"), "one-too-many", "v"));
        Assert.IsType<OkObjectResult>(await Put(As("user-one"), "k0", "changed"));
        Assert.Equal(PreferencesController.MaxKeysPerUser, Stated(await As("user-one").GetAll(default)).Count);
    }

    [Fact]
    public async Task Delete_removes_one_of_the_callers_own_preferences_and_no_one_elses()
    {
        await AddUser("user-one");
        await AddUser("user-two");
        await Put(As("user-one"), "chat.enabled", "false");
        await Put(As("user-one"), "taskboard.folded.done", "true");
        await Put(As("user-two"), "chat.enabled", "false");

        Assert.IsType<NoContentResult>(await As("user-one").Delete("chat.enabled", default));

        Assert.Equal(["taskboard.folded.done"], Stated(await As("user-one").GetAll(default)).Keys);
        Assert.Equal("false", Stated(await As("user-two").GetAll(default))["chat.enabled"]);
    }

    [Fact]
    public async Task Deleting_a_preference_the_caller_does_not_have_is_not_found()
    {
        await AddUser("user-one");
        await AddUser("user-two");
        await Put(As("user-two"), "chat.enabled", "false");

        Assert.IsType<NotFoundResult>(await As("user-one").Delete("chat.enabled", default));
        Assert.IsType<UnauthorizedResult>(await As("ghost").Delete("chat.enabled", default));
    }
}
