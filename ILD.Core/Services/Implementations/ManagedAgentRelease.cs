using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ILD.Core.Services.Implementations.Adapters;

namespace ILD.Core.Services.Implementations;

/// <summary>The newest build a release manifest names and where to fetch it.</summary>
/// <param name="Version">The version the manifest names, or null when it names none.</param>
/// <param name="Url">The <c>.tar.gz</c> archive's address.</param>
/// <param name="Sha512">The archive's SHA-512 as lowercase hex. It is an integrity check only: the manifest and the archive come from
/// the same server, so it catches a damaged download and says nothing about who published it.</param>
internal sealed record AgentReleaseManifest(string? Version, string? Url, string? Sha512);

public sealed partial class ManagedAgentService
{
    /// <summary>The platform name release manifests are published under, or null on a platform the agent has none for.</summary>
    internal static string? ReleasePlatform()
    {
        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };
        if (architecture is null) return null;
        if (OperatingSystem.IsMacOS()) return $"darwin_{architecture}";
        if (!OperatingSystem.IsLinux()) return null;
        return IsMusl() ? $"linux_{architecture}_musl" : $"linux_{architecture}";
    }

    private static bool IsMusl()
        => Directory.Exists("/lib") && Directory.EnumerateFiles("/lib", "*musl*").Any();

    /// <summary>The manifest URL ILD uses: the environment's, when the release names a variable that is set, else the built-in one.</summary>
    internal string ManifestUrlOf(AgentRelease release)
        => release.ManifestUrlVariable is { } variable && _environment.Get(variable) is { Length: > 0 } configured ? configured : release.ManifestUrl;

    private async Task<AgentReleaseManifest> ReadReleaseManifestAsync(AgentRelease release, CancellationToken ct)
    {
        var platform = ReleasePlatform()
            ?? throw new InvalidOperationException("This platform has no published build of the agent.");
        using var doc = await _http.GetFromJsonAsync<JsonDocument>(ManifestUrlOf(release).Replace("{platform}", platform), ct);
        var root = doc?.RootElement;
        string? Text(string name)
            => root is { ValueKind: JsonValueKind.Object } element && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        return new AgentReleaseManifest(Text("version"), Text("url"), Text("sha512")?.ToLowerInvariant());
    }

    /// <summary>
    /// Downloads the release into <paramref name="versionDir"/> for the platform ILD runs on, which is where ILD's own terminals and
    /// local runs start it: the archive is checked against the manifest's SHA-512 while it streams to disk, then its one executable
    /// is taken out as <c>bin/&lt;binary&gt;</c> and the version and manifest address are recorded beside it. A run in a container or
    /// pod of its own does not use this copy: <see cref="ILD.Core.Services.RunHosts.Tools.RunToolStore"/> installs the release there
    /// with a script of its own, in the installer container, for the platform of that machine. A manifest only ever names the newest build, so an exact
    /// <paramref name="version"/> other than that one cannot be installed.
    /// </summary>
    private async Task InstallReleaseAsync(ManagedAgent agent, AgentRelease release, string version, string versionDir, CancellationToken ct)
    {
        var manifest = await ReadReleaseManifestAsync(release, ct);
        if (manifest.Version is not { Length: > 0 } published || manifest.Url is not { Length: > 0 } url || manifest.Sha512 is not { Length: > 0 } sha512
            || !Uri.TryCreate(url, UriKind.Absolute, out var archiveUri) || archiveUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"The {agent.DisplayName} release manifest is incomplete.");
        if (version != "latest" && version != published)
            throw new InvalidOperationException($"{agent.DisplayName} {version} cannot be installed: the release server offers only {published}.");

        var archive = Path.Combine(versionDir, "release.tar.gz");
        using (var response = await _http.GetAsync(archiveUri, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
            await using (var file = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var body = await response.Content.ReadAsStreamAsync(ct))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await body.ReadAsync(buffer, ct)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), sha512, StringComparison.Ordinal))
                throw new InvalidOperationException($"The downloaded {agent.DisplayName} archive does not match the checksum its manifest names.");
        }

        var binary = ManagedAgentInstall.BinaryIn(versionDir, agent);
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        await ExtractEntryAsync(archive, release.ArchiveEntry, binary, ct);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.Delete(archive);

        File.WriteAllText(
            ManagedAgentInstall.ReleaseRecordIn(versionDir),
            JsonSerializer.Serialize(new { version = published, manifestUrl = ManifestUrlOf(release) }));
    }

    private static async Task ExtractEntryAsync(string archive, string entryName, string destination, CancellationToken ct)
    {
        await using var compressed = File.OpenRead(archive);
        await using var tar = new GZipStream(compressed, CompressionMode.Decompress);
        using var reader = new TarReader(tar);
        while (await reader.GetNextEntryAsync(copyData: false, ct) is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                || entry.DataStream is null
                || entry.Name.TrimStart('.', '/') != entryName)
                continue;

            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await entry.DataStream.CopyToAsync(output, ct);
            return;
        }
        throw new InvalidOperationException($"The downloaded archive has no '{entryName}' file.");
    }
}
