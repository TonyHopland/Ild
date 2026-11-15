using System.Security.Cryptography;
using System.Text;

namespace ILD.Core.Services.RunHosts.Isolated;

/// <summary>
/// Which spec the chat container or pod was made from. The chat environment is long-lived, so one made from another spec (a new
/// runner image, other settings) is replaced: the engine works out what it would make now and stores the stamp of it on the object
/// (a label or an annotation, under <see cref="Key"/>); whether a stored stamp is still current is decided here.
/// </summary>
public static class ChatSpecStamp
{
    /// <summary>The label or annotation that carries the stamp.</summary>
    public const string Key = "ild/spec";

    /// <summary>The stamp of what the chat environment is made from: the first 8 bytes of its SHA-256, in hex.</summary>
    public static string Of(string spec)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(spec)), 0, 8).ToLowerInvariant();

    /// <summary>Whether the chat environment is as ILD would make it now; one with no stamp was made by an ILD that did not stamp it.</summary>
    public static bool IsCurrent(string? stored, string computed) => stored is not null && stored == computed;
}
