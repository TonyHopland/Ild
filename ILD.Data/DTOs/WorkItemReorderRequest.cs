namespace ILD.Data.DTOs;

/// <summary>
/// A card dropped on the board, named by the cards directly above and below the
/// drop spot in its column. At least one is required; null at the top or bottom.
/// </summary>
public class WorkItemReorderRequest
{
    public string? AboveId { get; set; }

    public string? BelowId { get; set; }

    /// <summary>
    /// The column the card was dropped in, by status name, when it came from
    /// another one. Null keeps the item's status.
    /// </summary>
    public string? TargetStatus { get; set; }
}
