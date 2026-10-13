using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

/// <summary>
/// A suggestion's text is checked as it will be stored: PostgreSQL text holds
/// no NUL character, so the save drops them, and a rationale or a length that
/// only passes with them would be stored as something the checks never saw.
/// </summary>
public class GlobalVariableProposalTextTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(ApiFactory Factory, HttpClient Agent)> StartAsync()
    {
        var factory = new ApiFactory();
        var human = await factory.CreateAuthenticatedClientAsync();
        using (var set = await human.PutAsJsonAsync("/api/v1/settings/globalVariables.maxLength", new { value = "5" }, Ct))
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = $"suggestion-text-{Guid.NewGuid():N}" };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1, CreatedAt = DateTime.UtcNow };
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = Guid.NewGuid().ToString(),
            LoopTemplateVersionId = version.Id,
            Status = LoopRunStatus.Running,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            StartedAt = DateTime.UtcNow,
        };
        db.AddRange(template, version, run);
        await db.SaveChangesAsync(Ct);

        var agent = factory.CreateClient();
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", factory.Services.GetRequiredService<ILD.Api.Configuration.AgentAuthTokenProvider>().Token);
        agent.DefaultRequestHeaders.Add("X-ILD-Run-Id", run.Id.ToString());
        return (factory, agent);
    }

    [Fact]
    public async Task A_rationale_of_only_NUL_and_spaces_is_blank()
    {
        var (factory, agent) = await StartAsync();
        await using var _ = factory;

        var resp = await agent.PostAsJsonAsync("/api/v1/agent/global-variables/proposals",
            new { name = "rules", value = "x", rationale = " \0 " }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().GlobalVariableProposals.AnyAsync(Ct));
    }

    [Fact]
    public async Task The_length_limit_counts_the_value_as_stored()
    {
        var (factory, agent) = await StartAsync();
        await using var _ = factory;

        var resp = await agent.PostAsJsonAsync("/api/v1/agent/global-variables/proposals",
            new { name = "rules", value = "ab\0\0\0cd", rationale = "\0Short.\0" }, Ct);

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        using var scope = factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().GlobalVariableProposals.SingleAsync(Ct);
        Assert.Equal("abcd", stored.Value);
        Assert.Equal("Short.", stored.Rationale);
    }
}
