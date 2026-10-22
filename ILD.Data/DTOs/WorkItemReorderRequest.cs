namespace ILD.Data.DTOs;

/// <summary>
/// A card dropped on the board, named by the cards directly above and below the
/// drop spot in its column. At least one is required; null at the top or bottom.
/// </summary>
public class WorkItemReorderRequest
{
    public string? AboveId { get; set; }

    public string? BelowId { get; set; }
}
