using System.Net;
using static ILD.Tests.Integration.ChatScheduleTestHost;

namespace ILD.Tests.Integration;

/// <summary>
/// A schedule's text is judged as it will be stored. The save strips NUL, which
/// Postgres cannot hold, so a value is checked without it.
/// </summary>
public sealed class ChatScheduleInputTests
{
    [Fact]
    public async Task A_prompt_of_nothing_but_nul_is_refused()
    {
        await using var host = await StartAsync("2026-07-06T05:00:00Z");
        using var response = await host.PostScheduleAsync(Body(prompt: "\0\0"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await host.ListAsync());
    }

    [Fact]
    public async Task An_ai_tag_of_nothing_but_nul_means_the_default_provider()
    {
        await using var host = await StartAsync("2026-07-06T05:00:00Z");
        var id = await host.CreateAsync(Body(aiTag: "\0"));

        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await host.GetAsync(id)).GetProperty("aiTag").ValueKind);
    }
}
