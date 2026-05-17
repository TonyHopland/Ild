using ILD.Data.DTOs;
using ILD.Data.Entities;
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
