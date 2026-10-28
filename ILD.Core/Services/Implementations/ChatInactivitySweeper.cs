using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// The opt-in deletion of inactive chats: while
/// <see cref="AppSettingKeys.ChatAutoDeleteInactive"/> is on, every unstarred chat
/// of every user with no new message for
/// <see cref="AppSettingKeys.ChatAutoDeleteInactiveDays"/> is hard-deleted the way
/// a manual delete does it. Both settings are read on every pass, so a change
/// takes effect on the next one without a restart.
///
/// A chat with a turn in flight, or a send, stop or delete under way, is skipped
/// rather than interrupted and comes up again on a later pass. Whether the chat is
/// still unstarred and inactive is checked again under its turn gate, so one that
/// was spoken to or starred after it was picked is kept. A chat that fails to
/// delete is kept and the pass goes on with the rest.
/// </summary>
public sealed class ChatInactivitySweeper : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(10);

    private readonly IServiceScopeFactory _scopes;
    private readonly IChatTurnRunner _runner;
    private readonly ILogger<ChatInactivitySweeper> _log;

    public ChatInactivitySweeper(
        IServiceScopeFactory scopes,
        IChatTurnRunner runner,
        ILogger<ChatInactivitySweeper> log)
    {
        _scopes = scopes;
        _runner = runner;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = InitialDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { return; }
            delay = SweepInterval;

            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Inactive chat sweep failed");
            }
        }
    }

    internal async Task SweepOnceAsync(CancellationToken ct)
    {
        DateTime cutoff;
        IReadOnlyList<Guid> candidates;
        using (var scope = _scopes.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var settings = sp.GetRequiredService<ISchedulerSettingsService>();
            if (!await settings.GetChatAutoDeleteInactiveAsync(ct)) return;

            var days = await settings.GetChatAutoDeleteInactiveDaysAsync(ct);
            cutoff = DateTime.UtcNow - TimeSpan.FromDays(days);
            candidates = await sp.GetRequiredService<IChatService>().ListInactiveAsync(cutoff, ct);
        }

        var deleted = 0;
        foreach (var chatId in candidates)
        {
            if (ct.IsCancellationRequested) break;
            // A scope per chat, so a delete that failed partway leaves nothing
            // tracked behind for the next one to save.
            using var scope = _scopes.CreateScope();
            var chats = scope.ServiceProvider.GetRequiredService<IChatService>();
            try
            {
                // Not cancelled by shutdown once begun: stopping halfway would leave
                // the chat without its pi files for no reason.
                if (await _runner.TryDeleteIfIdleAsync(chatId, () => chats.DeleteIfInactiveAsync(chatId, cutoff, CancellationToken.None)))
                    deleted++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not delete inactive chat {ChatSessionId}; it is kept and retried on the next sweep", chatId);
            }
        }

        if (deleted > 0)
            _log.LogInformation("Deleted {Deleted} chats with no message since {Cutoff:o}", deleted, cutoff);
    }
}
