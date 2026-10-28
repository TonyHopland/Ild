using ILD.Core.Services.Interfaces;

namespace ILD.Tests.Fakes;

/// <summary>Renders only <c>{{Node.Input}}</c> and <c>{{WorkItem.Title}}</c>, so a test sees which template reached it.</summary>
internal sealed class FakeRendering : IPromptRenderingService
{
    public Task<string> RenderAsync(string? template, Guid runId, WorkItemView workItem, string? previousNodeOutput)
        => Task.FromResult((template ?? string.Empty)
            .Replace("{{Node.Input}}", previousNodeOutput ?? string.Empty)
            .Replace("{{WorkItem.Title}}", workItem.Title));
}
