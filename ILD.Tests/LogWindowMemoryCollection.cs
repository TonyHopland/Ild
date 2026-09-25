using Xunit;

namespace ILD.Tests;

/// <summary>
/// A test that measures how much memory reading a CI log leaves behind cannot run
/// beside others: <c>GC.GetTotalMemory</c> is process-wide, so anything another
/// test allocates in the same moment is counted against the reader. Mirrors
/// <see cref="ProcessGlobalStateCollection"/>, which serializes for the same kind
/// of reason — one shared, process-global thing.
/// </summary>
[CollectionDefinition("LogWindowMemory", DisableParallelization = true)]
public sealed class LogWindowMemoryCollection
{
}
