namespace ILD.Data.DTOs;

/// <summary>
/// Request body for <c>POST /api/v1/looptemplates/upgrade-document</c>: the text
/// of an exported loop file, which may still be in the old
/// <c>ild-loop-template/v1</c> format.
/// </summary>
public class LoopDocumentUpgradeRequest
{
    public string Document { get; set; } = string.Empty;
}
