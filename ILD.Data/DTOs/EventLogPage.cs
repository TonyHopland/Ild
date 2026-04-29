using ILD.Data.Entities;

namespace ILD.Data.DTOs;

public class EventLogPage
{
    public IReadOnlyList<EventLog> Entries { get; init; } = Array.Empty<EventLog>();
    public int NextCursor { get; init; }
    public bool HasMore { get; init; }
}
