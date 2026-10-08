namespace ILD.Core.Services.Interfaces;

/// <summary>The chat schedule scheduler, as something that changes schedules wakes it.</summary>
public interface IChatScheduleScheduler
{
    /// <summary>Run a pass now rather than at the next interval.</summary>
    void Pulse();
}
