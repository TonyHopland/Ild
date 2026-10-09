using System.Globalization;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>How a schedule write or Run now went, when it did not simply succeed.</summary>
public enum ChatScheduleRefusal
{
    /// <summary>The caller has no such schedule.</summary>
    NotFound,

    /// <summary>The request is invalid; the result's error says why.</summary>
    Invalid,

    /// <summary>A firing, edit or delete of the schedule is under way.</summary>
    Busy,
}

/// <summary>A schedule write or Run now: its value, or why there is none.</summary>
public sealed record ChatScheduleResult<T>(T? Value, ChatScheduleRefusal? Refusal = null, string? Error = null)
    where T : class
{
    public static ChatScheduleResult<T> Refused(ChatScheduleRefusal refusal, string? error = null) => new(null, refusal, error);
}

/// <summary>
/// A user's Chat Schedules and their firings (ADR-0026). A firing is an ordinary
/// chat turn of the owner's, with every tool group, started only on an idle chat
/// and recorded with how it went. Every user-facing call is scoped by owner, so
/// another user's schedule reads as missing.
/// </summary>
public sealed class ChatScheduleService
{
    public const string PausedReason = "scheduler paused";
    public const string ChatBusyReason = "chat busy";
    public const string PreviousTurnRunningReason = "the previous firing's turn is still running";
    public const string RestartReason = "ILD restarted while this turn was running";
    public const string RestartNote = "This scheduled turn was cut off because ILD restarted while it was running.";
    public const string NoResultReason = "The turn ended without a result.";
    public const string BusyError = "The schedule is firing or being changed right now; try again in a moment.";

    private static readonly string[] ToolGroups = [AiToolCatalog.Read, AiToolCatalog.Write, AiToolCatalog.Execute, AiToolCatalog.Ild];

    private readonly AppDbContext _db;
    private readonly IProviderStore _providers;
    private readonly IChatService _chat;
    private readonly IChatTurnRunner _runner;
    private readonly IChatNotifier _chatNotifier;
    private readonly IChatScheduleNotifier _notifier;
    private readonly ISchedulerSettingsService _settings;
    private readonly ChatScheduleLocks _locks;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<ChatScheduleService> _log;

