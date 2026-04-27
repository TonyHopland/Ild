using ILD.Core.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Core.Enums;
using ILD.Core.Models;
namespace ILD.Core.Services.Interfaces;

public interface IAIProviderService
{
    Task<string> CompleteAsync(string prompt, string? providerId = null, CancellationToken cancellationToken = default);
    Task<string> RenderPromptAsync(string template, LoopRunContext context);
    Task<bool> ValidatePromptTemplateAsync(string template);
    Task<IEnumerable<string>> GetAvailableProvidersAsync();
    Task<IEnumerable<string>> GetAvailableToolsAsync();
    Task<ToolExecutionResult> ExecuteToolAsync(string toolName, string arguments, string worktreePath);
}
