using ILD.Data.Entities;

namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Owns the per-<see cref="LoopRun"/> AI session map: how
/// <c>SessionsJson</c> (a JSON array of <c>{providerId, sessionId}</c>) is
/// parsed, looked up by provider, and rewritten when an adapter finishes.
///
/// Previously this lived as ~80 lines of private helpers inside
/// <c>AINodeExecutor</c>, mixing JSON shape, legacy-case tolerance, and
/// persistence into the node-execution flow. Pulling it behind a small
/// interface lets sessions be tested directly and gives non-AI callers
/// (e.g. recovery, diagnostics) a stable handle.
///
/// Contract:
/// <list type="bullet">
///   <item>Property names are camelCase (<c>providerId</c>, <c>sessionId</c>);
///   PascalCase is accepted on read for legacy DB rows but never written.</item>
///   <item>Passing <c>sessionId=null</c> to
///   <see cref="PersistAsync"/> clears the entry for that provider.</item>
///   <item><see cref="PersistAsync"/> writes through the supplied
///   <c>LoopRun</c> and calls the run store's <c>UpdateRunAsync</c>; callers
///   are responsible for using a fresh run snapshot to avoid clobbering
///   concurrent column writes.</item>
/// </list>
/// </summary>
public interface IAiSessionManager
{
    /// <summary>
    /// Return the session id associated with <paramref name="providerId"/> in
    /// <paramref name="sessionsJson"/>, or <c>null</c> if none is recorded
    /// (or if the JSON is malformed).
    /// </summary>
    string? Resolve(string? sessionsJson, Guid providerId);

    /// <summary>
    /// Upsert (or, with <c>sessionId=null</c>, remove) the provider's session
    /// entry on <paramref name="run"/> and persist via the supplied store
    /// delegate. The delegate is the run store's update method so this
    /// service can stay free of EF/DI plumbing.
    /// </summary>
    Task PersistAsync(LoopRun run, Guid providerId, string? sessionId, Func<LoopRun, Task> persist);
}
