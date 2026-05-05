using System.Text.RegularExpressions;
using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations;

public sealed class PromptTemplateResolver : IPromptTemplateResolver
{
    private static readonly Regex Placeholder =
        new(@"\{\{\s*([A-Za-z][A-Za-z0-9_.:/\\-]*)\s*\}\}", RegexOptions.Compiled);

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
        };

        return Placeholder.Replace(template, m =>
        {
            var key = m.Groups[1].Value;
            if (values.TryGetValue(key, out var v)) return v ?? "";
            if (key.StartsWith("WorkTree.File:", StringComparison.OrdinalIgnoreCase))
            {
                var rel = key.Substring("WorkTree.File:".Length);
                var full = string.IsNullOrEmpty(context.WorktreePath) ? null : Path.Combine(context.WorktreePath, rel);
                return full != null && File.Exists(full) ? File.ReadAllText(full) : "";
            }
            return m.Value;
        });
    }
}
