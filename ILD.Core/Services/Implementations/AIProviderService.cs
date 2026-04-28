using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ILD.Core.DTOs;
using ILD.Core.Models;
using ILD.Core.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// OpenAI-compatible chat completion client + simple template renderer.
/// </summary>
public class AIProviderService : IAIProviderService
{
    private readonly AppDbContext _db;
    private readonly HttpClient _http;

    private static readonly Regex Placeholder = new(@"\{\{\s*([A-Za-z][A-Za-z0-9_.:/\\-]*)\s*\}\}", RegexOptions.Compiled);

    public AIProviderService(AppDbContext db, HttpClient http)
    {
        _db = db;
        _http = http;
    }

    public async Task<string> CompleteAsync(string prompt, string? providerId = null, CancellationToken cancellationToken = default)
    {
        var provider = await ResolveProviderAsync(providerId);
        if (provider == null) return $"[no-provider] {prompt}";

        var requestUri = provider.BaseUrl.TrimEnd('/') + "/chat/completions";
        var body = new
        {
            model = provider.Model,
            messages = new[] { new { role = "user", content = prompt } },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, requestUri) { Content = JsonContent.Create(body) };
        if (!string.IsNullOrEmpty(provider.ApiKey))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.ApiKey);
        try
        {
            using var resp = await _http.SendAsync(req, cancellationToken);
            resp.EnsureSuccessStatusCode();
            var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";
        }
        catch (Exception ex)
        {
            return $"[ai-error] {ex.Message}";
        }
    }

    public Task<string> RenderPromptAsync(string template, LoopRunContext context)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["WorkItem.Title"] = context.WorkItemTitle,
            ["WorkItem.Description"] = context.WorkItemDescription,
            ["WorkTree.Diff"] = "",
            ["EventLog.Summary"] = string.Join("\n", context.EventLogSummary ?? new()),
            ["EventLog.LastN"] = string.Join("\n", (context.EventLogSummary ?? new()).TakeLast(10)),
            ["Node.Input"] = context.PreviousNodeOutput ?? "",
            ["PreviousNode.Output"] = context.PreviousNodeOutput ?? "",
        };

        var rendered = Placeholder.Replace(template, m =>
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
        return Task.FromResult(rendered);
    }

    public Task<bool> ValidatePromptTemplateAsync(string template)
    {
        // Reuse known placeholders set; reject unknown names that don't have a colon prefix wildcard.
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "WorkItem.Title","WorkItem.Description","WorkTree.Diff",
            "EventLog.Summary","EventLog.LastN","Node.Input","PreviousNode.Output",
        };
        foreach (Match m in Placeholder.Matches(template))
        {
            var name = m.Groups[1].Value;
            if (!known.Contains(name) && !name.StartsWith("WorkTree.File:", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(false);
        }
        return Task.FromResult(true);
    }

    public async Task<IEnumerable<string>> GetAvailableProvidersAsync()
        => await _db.AiProviders.Select(p => p.Name).ToListAsync();

    public Task<IEnumerable<string>> GetAvailableToolsAsync()
        => Task.FromResult<IEnumerable<string>>(new[]
        {
            "shell.exec","file.read","file.write","git.diff","ild.create_workitem"
        });

    public async Task<ToolExecutionResult> ExecuteToolAsync(string toolName, string arguments, string worktreePath)
    {
        try
        {
            switch (toolName)
            {
                case "shell.exec":
                    return await RunShellAsync(arguments, worktreePath);
                case "file.read":
                {
                    var safe = SafePath(worktreePath, arguments);
                    return safe == null
                        ? new ToolExecutionResult(false, "", "path traversal", -1)
                        : new ToolExecutionResult(true, await File.ReadAllTextAsync(safe), null);
                }
                case "file.write":
                {
                    var doc = JsonDocument.Parse(arguments);
                    var path = doc.RootElement.GetProperty("path").GetString() ?? "";
                    var content = doc.RootElement.GetProperty("content").GetString() ?? "";
                    var safe = SafePath(worktreePath, path);
                    if (safe == null) return new ToolExecutionResult(false, "", "path traversal", -1);
                    Directory.CreateDirectory(Path.GetDirectoryName(safe)!);
                    await File.WriteAllTextAsync(safe, content);
                    return new ToolExecutionResult(true, "ok", null);
                }
                case "git.diff":
                    return await RunShellAsync("git diff HEAD", worktreePath);
                default:
                    return new ToolExecutionResult(false, "", $"unknown tool {toolName}", -1);
            }
        }
        catch (Exception ex) { return new ToolExecutionResult(false, "", ex.Message, -1); }
    }

    private async Task<AiProvider?> ResolveProviderAsync(string? providerId)
    {
        if (Guid.TryParse(providerId, out var id))
            return await _db.AiProviders.FindAsync(id);
        if (!string.IsNullOrEmpty(providerId))
            return await _db.AiProviders.FirstOrDefaultAsync(p => p.Name == providerId);
        return await _db.AiProviders.FirstOrDefaultAsync(p => p.IsDefault)
            ?? await _db.AiProviders.FirstOrDefaultAsync();
    }

    private static string? SafePath(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var rootFull = Path.GetFullPath(root);
        return full.StartsWith(rootFull, StringComparison.Ordinal) ? full : null;
    }

    private static async Task<ToolExecutionResult> RunShellAsync(string command, string cwd)
    {
        var psi = new ProcessStartInfo("/bin/sh")
        {
            WorkingDirectory = Directory.Exists(cwd) ? cwd : Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);
        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return new ToolExecutionResult(proc.ExitCode == 0, stdout, proc.ExitCode == 0 ? null : stderr, proc.ExitCode);
    }
}
