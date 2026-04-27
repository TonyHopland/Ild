namespace ILD.Core.DTOs;

public record LoopTemplateGraph(
    Guid LoopTemplateVersionId,
    List<LoopNodeDto> Nodes,
    List<LoopNodeEdgeDto> Edges
);