    public ChatScheduleService(
        AppDbContext db,
        IProviderStore providers,
        IChatService chat,
        IChatTurnRunner runner,
        IChatNotifier chatNotifier,
        IChatScheduleNotifier notifier,
        ISchedulerSettingsService settings,
        ChatScheduleLocks locks,
        IServiceScopeFactory scopes,
        TimeProvider time,
        ILogger<ChatScheduleService> log)
    {
        _db = db;
        _providers = providers;
        _chat = chat;
        _runner = runner;
        _chatNotifier = chatNotifier;
        _notifier = notifier;
        _settings = settings;
        _locks = locks;
        _scopes = scopes;
        _time = time;
        _log = log;
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    public async Task<ChatScheduleListView> ListAsync(string userId, CancellationToken ct = default)
    {
        var paused = await _settings.GetIsPausedAsync(ct);
        var schedules = await _db.ChatSchedules.AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderBy(s => s.Name).ThenBy(s => s.Id)
            .ToListAsync(ct);
        var ids = schedules.Select(s => s.Id).ToList();

        var firings = _db.ChatScheduleFirings.AsNoTracking().Where(f => ids.Contains(f.ScheduleId));
        var lastFirings = await firings
            .Where(f => f.Sequence == _db.ChatScheduleFirings.Where(g => g.ScheduleId == f.ScheduleId).Max(g => g.Sequence))
            .ToDictionaryAsync(f => f.ScheduleId, ct);
        var latestChats = await firings
            .Where(f => f.ChatSessionId != null && f.Sequence == _db.ChatScheduleFirings
                .Where(g => g.ScheduleId == f.ScheduleId && g.ChatSessionId != null).Max(g => g.Sequence))
            .ToDictionaryAsync(f => f.ScheduleId, f => f.ChatSessionId, ct);

        var named = schedules.SelectMany(RepositoryIdsOf).Distinct().ToList();
        var existing = (await _db.Repositories.AsNoTracking().Where(r => named.Contains(r.Id)).Select(r => r.Id).ToListAsync(ct))
            .ToHashSet();

        return new ChatScheduleListView(paused, schedules
            .Select(s => ToView(s, existing, lastFirings.GetValueOrDefault(s.Id), latestChats.GetValueOrDefault(s.Id)))
            .ToList());
    }

    public async Task<ChatScheduleResult<ChatScheduleView>> CreateAsync(string userId, ChatScheduleRequest request, CancellationToken ct = default)
    {
        var (valid, error) = await ValidateAsync(request, ct);
        if (valid is null) return ChatScheduleResult<ChatScheduleView>.Refused(ChatScheduleRefusal.Invalid, error);

        var now = UtcNow;
        var schedule = new ChatSchedule { Id = Guid.NewGuid(), UserId = userId, CreatedAt = now };
        Apply(schedule, valid);
        schedule.NextFireAt = valid.Enabled ? valid.Cron.Next(now, valid.Zone) : null;
        _db.ChatSchedules.Add(schedule);
        await _db.SaveChangesAsync(ct);

        await _notifier.SchedulesChangedAsync(userId, schedule.Id);
        return new(await ViewAsync(schedule, ct));
    }

    /// <summary>
    /// Saves the owner's edit. Turning the schedule off clears its next firing and
    /// any it owes; turning it on, or changing its cron or zone, counts the next
    /// firing from now. A changed AI tag makes the next firing start a new chat,
    /// since a chat keeps its provider for life.
    /// </summary>
    public async Task<ChatScheduleResult<ChatScheduleView>> UpdateAsync(string userId, Guid id, ChatScheduleRequest request, CancellationToken ct = default)
    {
        if (!await OwnsAsync(userId, id, ct)) return ChatScheduleResult<ChatScheduleView>.Refused(ChatScheduleRefusal.NotFound);
        var (valid, error) = await ValidateAsync(request, ct);
        if (valid is null) return ChatScheduleResult<ChatScheduleView>.Refused(ChatScheduleRefusal.Invalid, error);

        using (var held = _locks.TryEnter(id))
        {
            if (held is null) return ChatScheduleResult<ChatScheduleView>.Refused(ChatScheduleRefusal.Busy, BusyError);
            var schedule = await _db.ChatSchedules.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);
            if (schedule is null) return ChatScheduleResult<ChatScheduleView>.Refused(ChatScheduleRefusal.NotFound);

            if (!string.Equals(schedule.AiTag, valid.AiTag, StringComparison.OrdinalIgnoreCase))
                schedule.ContinueChatSessionId = null;
            var timingChanged = schedule.Enabled != valid.Enabled
                || schedule.CronExpression != valid.CronExpression
                || schedule.TimeZone != valid.TimeZone;
            Apply(schedule, valid);
            if (!valid.Enabled)
            {
                schedule.NextFireAt = null;
                schedule.PendingSince = null;
            }
            else if (timingChanged)
            {
                schedule.NextFireAt = valid.Cron.Next(UtcNow, valid.Zone);
                schedule.PendingSince = null;
            }
            await _db.SaveChangesAsync(ct);

            await _notifier.SchedulesChangedAsync(userId, id);
            return new(await ViewAsync(schedule, ct));
        }
    }

    /// <summary>Removes the schedule and its firings. Its chats stay, unmarked.</summary>
    public async Task<ChatScheduleRefusal?> DeleteAsync(string userId, Guid id, CancellationToken ct = default)
    {
        if (!await OwnsAsync(userId, id, ct)) return ChatScheduleRefusal.NotFound;

        using (var held = _locks.TryEnter(id))
        {
            if (held is null) return ChatScheduleRefusal.Busy;
            var deleted = await _db.ChatSchedules.Where(s => s.Id == id && s.UserId == userId).ExecuteDeleteAsync(ct);
            if (deleted == 0) return ChatScheduleRefusal.NotFound;
        }

        await _notifier.SchedulesChangedAsync(userId, id);
        return null;
    }

