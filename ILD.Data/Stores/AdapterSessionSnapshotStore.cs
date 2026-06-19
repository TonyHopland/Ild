using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Stores;

public sealed class AdapterSessionSnapshotStore : IAdapterSessionSnapshotStore
{
    private readonly AppDbContext _db;

    public AdapterSessionSnapshotStore(AppDbContext db)
    {
        _db = db;
    }

    public Task<AdapterSessionSnapshot?> GetAsync(Guid loopRunId, string adapterName, string sessionId, CancellationToken cancellationToken = default)
        => _db.AdapterSessionSnapshots
            .FirstOrDefaultAsync(
                s => s.LoopRunId == loopRunId
                    && s.AdapterName == adapterName
                    && s.SessionId == sessionId,
                cancellationToken);

    public async Task UpsertAsync(Guid loopRunId, string adapterName, string sessionId, string sessionJson, CancellationToken cancellationToken = default)
    {
        var existing = await GetAsync(loopRunId, adapterName, sessionId, cancellationToken);
        if (existing is null)
        {
            _db.AdapterSessionSnapshots.Add(new AdapterSessionSnapshot
            {
                Id = Guid.NewGuid(),
                LoopRunId = loopRunId,
                AdapterName = adapterName,
                SessionId = sessionId,
                SessionJson = sessionJson,
            });
        }
        else
        {
            existing.SessionJson = sessionJson;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task<AdapterSessionSnapshot?> GetForChatAsync(Guid chatSessionId, string adapterName, string sessionId, CancellationToken cancellationToken = default)
        => _db.AdapterSessionSnapshots
            .FirstOrDefaultAsync(
                s => s.ChatSessionId == chatSessionId
                    && s.AdapterName == adapterName
                    && s.SessionId == sessionId,
                cancellationToken);

    public async Task UpsertForChatAsync(Guid chatSessionId, string adapterName, string sessionId, string sessionJson, CancellationToken cancellationToken = default)
    {
        var existing = await GetForChatAsync(chatSessionId, adapterName, sessionId, cancellationToken);
        if (existing is null)
        {
            _db.AdapterSessionSnapshots.Add(new AdapterSessionSnapshot
            {
                Id = Guid.NewGuid(),
                ChatSessionId = chatSessionId,
                AdapterName = adapterName,
                SessionId = sessionId,
                SessionJson = sessionJson,
            });
        }
        else
        {
            existing.SessionJson = sessionJson;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }
}
