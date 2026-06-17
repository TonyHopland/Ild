using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations;

public sealed class PromptTemplateResolver : IPromptTemplateResolver
{
    public string Render(string template, PromptContext context)
    {
        if (string.IsNullOrEmpty(template)) return template ?? "";

        var summary = context.EventLogSummary ?? Array.Empty<string>();
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["WorkItem.Title"] = context.WorkItemTitle,
            ["WorkItem.Description"] = context.WorkItemDescription,
            ["WorkTree.Diff"] = "",
            ["EventLog.Summary"] = string.Join("\n", summary),
            ["EventLog.LastN"] = string.Join("\n", summary.TakeLast(10)),
            ["Node.Input"] = context.PreviousNodeOutput,
            ["PreviousNode.Output"] = context.PreviousNodeOutput,
            ["Conversation.Full"] = context.ConversationFull,
            ["Conversation.AI"] = context.ConversationAI,
            ["Conversation.Human"] = context.ConversationHuman,
        };

        return PromptPlaceholderRegistry.Pattern.Replace(template, m =>
        {
            var key = m.Groups[1].Value;
            if (values.TryGetValue(key, out var v)) return v ?? "";
            if (key.StartsWith(PromptPlaceholderRegistry.WorkTreeFilePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var rel = key.Substring(PromptPlaceholderRegistry.WorkTreeFilePrefix.Length);
                var full = string.IsNullOrEmpty(context.WorktreePath) ? null : Path.Combine(context.WorktreePath, rel);
                return full != null && File.Exists(full) ? File.ReadAllText(full) : "";
            }
            if (key.StartsWith(PromptPlaceholderRegistry.VariablePrefix, StringComparison.OrdinalIgnoreCase))
            {
                // A loop variable that has not been written yet renders empty —
                // the handoff producer may run after this template is rendered.
                var name = key.Substring(PromptPlaceholderRegistry.VariablePrefix.Length);
                return context.RunVariables != null && context.RunVariables.TryGetValue(name, out var val) ? val : "";
            }
            return m.Value;
        });
    }
}
