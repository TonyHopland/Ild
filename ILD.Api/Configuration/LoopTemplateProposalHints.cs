using ILD.Core.Services.Interfaces;

namespace ILD.Api.Configuration;

/// <summary>
/// The hints every view of Loop Template Proposals re-reads on (ADR-0028): the
/// board hears the template, each chat its own proposals. Sent once the change
/// has landed, and only when a proposal changed.
/// </summary>
internal static class LoopTemplateProposalHints
{
    public static async Task SendAsync(
        IWorkItemNotifier notifier, IChatNotifier chatNotifier, Guid templateId, int changed, IEnumerable<Guid> chatSessionIds)
    {
        if (changed == 0) return;
        await notifier.LoopTemplateProposalsChangedAsync(templateId);
        foreach (var chat in chatSessionIds.Distinct())
            await chatNotifier.LoopTemplateProposalsChangedAsync(chat);
    }
}
