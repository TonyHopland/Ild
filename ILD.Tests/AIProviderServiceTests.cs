using FluentAssertions;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Core.Services.Implementations;

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
        var svc = new AIProviderService(db.Providers, new HttpClient());

        var ctx = new LoopRunContext(Guid.NewGuid(), Guid.NewGuid(), "Title", "Desc", "/tmp/x", "feat", new List<string> { "a", "b" }, "prev");
        var rendered = await svc.RenderPromptAsync("T={{WorkItem.Title}} P={{PreviousNode.Output}}", ctx);

        rendered.Should().Be("T=Title P=prev");
    }

    [Fact]
    public async Task ValidatePromptTemplate_rejects_unknown_placeholders()
    {
        using var db = new TestDb();
        var svc = new AIProviderService(db.Providers, new HttpClient());

        (await svc.ValidatePromptTemplateAsync("ok {{WorkItem.Title}}")).Should().BeTrue();
        (await svc.ValidatePromptTemplateAsync("bad {{No.Such}}")).Should().BeFalse();
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

        var svc = new AIProviderService(db.Providers, new HttpClient(new ErrorHandler()));

        var act = async () => await svc.CompleteAsync("hello");
        await act.Should().ThrowAsync<AiProviderException>();
    }
}
