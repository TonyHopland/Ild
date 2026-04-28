using FluentAssertions;
using ILD.Core.DTOs;
using ILD.Core.Services.Implementations;

namespace ILD.Tests;

public class AIProviderServiceTests
{
    [Fact]
    public async Task RenderPrompt_substitutes_known_placeholders()
    {
        using var db = new TestDb();
        var svc = new AIProviderService(db.Context, new HttpClient());

        var ctx = new LoopRunContext(Guid.NewGuid(), Guid.NewGuid(), "Title", "Desc", "/tmp/x", "feat", new List<string>{"a","b"}, "prev");
        var rendered = await svc.RenderPromptAsync("T={{WorkItem.Title}} P={{PreviousNode.Output}}", ctx);

        rendered.Should().Be("T=Title P=prev");
    }

    [Fact]
    public async Task ValidatePromptTemplate_rejects_unknown_placeholders()
    {
        using var db = new TestDb();
        var svc = new AIProviderService(db.Context, new HttpClient());

        (await svc.ValidatePromptTemplateAsync("ok {{WorkItem.Title}}")).Should().BeTrue();
        (await svc.ValidatePromptTemplateAsync("bad {{No.Such}}")).Should().BeFalse();
    }
}
