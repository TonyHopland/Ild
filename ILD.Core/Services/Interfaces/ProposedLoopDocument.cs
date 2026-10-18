using ILD.Data.DTOs;

namespace ILD.Core.Services.Interfaces;

/// <summary>
/// A proposed loop document after the checks a save makes. Valid: <see cref="Request"/>
/// is what a save of it sends and <see cref="Document"/> the document it stores,
/// outputs normalised. Otherwise <see cref="Error"/> says what is wrong with the
/// document, or <see cref="ValidationErrors"/> lists every rule its graph breaks.
/// </summary>
public sealed record ProposedLoopDocument(
    LoopTemplateCreateRequest? Request,
    string? Document,
    string? Error,
    IReadOnlyList<string> ValidationErrors);
