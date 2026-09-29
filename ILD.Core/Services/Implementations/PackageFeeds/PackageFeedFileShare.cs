using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations.PackageFeeds;

/// <summary>
/// The credential file of an owner whose processes come and go — a preview runtime,
/// whose install run and each live service hold it. The file is written when the
/// first holder takes a lease and deleted when the last one lets go, so it exists
/// exactly while something that can read it is running; a lease taken after that
/// writes a fresh file from the same credentials.
/// </summary>
public sealed class PackageFeedFileShare
{
    private readonly IReadOnlyList<PackageFeedCredential> _feeds;
    private readonly string _prefix;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private PackageFeedEnvironment? _current;
    private int _holders;

    public PackageFeedFileShare(IReadOnlyList<PackageFeedCredential> feeds, string prefix, ILogger logger)
    {
        _feeds = feeds;
        _prefix = prefix;
        _logger = logger;
    }

    /// <summary>A hold on the file for one holder; disposing it more than once is harmless.</summary>
    public Lease Acquire()
    {
        lock (_lock)
        {
            _current ??= PackageFeedCredentialFiles.Materialize(_feeds, _prefix, _logger);
            _holders++;
            return new Lease(this, _current.Environment);
        }
    }

    private void Release()
    {
        lock (_lock)
        {
            if (--_holders > 0)
                return;
            _current!.Dispose();
            _current = null;
        }
    }

    public sealed class Lease : IDisposable
    {
        private readonly PackageFeedFileShare _share;
        private int _released;

        internal Lease(PackageFeedFileShare share, IReadOnlyDictionary<string, string> environment)
        {
            _share = share;
            Environment = environment;
        }

        /// <summary>The feed variables for the process this lease is held for.</summary>
        public IReadOnlyDictionary<string, string> Environment { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                _share.Release();
        }
    }
}
