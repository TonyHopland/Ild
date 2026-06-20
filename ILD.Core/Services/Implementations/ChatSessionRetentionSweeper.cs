using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Backstops abandoned chat sessions (ADR-0010). A Chat Session is normally
/// reclaimed only on an explicit "End chat"; this sweeper hard-deletes sessions
/// idle longer than <see cref="ChatOptions.IdleRetentionPeriod"/> so a user who
/// walks away forever does not leak a session row and scratch directory.
/// </summary>
public sealed class ChatSessionRetentionSweeper : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ChatOptions _options;
    private readonly ILogger<ChatSessionRetentionSweeper> _log;

    public ChatSessionRetentionSweeper(
        IServiceScopeFactory scopes,
        ChatOptions options,
        ILogger<ChatSessionRetentionSweeper> log)
    {
        _scopes = scopes;
        _options = options;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var chat = scope.ServiceProvider.GetRequiredService<IChatService>();
                var cutoff = DateTimeOffset.UtcNow - _options.IdleRetentionPeriod;
                var removed = await chat.SweepIdleAsync(cutoff, stoppingToken);
                if (removed > 0)
                    _log.LogInformation("Chat session retention swept {Removed} idle session(s) older than {Cutoff:o}", removed, cutoff);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Chat session retention sweep failed");
            }

            try { await Task.Delay(_options.SweepInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
