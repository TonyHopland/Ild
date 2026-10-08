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

/// <summary>
/// A user's chat schedules (ADR-0025) and their firings. A firing is an ordinary
/// chat turn of the owner's, with every tool group, started only on an idle chat;
/// each one is recorded with how it went. Every user-facing call is scoped by
/// owner: another user's schedule reads as missing.
/// </summary>
public sealed class ChatScheduleService
{
    public const string PausedReason = "scheduler paused";
    public const string BusyReason = "chat busy";
    public const string RestartReason = "ILD restarted while this turn was running, so it was cut off.";
    public const string RestartNote = "This scheduled turn was cut off because ILD restarted while it was running.";
    public const string NoOutcomeReason = "The turn ended without recording a result.";

    private static readonly string[] ToolGroups = [AiToolCatalog.Read, AiToolCatalog.Write, AiToolCatalog.Execute, AiToolCatalog.Ild];

    private readonly AppDbContext _db;
    private readonly IProviderStore _providers;
    private readonly IChatService _chat;
    private readonly IChatTurnRunner _runner;
    private readonly IChatNotifier _notifier;
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
        IChatNotifier notifier,
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
        _notifier = notifier;
        _settings = settings;
        _locks = locks;
        _scopes = scopes;
        _time = time;
        _log = log;
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<ChatScheduleView>> ListAsync(string userId, CancellationToken ct = default)
    {
        var schedules = await _db.ChatSchedules.AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
        var ids = schedules.Select(s => s.Id).ToList();
        var lastFirings = await _db.ChatScheduleFirings.AsNoTracking()
            .Include(f => f.WorkItems)
            .Where(f => ids.Contains(f.ChatScheduleId)
                && f.Number == _db.ChatScheduleFirings.Where(g => g.ChatScheduleId == f.ChatScheduleId).Max(g => g.Number))
            .ToDictionaryAsync(f => f.ChatScheduleId, ct);
        return schedules.Select(s => ToView(s, lastFirings.GetValueOrDefault(s.Id))).ToList();
    }

    public async Task<ChatScheduleSaveResult> CreateAsync(string userId, ChatScheduleRequest request, CancellationToken ct = default)
    {
        var (valid, error) = await ValidateAsync(request, ct);
        if (valid is null) return new ChatScheduleSaveResult(null, error);

        var schedule = new ChatSchedule { Id = Guid.NewGuid(), UserId = userId, CreatedAt = UtcNow };
        Apply(schedule, valid);
        _db.ChatSchedules.Add(schedule);
        await _db.SaveChangesAsync(ct);
        return new ChatScheduleSaveResult(ToView(schedule, null), null);
    }