    /// <summary>
    /// Fires the schedule once now, whether the scheduler is paused or the schedule
    /// disabled, and leaves its next cron firing alone.
    /// </summary>
    public async Task<ChatScheduleResult<ChatScheduleFiringView>> RunNowAsync(string userId, Guid id, CancellationToken ct = default)
    {
        if (!await OwnsAsync(userId, id, ct)) return ChatScheduleResult<ChatScheduleFiringView>.Refused(ChatScheduleRefusal.NotFound);

        using var held = _locks.TryEnter(id);
        if (held is null) return ChatScheduleResult<ChatScheduleFiringView>.Refused(ChatScheduleRefusal.Busy, BusyError);
        var schedule = await _db.ChatSchedules.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);
        if (schedule is null) return ChatScheduleResult<ChatScheduleFiringView>.Refused(ChatScheduleRefusal.NotFound);

        return new(ToView(await FireAsync(schedule, ChatScheduleTrigger.RunNow, ct)));
    }

    /// <summary>
    /// The scheduler's step for one schedule: if its cron is due, or it owes a
    /// firing from a pause, fire it once and count the next firing from now, so
    /// however many it missed it makes up for one. While the scheduler is paused a
    /// due firing is recorded as skipped and owed instead. A schedule whose lock is
    /// held is left for the next pass.
    /// </summary>
    public async Task FireIfDueAsync(Guid id, CancellationToken ct)
    {
        using var held = _locks.TryEnter(id);
        if (held is null) return;
        var schedule = await _db.ChatSchedules.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (schedule is not { Enabled: true }) return;

        var now = UtcNow;
        var due = schedule.NextFireAt <= now;
        if (due) schedule.NextFireAt = NextFireAt(schedule, now);

        if (await _settings.GetIsPausedAsync(ct))
        {
            if (!due) return;
            schedule.PendingSince ??= now;
            await RecordAsync(schedule, ChatScheduleTrigger.Cron, now, ChatScheduleFiringOutcome.Skipped, PausedReason, chatSessionId: null, ct);
            return;
        }

        if (!due && schedule.PendingSince is null) return;
        schedule.PendingSince = null;
        await FireAsync(schedule, ChatScheduleTrigger.Cron, ct);
    }

    /// <summary>
    /// For startup: a turn does not survive a restart, so a firing still Running
    /// whose turn this process is not running was cut off. It fails, and its chat
    /// is told so. A turn this process did start since is left alone.
    /// </summary>
    public async Task RecoverInterruptedAsync(CancellationToken ct)
    {
        var running = await _db.ChatScheduleFirings.AsNoTracking()
            .Where(f => f.Outcome == ChatScheduleFiringOutcome.Running)
            .Select(f => new { f.Id, f.ScheduleId, f.ChatSessionId, f.TurnId, Owner = f.Schedule.UserId })
            .ToListAsync(ct);

        foreach (var firing in running)
        {
            if (firing.ChatSessionId is { } live && firing.TurnId is { } turn && _runner.ActiveTurnId(live) == turn)
                continue;

            // The failure and the note in its chat are one change: a firing left
            // failed without its note would never be picked up again to write it.
            await using (var transaction = await _db.Database.BeginTransactionAsync(ct))
            {
                var failed = await _db.ChatScheduleFirings
                    .Where(f => f.Id == firing.Id && f.Outcome == ChatScheduleFiringOutcome.Running)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(f => f.Outcome, ChatScheduleFiringOutcome.Failed)
                        .SetProperty(f => f.Reason, RestartReason), ct);
                if (failed == 0) continue;

                if (firing.ChatSessionId is { } chatId)
                {
                    var lastSequence = await _db.ChatMessages.Where(m => m.ChatSessionId == chatId).MaxAsync(m => (int?)m.Sequence, ct);
                    _db.ChatMessages.Add(new ChatMessage
                    {
                        Id = Guid.NewGuid(),
                        ChatSessionId = chatId,
                        Role = "assistant",
                        Content = RestartNote,
                        Interrupted = true,
                        Sequence = (lastSequence ?? -1) + 1,
                        // The clock a turn stamps its messages with, so that a note
                        // sharing a sequence with one of them orders by save time.
                        CreatedAt = DateTime.UtcNow,
                    });
                    await _db.SaveChangesAsync(ct);
                }
                await transaction.CommitAsync(ct);
            }
            _log.LogWarning("Schedule firing {FiringId} was cut off by a restart", firing.Id);

            if (firing.ChatSessionId is { } noted)
            {
                await _chatNotifier.UnreadChangedAsync(firing.Owner, noted);
                await _chatNotifier.ActivityChangedAsync(firing.Owner, noted);
            }
            await _notifier.SchedulesChangedAsync(firing.Owner, firing.ScheduleId);
        }
    }

    private Task<bool> OwnsAsync(string userId, Guid id, CancellationToken ct)
        => _db.ChatSchedules.AsNoTracking().AnyAsync(s => s.Id == id && s.UserId == userId, ct);

    /// <summary>
    /// One firing, under the schedule's lock: skipped while an earlier firing's turn
    /// still runs, failed when it has no chat to run in, else a turn in the chat
    /// it continues or a new one, started only if that chat is idle. Every change
    /// made to <paramref name="schedule"/> beforehand is saved with the firing.
    /// </summary>
    private async Task<ChatScheduleFiring> FireAsync(ChatSchedule schedule, ChatScheduleTrigger trigger, CancellationToken ct)
    {
        var now = UtcNow;
        if (await ChatOfRunningTurnAsync(schedule.Id, ct) is { } runningIn)
            return await RecordAsync(schedule, trigger, now, ChatScheduleFiringOutcome.Skipped, PreviousTurnRunningReason, runningIn, ct);

        var previous = await _db.ChatScheduleFirings.AsNoTracking()
            .Where(f => f.ScheduleId == schedule.Id && f.TurnId != null)
            .OrderByDescending(f => f.Sequence)
            .Select(f => (DateTime?)f.FiredAt)
            .FirstOrDefaultAsync(ct);

        var (chatId, error) = await ChatForFiringAsync(schedule, ct);
        if (chatId is null)
            return await RecordAsync(schedule, trigger, now, ChatScheduleFiringOutcome.Failed, error, chatSessionId: null, ct);

        var message = await ContextBlockAsync(schedule, now, previous, ct) + "\n\n" + schedule.Prompt;
        var firing = new ChatScheduleFiring
        {
            Id = Guid.NewGuid(),
            ScheduleId = schedule.Id,
            Sequence = await NextSequenceAsync(schedule.Id, ct),
            Trigger = trigger,
            FiredAt = now,
            ChatSessionId = chatId,
            Outcome = ChatScheduleFiringOutcome.Running,
        };
        var (owner, scheduleId) = (schedule.UserId, schedule.Id);
        var turnId = await _runner.TrySubmitIfIdleAsync(chatId.Value, message,
            async startingTurn =>
            {
                firing.TurnId = startingTurn;
                schedule.PendingSince = null;
                _db.ChatScheduleFirings.Add(firing);
                await _db.SaveChangesAsync(CancellationToken.None);
            },
            (endedTurn, interrupted) => EndFiringAsync(owner, scheduleId, endedTurn, interrupted));

        if (turnId is null)
        {
            firing.Outcome = ChatScheduleFiringOutcome.Skipped;
            firing.Reason = ChatBusyReason;
            _db.ChatScheduleFirings.Add(firing);
            await _db.SaveChangesAsync(ct);
        }

        await _notifier.SchedulesChangedAsync(owner, scheduleId);
        return firing;
    }

    private async Task<ChatScheduleFiring> RecordAsync(
        ChatSchedule schedule, ChatScheduleTrigger trigger, DateTime firedAt,
        ChatScheduleFiringOutcome outcome, string? reason, Guid? chatSessionId, CancellationToken ct)
    {
        var firing = new ChatScheduleFiring
        {
            Id = Guid.NewGuid(),
            ScheduleId = schedule.Id,
            Sequence = await NextSequenceAsync(schedule.Id, ct),
            Trigger = trigger,
            FiredAt = firedAt,
            ChatSessionId = chatSessionId,
            Outcome = outcome,
            Reason = reason is null ? null : ChatScheduleFiring.ClipReason(reason),
        };
        _db.ChatScheduleFirings.Add(firing);
        await _db.SaveChangesAsync(ct);
        await _notifier.SchedulesChangedAsync(schedule.UserId, schedule.Id);
        return firing;
    }

    // Firings are only ever added under the schedule's lock, so the next number is free.
    private async Task<int> NextSequenceAsync(Guid scheduleId, CancellationToken ct)
        => (await _db.ChatScheduleFirings.Where(f => f.ScheduleId == scheduleId).MaxAsync(f => (int?)f.Sequence, ct) ?? 0) + 1;

    /// <summary>
    /// The chat in which an earlier firing's turn is still running, or null. A
    /// schedule runs one turn at a time even when each firing gets its own chat.
    /// Only a turn the runner still has counts, so a firing whose turn has just
    /// ended and is not recorded yet holds nothing up.
    /// </summary>
    private async Task<Guid?> ChatOfRunningTurnAsync(Guid scheduleId, CancellationToken ct)
    {
        var running = await _db.ChatScheduleFirings.AsNoTracking()
            .Where(f => f.ScheduleId == scheduleId && f.Outcome == ChatScheduleFiringOutcome.Running
                && f.TurnId != null && f.ChatSessionId != null)
            .Select(f => new { ChatId = f.ChatSessionId!.Value, TurnId = f.TurnId!.Value })
            .ToListAsync(ct);
        return running.FirstOrDefault(f => _runner.ActiveTurnId(f.ChatId) == f.TurnId)?.ChatId;
    }

    /// <summary>
    /// The chat the firing's turn runs in, on the provider its tag picks now: the
    /// schedule's own when it continues one that still exists on that provider,
    /// otherwise a new chat of the owner's with every tool group, named after the
    /// schedule. A chat keeps its provider for life, so a tag that has moved to
    /// another provider starts a new chat, as editing the tag does. Or why there is none.
    /// </summary>
    private async Task<(Guid? ChatId, string? Error)> ChatForFiringAsync(ChatSchedule schedule, CancellationToken ct)
    {
        var (provider, error) = await AiNodeProviderResolver.ResolveAsync(
            _providers, schedule.AiTag, RemoteAiProviderOverrideMode.None, overrideId: null);
        if (provider is null) return (null, error);

        if (schedule.ContinueSession && schedule.ContinueChatSessionId is { } continued
            && await _db.ChatSessions.AsNoTracking().AnyAsync(c => c.Id == continued
                && c.UserId == schedule.UserId && c.AiProviderId == provider.Id, ct))
            return (continued, null);

        ChatSessionView chat;
        try
        {
            chat = await _chat.StartAsync(schedule.UserId, provider.Id, ToolGroups, ct);
        }
        catch (InvalidOperationException ex)
        {
            return (null, ex.Message);
        }

        await _db.ChatSessions
            .Where(c => c.Id == chat.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.ScheduleId, schedule.Id)
                .SetProperty(c => c.Name, schedule.Name)
                .SetProperty(c => c.TitleSource, ChatTitleSource.Manual), ct);
        schedule.ContinueChatSessionId = chat.Id;

        // Creating a chat announces nothing by itself, and its first turn may run
        // for hours: the owner's sidebar learns of it now.
        await _chatNotifier.ActivityChangedAsync(schedule.UserId, chat.Id);
        return (chat.Id, null);
    }

    /// <summary>
    /// What a scheduled turn is told before its prompt. Nothing is open in the UI
    /// for it, so everything it needs to know about this firing is here.
    /// </summary>
    private async Task<string> ContextBlockAsync(ChatSchedule schedule, DateTime firedAt, DateTime? previous, CancellationToken ct)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZone);
        var repositories = schedule.RepositoryScope switch
        {
            ChatScheduleRepositoryScope.All => "all",
            ChatScheduleRepositoryScope.None => "none",
            _ => await SelectedRepositoryNamesAsync(schedule, ct),
        };
        return string.Join("\n",
            "[Scheduled job]",
            $"This turn was started by the schedule \"{schedule.Name}\", not by a person, and nothing is open in the UI.",
            "A scheduled job only checks: it changes no code, and files a work item for anything that needs changing.",
            $"This firing: {When(firedAt, zone)}",
            $"Previous firing: {(previous is { } at ? When(at, zone) : "none")}",
            $"Repositories it may use: {repositories}");
    }

    private async Task<string> SelectedRepositoryNamesAsync(ChatSchedule schedule, CancellationToken ct)
    {
        var ids = RepositoryIdsOf(schedule);
        var names = await _db.Repositories.AsNoTracking()
            .Where(r => ids.Contains(r.Id))
            .OrderBy(r => r.Name)
            .Select(r => r.Name)
            .ToListAsync(ct);
        return names.Count == 0 ? "none" : string.Join(", ", names);
    }

    private static string When(DateTime utc, TimeZoneInfo zone)
    {
        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
        return string.Create(CultureInfo.InvariantCulture, $"{local:yyyy-MM-dd HH:mm} {zone.Id} ({utc:yyyy-MM-dd'T'HH:mm:ss'Z'})");
    }

    /// <summary>
    /// The end of a firing's turn that the chat service did not record: stopped from
    /// the chat or replaced by the owner's message, or ended without a result. Runs
    /// after the turn, outside any request's scope.
    /// </summary>
    private async Task EndFiringAsync(string owner, Guid scheduleId, Guid turnId, bool interrupted)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var outcome = interrupted ? ChatScheduleFiringOutcome.Stopped : ChatScheduleFiringOutcome.Failed;
        var reason = interrupted ? null : NoResultReason;
        var ended = await db.ChatScheduleFirings
            .Where(f => f.TurnId == turnId && f.Outcome == ChatScheduleFiringOutcome.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.Outcome, outcome)
                .SetProperty(f => f.Reason, reason));
        if (ended > 0) await _notifier.SchedulesChangedAsync(owner, scheduleId);
    }

    private async Task<(ValidSchedule? Valid, string? Error)> ValidateAsync(ChatScheduleRequest request, CancellationToken ct)
    {
        var name = ChatTitles.WithoutNul(request.Name ?? string.Empty).Trim();
        if (name.Length == 0) return (null, "A name is required.");
        if (name.Length > ChatSchedule.MaxNameLength) return (null, $"The name can be at most {ChatSchedule.MaxNameLength} characters.");
        // Judged as it will be stored: the save strips NUL, which Postgres cannot hold.
        var prompt = ChatTitles.WithoutNul(request.Prompt ?? string.Empty);
        if (string.IsNullOrWhiteSpace(prompt)) return (null, "A prompt is required.");

        var tag = request.AiTag is null ? null : ChatTitles.WithoutNul(request.AiTag).Trim();
        if (string.IsNullOrEmpty(tag)) tag = null;
        else if (AiProviderTag.Problem(tag) is { } tagProblem) return (null, $"The AI tag {tagProblem}.");

        var cronExpression = request.CronExpression?.Trim() ?? string.Empty;
        if (cronExpression.Length > 256) return (null, "The cron expression is too long.");
        if (!ChatScheduleCron.TryParse(cronExpression, out var cron, out var cronError)) return (null, cronError);
        if (!TryFindZone(request.TimeZone, out var zone)) return (null, $"Unknown time zone '{request.TimeZone}'.");

        if (!Enum.IsDefined(request.RepositoryScope)) return (null, "The repository scope must be All, Selected or None.");
        var repositoryIds = (request.RepositoryIds ?? []).Distinct().ToList();
        if (request.RepositoryScope != ChatScheduleRepositoryScope.Selected)
        {
            if (repositoryIds.Count > 0) return (null, $"Repositories are chosen only with the Selected scope, not with {request.RepositoryScope}.");
        }
        else
        {
            if (repositoryIds.Count == 0) return (null, "Select at least one repository, or choose All or None.");
            var known = await _db.Repositories.AsNoTracking().Where(r => repositoryIds.Contains(r.Id)).Select(r => r.Id).ToListAsync(ct);
            var unknown = repositoryIds.Except(known).ToList();
            if (unknown.Count > 0) return (null, $"Not a repository: {string.Join(", ", unknown)}.");
        }

        return (new ValidSchedule(name, prompt, tag, cronExpression, cron, request.TimeZone!.Trim(), zone, request.Enabled,
            request.RepositoryScope, repositoryIds, request.ContinueSession), null);
    }

    private static void Apply(ChatSchedule schedule, ValidSchedule valid)
    {
        schedule.Name = valid.Name;
        schedule.Prompt = valid.Prompt;
        schedule.AiTag = valid.AiTag;
        schedule.CronExpression = valid.CronExpression;
        schedule.TimeZone = valid.TimeZone;
        schedule.Enabled = valid.Enabled;
        schedule.RepositoryScope = valid.RepositoryScope;
        schedule.RepositoryIdsCsv = string.Join(',', valid.RepositoryIds);
        schedule.ContinueSession = valid.ContinueSession;
    }

    private static DateTime NextFireAt(ChatSchedule schedule, DateTime afterUtc)
        => ChatScheduleCron.Parse(schedule.CronExpression).Next(afterUtc, TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZone));

    private static bool TryFindZone(string? id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128) return false;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    private static List<Guid> RepositoryIdsOf(ChatSchedule schedule)
        => schedule.RepositoryIdsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToList();

    private async Task<ChatScheduleView> ViewAsync(ChatSchedule schedule, CancellationToken ct)
    {
        var ids = RepositoryIdsOf(schedule);
        var existing = (await _db.Repositories.AsNoTracking().Where(r => ids.Contains(r.Id)).Select(r => r.Id).ToListAsync(ct)).ToHashSet();
        var firings = _db.ChatScheduleFirings.AsNoTracking().Where(f => f.ScheduleId == schedule.Id).OrderByDescending(f => f.Sequence);
        var last = await firings.FirstOrDefaultAsync(ct);
        var latestChat = await firings.Where(f => f.ChatSessionId != null).Select(f => f.ChatSessionId).FirstOrDefaultAsync(ct);
        return ToView(schedule, existing, last, latestChat);
    }

    // Repositories deleted since the schedule was saved are left out.
    private static ChatScheduleView ToView(ChatSchedule s, IReadOnlySet<Guid> existingRepositories, ChatScheduleFiring? lastFiring, Guid? latestChat)
        => new(s.Id, s.Name, s.Prompt, s.AiTag, s.CronExpression, s.TimeZone, s.Enabled, s.RepositoryScope,
            RepositoryIdsOf(s).Where(existingRepositories.Contains).ToList(), s.ContinueSession, Utc(s.NextFireAt),
            lastFiring is null ? null : ToView(lastFiring), latestChat);

    private static ChatScheduleFiringView ToView(ChatScheduleFiring f)
        => new(f.Id, f.Trigger, Utc(f.FiredAt), f.Outcome, f.Reason, f.ChatSessionId);

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? Utc(DateTime? value) => value is { } v ? Utc(v) : null;

    private sealed record ValidSchedule(
        string Name, string Prompt, string? AiTag, string CronExpression, ChatScheduleCron Cron,
        string TimeZone, TimeZoneInfo Zone, bool Enabled,
        ChatScheduleRepositoryScope RepositoryScope, IReadOnlyList<Guid> RepositoryIds, bool ContinueSession);
}
