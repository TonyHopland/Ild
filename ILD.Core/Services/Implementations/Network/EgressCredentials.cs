using System.Security.Cryptography;
using System.Text;

namespace ILD.Core.Services.Implementations.Network;

/// <summary>What a launch's proxy credential grants: the provider whose scoped entries apply, or the installer zone, or neither.</summary>
public readonly record struct LaunchScope(Guid? Provider = null, string? Installer = null);

/// <summary>
/// The credentials the egress proxy hands to its launches (<c>docs/adr/0019</c>). A credential is a token ILD signs with a key of its
/// own: <c>base64url(scope).base64url(HMAC-SHA256(scope))</c>. The proxy takes a launch's scope only from a token it can verify,
/// never from what a client claims, so an agent cannot widen its scope and a client without a token gets nothing.
///
/// <para>
/// Nothing is stored per launch. The key lives in a file under ILD's data path and is read again at every start, so the token in the
/// environment of a parked run or the chat container, which is reconnected after a restart and keeps that environment, still verifies.
/// </para>
/// </summary>
public sealed class EgressCredentials
{
    /// <summary>The Basic-auth user name of every proxy URL. Some proxy clients (undici among them) send <c>Proxy-Authorization</c> only when both user and password are present.</summary>
    public const string User = "ild";

    private const int KeyBytes = 32;

    private readonly byte[] _key;

    public EgressCredentials(byte[] key)
    {
        if (key.Length < KeyBytes) throw new ArgumentException($"The key must be at least {KeyBytes} bytes.", nameof(key));
        _key = key;
    }

    private static EgressCredentials _current = new(RandomNumberGenerator.GetBytes(KeyBytes));

    /// <summary>
    /// The credentials launches are minted with. The launch helpers are static, as they are about the process environment, so the
    /// one ILD runs with is set once at start (<see cref="Use"/>); until then a key of this process's own, which verifies nowhere else.
    /// </summary>
    public static EgressCredentials Current => Volatile.Read(ref _current);

    public static void Use(EgressCredentials credentials) => Volatile.Write(ref _current, credentials);

    /// <summary>The key kept in <paramref name="path"/>, made there (readable by ILD alone) when it is not.</summary>
    public static EgressCredentials Load(string path)
    {
        if (File.Exists(path) && File.ReadAllBytes(path) is { Length: >= KeyBytes } existing)
            return new EgressCredentials(existing);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var key = RandomNumberGenerator.GetBytes(KeyBytes);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, options))
                stream.Write(key);
            // Published whole, so a second ILD starting at once reads a complete key; whichever lands first is the key.
            if (!File.Exists(path)) File.Move(temp, path);
        }
        finally
        {
            File.Delete(temp);
        }
        return new EgressCredentials(File.ReadAllBytes(path));
    }

    /// <summary>The token a launch with <paramref name="scope"/> is given.</summary>
    public string Mint(LaunchScope scope)
    {
        if (scope.Installer?.Contains('|') == true) throw new ArgumentException("A zone has no '|'.", nameof(scope));
        var payload = Encoding.UTF8.GetBytes($"v1|{scope.Provider:D}|{scope.Installer}");
        return Base64Url(payload) + "." + Base64Url(HMACSHA256.HashData(_key, payload));
    }

    /// <summary>The scope a <c>Proxy-Authorization</c> header value carries, when it is Basic, names ILD's user and holds a token this key signed.</summary>
    public bool TryRead(string? proxyAuthorization, out LaunchScope scope)
    {
        scope = default;
        var parts = (proxyAuthorization ?? string.Empty).Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !parts[0].Equals("Basic", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
            var colon = credentials.IndexOf(':');
            return colon == User.Length && credentials.StartsWith(User, StringComparison.Ordinal) && TryVerify(credentials[(colon + 1)..], out scope);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private bool TryVerify(string token, out LaunchScope scope)
    {
        scope = default;
        var dot = token.IndexOf('.');
        if (dot <= 0 || !TryFromBase64Url(token[..dot], out var payload) || !TryFromBase64Url(token[(dot + 1)..], out var signature)) return false;
        if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(_key, payload), signature)) return false;

        var fields = Encoding.UTF8.GetString(payload).Split('|');
        if (fields.Length != 3 || fields[0] != "v1") return false;
        Guid? provider = null;
        if (fields[1].Length > 0)
        {
            if (!Guid.TryParse(fields[1], out var id)) return false;
            provider = id;
        }
        scope = new LaunchScope(provider, fields[2].Length > 0 ? fields[2] : null);
        return true;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryFromBase64Url(string value, out byte[] bytes)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        try
        {
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}