    /// <summary>Null when the user has no such schedule.</summary>
    public async Task<ChatScheduleSaveResult?> UpdateAsync(string userId, Guid id, ChatScheduleRequest request, CancellationToken ct = default)
    {
        if (!await OwnsAsync(userId, id, ct)) return null;
        var (valid, error) = await ValidateAsync(request, ct);
        if (valid is null) return new ChatScheduleSaveResult(null, error);

        using (await _locks.EnterAsync(id, ct))
        {
            var schedule = await _db.ChatSchedules.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);
            if (schedule is null) return null;

            // A chat started on the old tag's provider stays on it, so a changed tag needs a new chat.
            if (!string.Equals(schedule.AiTag, valid.AiTag, StringComparison.OrdinalIgnoreCase))
                schedule.LatestChatSessionId = null;
            Apply(schedule, valid);
            await _db.SaveChangesAsync(ct);
            return new ChatScheduleSaveResult(ToView(schedule, await LastFiringAsync(id, ct)), null);
        }
    }

    /// <summary>
    /// False when the user has no such schedule. Its chats stay and lose their
    /// mark, and the work items it filed stay.
    /// </summary>
    public async Task<bool> DeleteAsync(string userId, Guid id, CancellationToken ct = default)
    {
        if (!await OwnsAsync(userId, id, ct)) return false;

        int deleted;
        using (await _locks.EnterAsync(id, ct))
            deleted = await _db.ChatSchedules.Where(s => s.Id == id && s.UserId == userId).ExecuteDeleteAsync(ct);
        _locks.Forget(id);
        return deleted > 0;
    }

    /// <summary>
    /// Fires the schedule once now, paused or not, and leaves its next firing
    /// alone. Null when the user has no such schedule.
    /// </summary>
    public async Task<ChatScheduleFiringView?> RunNowAsync(string userId, Guid id, CancellationToken ct = default)
    {
        if (!await OwnsAsync(userId, id, ct)) return null;

        using (await _locks.EnterAsync(id, ct))
        {
            var schedule = await _db.ChatSchedules.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);
            if (schedule is null) return null;
            return ToView(await FireAsync(schedule, ChatScheduleTrigger.RunNow, scheduledFor: null));
        }
    }

    /// <summary>
    /// The scheduler's step for one candidate: under the schedule's lock, re-read
    /// it and the pause flag, then skip or fire it if it is still due. A schedule
    /// whose lock is taken is left for the next pass.
    /// </summary>
    public async Task FireIfDueAsync(Guid id, CancellationToken ct)
    {
        using var held = _locks.TryEnter(id);
        if (held is null) return;

        var schedule = await _db.ChatSchedules.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (schedule is null)
        {
            _locks.Forget(id);
            return;
        }
        if (!schedule.Enabled) return;

        var now = UtcNow;
        var due = schedule.NextFireAt <= now;
        if (await _settings.GetIsPausedAsync(ct))
        {
            if (!due) return;
            _db.ChatScheduleFirings.Add(new ChatScheduleFiring
            {
                Id = Guid.NewGuid(),
                ChatScheduleId = id,
                Number = await NextFiringNumberAsync(id),
                Trigger = ChatScheduleTrigger.Schedule,
                ScheduledFor = schedule.NextFireAt,
                FiredAt = now,
                Outcome = ChatScheduleFiringOutcome.Skipped,
                Reason = PausedReason,
            });
            schedule.PendingSince ??= schedule.NextFireAt;
            schedule.NextFireAt = NextFireAt(schedule, now);
            await _db.SaveChangesAsync(ct);
            return;
        }

        if (!due && schedule.PendingSince is null) return;

        // Advanced before firing, so a firing that fails part-way is not retried
        // every pass: however many firings were missed, one is made up for.
        var scheduledFor = schedule.PendingSince ?? schedule.NextFireAt;
        schedule.PendingSince = null;
        schedule.NextFireAt = NextFireAt(schedule, now);
        await _db.SaveChangesAsync(ct);
        await FireAsync(schedule, ChatScheduleTrigger.Schedule, scheduledFor);
    }

    /// <summary>
    /// For startup: no turn survives a restart, so every firing still Running
    /// fails, and a chat whose turn was cut off is told so.
    /// </summary>
    public async Task FailInterruptedFiringsAsync(CancellationToken ct)
    {
        var interrupted = await _db.ChatScheduleFirings
            .Where(f => f.Outcome == ChatScheduleFiringOutcome.Running)
            .ToListAsync(ct);
        if (interrupted.Count == 0) return;

        foreach (var firing in interrupted)
        {
            firing.Outcome = ChatScheduleFiringOutcome.Failed;
            firing.Reason = RestartReason;
        }

        var cutOff = interrupted
            .Where(f => f.TurnId is not null && f.ChatSessionId is not null)
            .Select(f => f.ChatSessionId!.Value)
            .Distinct()
            .ToList();
        var chats = await _db.ChatSessions.AsNoTracking()
            .Where(c => cutOff.Contains(c.Id))
            .Select(c => new { c.Id, c.UserId, LastSequence = _db.ChatMessages.Where(m => m.ChatSessionId == c.Id).Max(m => (int?)m.Sequence) })
            .ToListAsync(ct);
        foreach (var chat in chats)
        {
            _db.ChatMessages.Add(new ChatMessage
            {
                Id = Guid.NewGuid(),
                ChatSessionId = chat.Id,
                Role = "assistant",
                Content = RestartNote,
                Interrupted = true,
                Sequence = (chat.LastSequence ?? -1) + 1,
                CreatedAt = DateTime.UtcNow,
            });
        }
        await _db.SaveChangesAsync(ct);

        foreach (var chat in chats)
            await _notifier.ActivityChangedAsync(chat.UserId, chat.Id);
        _log.LogWarning("Failed {Count} scheduled chat firing(s) that a restart cut off", interrupted.Count);
    }

    private Task<bool> OwnsAsync(string userId, Guid id, CancellationToken ct)
        => _db.ChatSchedules.AsNoTracking().AnyAsync(s => s.Id == id && s.UserId == userId, ct);

    // Firings are written only under the schedule's lock, so the next number is free.
    private async Task<int> NextFiringNumberAsync(Guid scheduleId)
        => (await _db.ChatScheduleFirings.Where(f => f.ChatScheduleId == scheduleId).MaxAsync(f => (int?)f.Number) ?? 0) + 1;

    private Task<ChatScheduleFiring?> LastFiringAsync(Guid scheduleId, CancellationToken ct)
        => _db.ChatScheduleFirings.AsNoTracking()
            .Include(f => f.WorkItems)
            .Where(f => f.ChatScheduleId == scheduleId)
            .OrderByDescending(f => f.Number)
            .FirstOrDefaultAsync(ct);

    private async Task<ChatScheduleFiring> FireAsync(ChatSchedule schedule, ChatScheduleTrigger trigger, DateTime? scheduledFor)
    {
        var previous = await _db.ChatScheduleFirings.AsNoTracking()
            .Where(f => f.ChatScheduleId == schedule.Id && f.TurnId != null)
            .OrderByDescending(f => f.Number)
            .Select(f => (DateTime?)f.FiredAt)
            .FirstOrDefaultAsync();
        var firedAt = UtcNow;
        var (chatId, error) = await ChatForFiringAsync(schedule);
        var firing = new ChatScheduleFiring
        {
            Id = Guid.NewGuid(),
            ChatScheduleId = schedule.Id,
            Number = await NextFiringNumberAsync(schedule.Id),
            Trigger = trigger,
            ScheduledFor = scheduledFor,
            FiredAt = firedAt,
            Outcome = chatId is null ? ChatScheduleFiringOutcome.Failed : ChatScheduleFiringOutcome.Running,
            Reason = chatId is null ? ChatScheduleFiring.ClipReason(error!) : null,
            ChatSessionId = chatId,
        };
        _db.ChatScheduleFirings.Add(firing);
        await _db.SaveChangesAsync();
        if (chatId is null) return firing;

        var message = await ContextBlockAsync(schedule, firing.FiredAt, previous) + "\n\n" + schedule.Prompt;
        try
        {
            var turnId = await _runner.TrySubmitIfIdleAsync(chatId.Value, message,
                async startingTurnId =>
                {
                    firing.TurnId = startingTurnId;
                    await _db.SaveChangesAsync();
                },
                EndFiringWithoutOutcomeAsync);
            if (turnId is null)
            {
                firing.Outcome = ChatScheduleFiringOutcome.Skipped;
                firing.Reason = BusyReason;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Schedule {ScheduleId} could not start its turn in chat {ChatSessionId}", schedule.Id, chatId);
            firing.Outcome = ChatScheduleFiringOutcome.Failed;
            firing.Reason = ChatScheduleFiring.ClipReason(ex.Message);
            firing.TurnId = null;
        }
        await _db.SaveChangesAsync();
        return firing;
    }

    /// <summary>
    /// The chat the firing's turn runs in: the schedule's own when it continues
    /// one that still exists, otherwise a new chat of the owner's, named after the
    /// schedule, on the provider its tag picks. Or why there is none.
    /// </summary>
    private async Task<(Guid? ChatId, string? Error)> ChatForFiringAsync(ChatSchedule schedule)
    {
        if (schedule.ContinueSession && schedule.LatestChatSessionId is { } latest
            && await _chat.ExistsForUserAsync(schedule.UserId, latest))
            return (latest, null);

        var (provider, error) = await AiNodeProviderResolver.ResolveAsync(
            _providers, schedule.AiTag, RemoteAiProviderOverrideMode.None, overrideId: null);
        if (provider is null) return (null, error);

        ChatSessionView chat;
        try
        {
            chat = await _chat.StartAsync(schedule.UserId, provider.Id, ToolGroups);
        }
        catch (InvalidOperationException ex)
        {
            return (null, ex.Message);
        }

        await _db.ChatSessions
            .Where(c => c.Id == chat.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.ChatScheduleId, schedule.Id)
                .SetProperty(c => c.Name, schedule.Name)
                .SetProperty(c => c.TitleSource, ChatTitleSource.Manual));
        schedule.LatestChatSessionId = chat.Id;
        await _db.SaveChangesAsync();

        // Into the owner's sidebar now, not only once its first turn ends.
        await _notifier.ActivityChangedAsync(schedule.UserId, chat.Id);
        return (chat.Id, null);
    }

    /// <summary>
    /// What a scheduled turn is told before its prompt. Nothing is open in the UI
    /// for it, so everything it needs to know about this firing is here.
    /// </summary>
    private async Task<string> ContextBlockAsync(ChatSchedule schedule, DateTime firedAt, DateTime? previous)
    {
        var repositories = schedule.RepositoryScope switch
        {
            ChatScheduleRepositoryScope.All => "all repositories",
            ChatScheduleRepositoryScope.None => "none",
            _ => await SelectedRepositoryNamesAsync(schedule),
        };
        return string.Join("\n",
            "[Scheduled job]",
            $"This turn was started by the schedule \"{schedule.Name}\", not by a person, and nothing is open in the UI.",
            "A scheduled job only checks: file anything that needs changing as a work item, and change no code.",
            $"This firing: {Iso(firedAt)}",
            $"Previous firing: {(previous is { } at ? Iso(at) : "none")}",
            $"Repositories it may use: {repositories}");
    }

    private async Task<string> SelectedRepositoryNamesAsync(ChatSchedule schedule)
    {
        var ids = RepositoryIdsOf(schedule);
        var names = await _db.Repositories.AsNoTracking()
            .Where(r => ids.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.Name);
        return string.Join(", ", ids.Select(id => names.GetValueOrDefault(id) ?? id.ToString()));
    }

    private static string Iso(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// The end of a firing's turn that recorded no outcome of its own: it was
    /// stopped or replaced, or it ended without a result (it threw, or its chat
    /// was deleted under it). Runs after the turn, outside any request's scope.
    /// </summary>
    private async Task EndFiringWithoutOutcomeAsync(Guid turnId, bool stopped)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.ChatScheduleFirings
            .Where(f => f.TurnId == turnId && f.Outcome == ChatScheduleFiringOutcome.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.Outcome, stopped ? ChatScheduleFiringOutcome.Stopped : ChatScheduleFiringOutcome.Failed)
                .SetProperty(f => f.Reason, stopped ? null : NoOutcomeReason));
    }

    private async Task<(ValidSchedule? Valid, string? Error)> ValidateAsync(ChatScheduleRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0) return (null, "A name is required.");
        if (name.Length > ChatSchedule.MaxNameLength) return (null, $"The name can be at most {ChatSchedule.MaxNameLength} characters.");
        if (string.IsNullOrWhiteSpace(request.Prompt)) return (null, "A prompt is required.");
        if (!ChatScheduleCron.TryParse(request.CronExpression, out var cron, out var cronError)) return (null, cronError);
        if (!TryFindZone(request.TimeZone, out var zone)) return (null, $"Unknown time zone '{request.TimeZone}'.");

        var repositoryIds = Array.Empty<Guid>();
        if (request.RepositoryScope == ChatScheduleRepositoryScope.Selected)
        {
            repositoryIds = (request.RepositoryIds ?? []).Distinct().ToArray();
            if (repositoryIds.Length == 0) return (null, "Select at least one repository, or choose all or none.");
            var known = await _db.Repositories.AsNoTracking().Where(r => repositoryIds.Contains(r.Id)).Select(r => r.Id).ToListAsync(ct);
            var unknown = repositoryIds.Except(known).ToList();
            if (unknown.Count > 0) return (null, $"Repository not found: {string.Join(", ", unknown)}");
        }

        var tag = request.AiTag?.Trim();
        if (string.IsNullOrEmpty(tag)) tag = null;
        else if (AiProviderTag.Problem(tag) is { } tagProblem) return (null, $"The AI tag {tagProblem}.");
        var (provider, providerError) = await AiNodeProviderResolver.ResolveAsync(
            _providers, tag, RemoteAiProviderOverrideMode.None, overrideId: null);
        if (provider is null) return (null, providerError);

        return (new ValidSchedule(
            name, request.Prompt!, tag, request.CronExpression!.Trim(), request.TimeZone!, cron, zone, request.Enabled,
            request.RepositoryScope, repositoryIds, request.ContinueSession), null);
    }

    private void Apply(ChatSchedule schedule, ValidSchedule valid)
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
        // An edit counts from now: nothing missed before it is made up for.
        schedule.PendingSince = null;
        schedule.NextFireAt = valid.Enabled ? ChatScheduleCron.Next(valid.Cron, UtcNow, valid.Zone) : null;
    }

    private static DateTime NextFireAt(ChatSchedule schedule, DateTime afterUtc)
        => ChatScheduleCron.Next(
            ChatScheduleCron.Parse(schedule.CronExpression), afterUtc, TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZone));

    private static bool TryFindZone(string? id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(id)) return false;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    private static IReadOnlyList<Guid> RepositoryIdsOf(ChatSchedule schedule)
        => schedule.RepositoryIdsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToList();

    private static ChatScheduleView ToView(ChatSchedule s, ChatScheduleFiring? lastFiring)
        => new(s.Id, s.Name, s.Prompt, s.AiTag, s.CronExpression, s.TimeZone, s.Enabled, s.RepositoryScope,
            RepositoryIdsOf(s), s.ContinueSession, s.LatestChatSessionId, s.NextFireAt,
            lastFiring is null ? null : ToView(lastFiring));

    private static ChatScheduleFiringView ToView(ChatScheduleFiring f)
        => new(f.Id, f.Trigger, f.ScheduledFor, f.FiredAt, f.Outcome, f.Reason, f.ChatSessionId,
            f.WorkItems.Where(w => w.WorkItemId is not null).OrderBy(w => w.CreatedAt).Select(w => w.WorkItemId!).ToList(),
            f.WorkItems.Count(w => w.WorkItemId is null));

    private sealed record ValidSchedule(
        string Name, string Prompt, string? AiTag, string CronExpression, string TimeZone,
        ChatScheduleCron.Expression Cron, TimeZoneInfo Zone, bool Enabled,
        ChatScheduleRepositoryScope RepositoryScope, IReadOnlyList<Guid> RepositoryIds, bool ContinueSession);
}
