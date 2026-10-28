using System.Runtime.CompilerServices;
using ILD.Core.Services.Backup;
using ILD.Core.Services.Implementations;

namespace ILD.Tests;

/// <summary>
/// Lowers the PBKDF2 iteration counts for the whole test process before any test runs: the suite
/// derives a backup key and hashes a password hundreds of times, and at production strength that
/// is most of its CPU. <see cref="ProductionKeyDerivationTests"/> pins the production counts.
/// </summary>
internal static class FastKeyDerivation
{
    internal const int Iterations = 1_000;

    [ModuleInitializer]
    internal static void Apply()
    {
        BackupCipher.IterationsOverride = Iterations;
        AuthService.Pbkdf2IterationsOverride = Iterations;
    }
}
