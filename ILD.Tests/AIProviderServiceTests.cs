using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Moq;

namespace ILD.Tests;

public class AIProviderServiceTests
{
    private sealed class ErrorHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(new HttpRequestException("upstream offline"));
    }

    [Fact]
    public async Task PreviewStart_tool_injects_repo_custom_env_resolved_via_the_run()
    {
        // The agent tool surface only holds the worktree path, so the repo's custom
        // .env must be resolved back through the run that owns the worktree —
        // worktree → run → work item → repository — and threaded into StartAsync,
        // matching the human WorkItems/Agent controllers.
        using var db = new TestDb();

        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "p", Type = "Forgejo", Url = "https://e" };
        var repoId = Guid.NewGuid();
        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(new Repository
        {
            Id = repoId,
            Name = "r",
            CloneUrl = "https://e/r.git",
            RemoteProviderId = remote.Id,
            PreviewEnv = "API_TOKEN=from-repo",
        });

        var lt = new LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
        var ltv = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = lt.Id, VersionNumber = 1, CreatedAt = DateTime.UtcNow };
        db.Context.LoopTemplates.Add(lt);
        db.Context.LoopTemplateVersions.Add(ltv);

        var wiId = Guid.NewGuid().ToString();
        const string worktreePath = "/tmp/worktrees/ild/wi-x-run-y";
        db.Context.LoopRuns.Add(new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = wiId,
            LoopTemplateVersionId = ltv.Id,
            WorktreePath = worktreePath,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
        });
        await db.Context.SaveChangesAsync();

        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync(wiId))
            .ReturnsAsync(new WorkItemView { Id = wiId, RepositoryId = repoId });

        WorktreePreviewStartOptions? captured = null;
        var preview = new Mock<IWorktreePreviewService>();
        preview.Setup(p => p.StartAsync(worktreePath, It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<string, WorktreePreviewStartOptions?, CancellationToken>((_, o, _) => captured = o)
            .ReturnsAsync(new WorktreePreviewResponse { State = "running", WorktreePath = worktreePath });

        var svc = new AIProviderService(db.Providers, workItems.Object, preview.Object, new HttpClient(), null, db.LoopRuns);

        var result = await svc.ExecuteToolAsync("ild.preview_start", "{}", worktreePath);

        Assert.True(result.Success);
        Assert.NotNull(captured);
        Assert.Equal("API_TOKEN=from-repo", captured!.CustomEnv);
    }

    [Fact]
    public async Task PreviewStart_tool_without_a_matching_run_injects_no_custom_env()
    {
        // An unmatched worktree path (no run owns it) must degrade gracefully to a
        // null CustomEnv rather than throwing.
        using var db = new TestDb();

        WorktreePreviewStartOptions? captured = null;
        var preview = new Mock<IWorktreePreviewService>();
        preview.Setup(p => p.StartAsync(It.IsAny<string>(), It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<string, WorktreePreviewStartOptions?, CancellationToken>((_, o, _) => captured = o)
            .ReturnsAsync(new WorktreePreviewResponse { State = "running" });

        var svc = new AIProviderService(db.Providers, Mock.Of<IWorkItemManager>(), preview.Object, new HttpClient(), null, db.LoopRuns);

        var result = await svc.ExecuteToolAsync("ild.preview_start", "{}", "/tmp/unknown-worktree");

        Assert.True(result.Success);
        Assert.NotNull(captured);
        Assert.Null(captured!.CustomEnv);
    }
    [Fact]
    public async Task RenderPrompt_substitutes_known_placeholders()
    {
        using var db = new TestDb();
        var svc = new AIProviderService(db.Providers, Mock.Of<IWorkItemManager>(), Mock.Of<IWorktreePreviewService>(), new HttpClient());

        var ctx = new LoopRunContext(Guid.NewGuid(), Guid.NewGuid().ToString(), "Title", "Desc", "/tmp/x", "feat", new List<string> { "a", "b" }, "prev");
        var rendered = await svc.RenderPromptAsync("T={{WorkItem.Title}} P={{PreviousNode.Output}}", ctx);

        Assert.Equal("T=Title P=prev", rendered);
    }

    [Fact]
    public async Task ValidatePromptTemplate_rejects_unknown_placeholders()
    {
        using var db = new TestDb();
        var svc = new AIProviderService(db.Providers, Mock.Of<IWorkItemManager>(), Mock.Of<IWorktreePreviewService>(), new HttpClient());

        Assert.True((await svc.ValidatePromptTemplateAsync("ok {{WorkItem.Title}}")));
        Assert.False((await svc.ValidatePromptTemplateAsync("bad {{No.Such}}")));
    }

    [Fact]
    public async Task CompleteAsync_throws_AiProviderException_on_http_failure()
    {
        using var db = new TestDb();
        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "default",
            BaseUrl = "http://localhost:9",
            Model = "gpt-test",
            IsDefault = true,
            CreatedAt = DateTime.UtcNow,
        };
        await db.Providers.CreateAiProviderAsync(provider);

        var svc = new AIProviderService(db.Providers, Mock.Of<IWorkItemManager>(), Mock.Of<IWorktreePreviewService>(), new HttpClient(new ErrorHandler()));

        var act = async () => await svc.CompleteAsync("hello");
        await Assert.ThrowsAsync<AiProviderException>(act);
    }
}
