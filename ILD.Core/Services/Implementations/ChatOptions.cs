namespace ILD.Core.Services.Implementations;

/// <summary>
/// Configuration for the standalone chat feature: where per-session scratch
/// directories live and how the idle-retention backstop sweeper behaves.
/// </summary>
public sealed class ChatOptions
{
    /// <summary>Root directory under which each chat session's scratch dir is created.</summary>
    public string ScratchRoot { get; init; } = Path.Combine("data", "chat-sessions");

    /// <summary>A chat session idle (no activity) longer than this is reclaimed by the sweeper.</summary>
    public TimeSpan IdleRetentionPeriod { get; init; } = TimeSpan.FromDays(14);

    /// <summary>How often the idle-retention sweeper runs.</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromHours(6);
}
