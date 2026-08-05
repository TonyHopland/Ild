namespace ILD.Data.Enums;

/// <summary>
/// Who parked a run at <c>WaitingHuman</c> with <c>IsHalted</c> set. The two
/// halts look identical in the run row but mean opposite things on the next
/// start: a <see cref="Shutdown"/> halt is ILD's own bookmark and resumes
/// itself, a <see cref="Human"/> halt is somebody waiting to steer and must be
/// left exactly where they left it.
/// </summary>
public enum HaltReason
{
    /// <summary>A person pressed Halt in the live view.</summary>
    Human = 0,

    /// <summary>The host was asked to stop and the drain parked the run.</summary>
    Shutdown = 1,

    /// <summary>
    /// The AI provider interrupted the node — a usage or session limit, a 429,
    /// an overloaded provider, a dropped stream — and the engine parked the run
    /// rather than routing it onto the <c>on_failure</c> edge. Startup treats
    /// this like <see cref="Human"/>, not <see cref="Shutdown"/>: nobody
    /// auto-resumes it, because resuming before the provider's window resets
    /// just spends another round-trip to be throttled again. Waiting for the
    /// reset is a judgement the person reading "resets 9:40am" makes.
    /// </summary>
    Throttled = 2,
}
