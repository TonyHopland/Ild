using ILD.Data.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Data.Enums;
using ILD.Data.Entities;
namespace ILD.Core.Services.Interfaces;

public interface ILoopTemplateManager
{
    Task<Guid> CreateLoopTemplateAsync(string name, string description, LoopTemplateGraph graph);
    Task<LoopTemplate?> GetLoopTemplateAsync(Guid templateId);
    Task<LoopTemplate?> GetLatestVersionAsync(Guid templateId);
    Task<IEnumerable<LoopTemplate>> GetAllLoopTemplatesAsync(int skip = 0, int take = 100);
    Task<Guid> UpdateLoopTemplateAsync(Guid templateId, string name, string description, LoopTemplateGraph graph);
    Task<Guid> CloneLoopTemplateAsync(Guid sourceTemplateId, string newName);
    Task<LoopTemplateVersion> GetVersionAsync(Guid templateId, int version);
    Task<IEnumerable<LoopTemplateVersion>> GetVersionsAsync(Guid templateId);
    Task<bool> ValidateGraphAsync(LoopTemplateGraph graph);
    Task DeleteLoopTemplateAsync(Guid templateId);
}
