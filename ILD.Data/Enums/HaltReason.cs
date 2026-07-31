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
}
