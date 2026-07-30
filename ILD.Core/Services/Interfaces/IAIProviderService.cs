using ILD.Data.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Data.Enums;
using ILD.Data.Entities;
namespace ILD.Core.Services.Interfaces;

public interface IAIProviderService
{
    Task<string> CompleteAsync(string prompt, string? providerId = null, CancellationToken cancellationToken = default);
    // Placeholder expansion is not offered here: a template field is rendered
    // exactly once, by its node executor, through IPromptRenderingService.
    // Validation stays — the loop editor checks a template before it is saved.
    Task<bool> ValidatePromptTemplateAsync(string template);
    Task<IEnumerable<string>> GetAvailableProvidersAsync();
    Task<IEnumerable<string>> GetAvailableToolsAsync();
    Task<ToolExecutionResult> ExecuteToolAsync(string toolName, string arguments, string worktreePath);
}
