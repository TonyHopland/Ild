using System.Text.RegularExpressions;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Local;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

public sealed class PromptTemplateResolver : IPromptTemplateResolver
{
    private readonly IRunHost _host;

    private static readonly TimeSpan FileReadLimit = TimeSpan.FromSeconds(30);

    private readonly ILogger<PromptTemplateResolver>? _logger;

    public PromptTemplateResolver(IRunHost? host = null, ILogger<PromptTemplateResolver>? logger = null)
    {
        _host = host ?? LocalRunHost.Shared;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadWorkTreeFilesAsync(string template, string? worktreePath, CancellationToken ct = default)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(template) || string.IsNullOrEmpty(worktreePath)) return files;
        foreach (Match m in PromptPlaceholderRegistry.Pattern.Matches(template))
        {
            var key = m.Groups[1].Value;
            if (!key.StartsWith(PromptPlaceholderRegistry.WorkTreeFilePrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rel = key.Substring(PromptPlaceholderRegistry.WorkTreeFilePrefix.Length);
            if (files.ContainsKey(rel)) continue;
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(FileReadLimit);
                var text = await _host.ReadTextFileAsync(Path.Combine(worktreePath, rel), limit.Token);
                if (text is not null) files[rel] = text;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger?.LogWarning(ex, "{{WorkTree.File:{Path}}} could not be read; it renders empty", rel);
            }
        }
        return files;
    }

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
                return context.WorkTreeFiles is not null && context.WorkTreeFiles.TryGetValue(rel, out var text) ? text : "";
            }
            if (key.StartsWith(PromptPlaceholderRegistry.VariablePrefix, StringComparison.OrdinalIgnoreCase))
            {
                // A loop variable that has not been written yet renders empty —
                // the handoff producer may run after this template is rendered.
                var name = key.Substring(PromptPlaceholderRegistry.VariablePrefix.Length);
                return context.RunVariables != null && context.RunVariables.TryGetValue(name, out var val) ? val : "";
            }
            if (PromptPlaceholderRegistry.TryGlobalName(key, out var global))
            {
                // The engine halts before a node whose globals are missing; one
                // deleted after that check renders empty, like an unset loop variable.
                return context.GlobalVariables != null && context.GlobalVariables.TryGetValue(global, out var val) ? val : "";
            }
            return m.Value;
        });
    }
}
