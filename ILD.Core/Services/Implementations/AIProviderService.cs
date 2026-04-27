using ILD.Core.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Core.Enums;
using ILD.Core.Models;
using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations;

public class AIProviderService : IAIProviderService
{
    private readonly ILogger<AIProviderService> _logger;
    private readonly AppDbContext _dbContext;

    public AIProviderService(ILogger<AIProviderService> logger, AppDbContext dbContext)
    {
        _logger = logger;
        _dbContext = dbContext;
    }

    public Task<string> CompleteAsync(string prompt, string? providerId = null, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException(nameof(CompleteAsync));
    }

    public Task<string> RenderPromptAsync(string template, LoopRunContext context)
    {
        throw new NotImplementedException(nameof(RenderPromptAsync));
    }

    public Task<bool> ValidatePromptTemplateAsync(string template)
    {
        throw new NotImplementedException(nameof(ValidatePromptTemplateAsync));
    }

    public Task<IEnumerable<string>> GetAvailableProvidersAsync()
    {
        throw new NotImplementedException(nameof(GetAvailableProvidersAsync));
    }

    public Task<IEnumerable<string>> GetAvailableToolsAsync()
    {
        throw new NotImplementedException(nameof(GetAvailableToolsAsync));
    }

    public Task<ToolExecutionResult> ExecuteToolAsync(string toolName, string arguments, string worktreePath)
    {
        throw new NotImplementedException(nameof(ExecuteToolAsync));
    }
}
