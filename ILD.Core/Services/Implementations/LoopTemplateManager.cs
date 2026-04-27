using ILD.Core.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Core.Enums;
using ILD.Core.Models;
using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations;

public class LoopTemplateManager : ILoopTemplateManager
{
    private readonly ILogger<LoopTemplateManager> _logger;
    private readonly AppDbContext _dbContext;

    public LoopTemplateManager(ILogger<LoopTemplateManager> logger, AppDbContext dbContext)
    {
        _logger = logger;
        _dbContext = dbContext;
    }

    public Task<Guid> CreateLoopTemplateAsync(string name, string description, LoopTemplateGraph graph)
    {
        throw new NotImplementedException(nameof(CreateLoopTemplateAsync));
    }

    public Task<LoopTemplate?> GetLoopTemplateAsync(Guid templateId)
    {
        throw new NotImplementedException(nameof(GetLoopTemplateAsync));
    }

    public Task<LoopTemplate?> GetLatestVersionAsync(Guid templateId)
    {
        throw new NotImplementedException(nameof(GetLatestVersionAsync));
    }

    public Task<IEnumerable<LoopTemplate>> GetAllLoopTemplatesAsync()
    {
        throw new NotImplementedException(nameof(GetAllLoopTemplatesAsync));
    }

    public Task<Guid> UpdateLoopTemplateAsync(Guid templateId, string name, string description, LoopTemplateGraph graph)
    {
        throw new NotImplementedException(nameof(UpdateLoopTemplateAsync));
    }

    public Task<Guid> CloneLoopTemplateAsync(Guid sourceTemplateId, string newName)
    {
        throw new NotImplementedException(nameof(CloneLoopTemplateAsync));
    }

    public Task<LoopTemplateVersion> GetVersionAsync(Guid templateId, int version)
    {
        throw new NotImplementedException(nameof(GetVersionAsync));
    }

    public Task<IEnumerable<LoopTemplateVersion>> GetVersionsAsync(Guid templateId)
    {
        throw new NotImplementedException(nameof(GetVersionsAsync));
    }

    public Task<bool> ValidateGraphAsync(LoopTemplateGraph graph)
    {
        throw new NotImplementedException(nameof(ValidateGraphAsync));
    }

    public Task DeleteLoopTemplateAsync(Guid templateId)
    {
        throw new NotImplementedException(nameof(DeleteLoopTemplateAsync));
    }
}
