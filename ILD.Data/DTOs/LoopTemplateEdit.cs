namespace ILD.Data.DTOs;

/// <summary>
/// One edit of an Edit List (ADR-0028): a Loop Editor edit, named by
/// <see cref="Op"/> (<c>editNodeField</c>, <c>setNodeField</c> or <c>editFile</c>)
/// and carrying that edit's own members.
/// </summary>
public sealed class LoopTemplateEdit
{
    public string? Op { get; init; }
    public string? NodeId { get; init; }
    public string? Field { get; init; }
    public string? OldString { get; init; }
    public string? NewString { get; init; }
    public string? Value { get; init; }
}
