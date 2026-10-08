using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Credits one work item create to the schedule firing whose own turn asked for
/// it. The record is written before the WorkItem server is asked, so no item is
/// created without one, and it names the item once the server answers. A create
/// whose outcome is unknown leaves it unresolved: counted, never shown as an item.
/// </summary>
public sealed class ScheduledItemCredit
{
    private const int ResolveAttempts = 3;

    private readonly AppDbContext _db;
    private readonly IChatNotifier _notifier;
    private readonly ILogger _log;
    private readonly Guid _firingId;
    private readonly Guid _scheduleId;
    private readonly string _owner;
    private Guid? _recordId;
    private bool _resolved;

    private ScheduledItemCredit(AppDbContext db, IChatNotifier notifier, ILogger log, Guid firingId, Guid scheduleId, string owner)
    {
        _db = db;
        _notifier = notifier;
        _log = log;
        _firingId = firingId;
        _scheduleId = scheduleId;
        _owner = owner;
    }

    /// <summary>
    /// The credit for a create made by <paramref name="turnId"/> of the chat
    /// <paramref name="chatSessionId"/>, or null unless that turn is a running
    /// firing's own. Any other turn, even an earlier one of the same chat, is
    /// never credited.
    /// </summary>
    public static async Task<ScheduledItemCredit?> ForTurnAsync(
        AppDbContext db, IChatNotifier notifier, ILogger log, Guid chatSessionId, Guid turnId)
    {
        var firing = await db.ChatScheduleFirings.AsNoTracking()
            .Where(f => f.ChatSessionId == chatSessionId
                && f.TurnId == turnId
                && f.Outcome == ChatScheduleFiringOutcome.Running)
            .Select(f => new { f.Id, f.ChatScheduleId, f.ChatSchedule!.UserId })
            .FirstOrDefaultAsync();
        return firing is null
            ? null
            : new ScheduledItemCredit(db, notifier, log, firing.Id, firing.ChatScheduleId, firing.UserId);
    }

    /// <summary>Right before the server is asked. If this throws, the create must not go ahead.</summary>
    public async Task OpenAsync()
    {
        var record = new ChatScheduleFiringWorkItem
        {
            Id = Guid.NewGuid(),
            ChatScheduleFiringId = _firingId,
            CreatedAt = DateTime.UtcNow,
        };
        _db.ChatScheduleFiringWorkItems.Add(record);
        await _db.SaveChangesAsync(CancellationToken.None);
        _recordId = record.Id;
        await _notifier.SchedulesChangedAsync(_owner, _scheduleId);
    }

    /// <summary>Once the server has named the item. Throws if it still cannot be recorded, leaving it unresolved.</summary>
    public async Task ResolveAsync(string workItemId)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _db.ChatScheduleFiringWorkItems
                    .Where(w => w.Id == _recordId)
                    .ExecuteUpdateAsync(s => s.SetProperty(w => w.WorkItemId, workItemId), CancellationToken.None);
                _resolved = true;
                break;
            }
            catch (Exception ex) when (attempt < ResolveAttempts)
            {
                _log.LogDebug(ex, "Recording work item {WorkItemId} on firing {FiringId} failed; retrying", workItemId, _firingId);
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt));
            }
        }
        await _notifier.SchedulesChangedAsync(_owner, _scheduleId);
    }

    /// <summary>After the server refused the create outright, so nothing was created.</summary>
    public async Task WithdrawAsync()
    {
        if (_recordId is not { } id || _resolved) return;
        try
        {
            await _db.ChatScheduleFiringWorkItems.Where(w => w.Id == id).ExecuteDeleteAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not withdraw a refused create from firing {FiringId}; it stays unresolved", _firingId);
            return;
        }
        await _notifier.SchedulesChangedAsync(_owner, _scheduleId);
    }
}
