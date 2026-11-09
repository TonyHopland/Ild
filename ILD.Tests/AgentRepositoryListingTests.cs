using System.Text.Json;
using ILD.Api.Controllers;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ILD.Tests;

public class AgentRepositoryListingTests
{
    [Fact]
    public async Task ListRepositories_never_returns_credentials_from_the_clone_url()
    {
        using var db = new TestDb();
        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://host" };
        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(new Repository { Id = Guid.NewGuid(), Name = "repo", CloneUrl = "https://u:secret@host/r.git", RemoteProviderId = remote.Id });
        await db.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var controller = new AgentController(
            new Mock<IWorkItemManager>().Object,
            new Mock<ILoopTemplateManager>().Object,
            db.LoopRuns,
            db.Context,
            db.Providers,
            NoPackageFeeds.Resolver,
            new Mock<IWorktreePreviewService>().Object,
            new Mock<IChatLoopScratchpad>().Object,
            new Mock<IChatNotifier>().Object,
            TestActors.Instance,
            new Mock<IWorkItemNotifier>().Object);

        var result = Assert.IsType<OkObjectResult>(await controller.ListRepositories());
        var json = JsonSerializer.Serialize(result.Value);
        Assert.DoesNotContain("secret", json);
        Assert.Contains("https://host/r.git", json);
    }
}
