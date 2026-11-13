using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Migrations;

/// <summary>
/// Gives the installers' whitelist scope (docs/adr/0035) the hosts a toolchain install needs, once per database. Install
/// scripts run in <c>whitelist</c> mode only for what this scope (and the global list) names, and an operator edits the
/// scope in Settings → Network, so the defaults are inserted once and recorded by <see cref="MarkerKey"/>: an entry the
/// operator deleted stays deleted. A backup restored from before the upgrade carries no marker, so the next startup seeds it.
/// </summary>
public static class InstallerNetworkDefaultsMigrator
{
    public const string MarkerKey = "migration.installerNetworkDefaults";

    /// <summary>
    /// Where mise (its registry, its versions and GitHub's release downloads), the download of each core tool and the package
    /// registries of npm, pip, cargo and the .NET credential provider fetch from. Exact hosts, or a leading-dot suffix.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultHosts =
    [
        // mise itself, and the tools it takes from GitHub releases.
        ".jdx.dev", "github.com", "api.github.com", ".githubusercontent.com",
        // The core tools (RunTool.CoreNames), where mise downloads each from.
        "nodejs.org",
        "go.dev", "dl.google.com",
        "dot.net", "builds.dotnet.microsoft.com", "ci.dot.net", "dotnetcli.azureedge.net", "dotnetcli.blob.core.windows.net",
        ".rust-lang.org", "rustup.rs", "sh.rustup.rs",
        ".python.org",
        "cache.ruby-lang.org",
        ".adoptium.net", "download.java.net", "cdn.azul.com", "api.azul.com", "corretto.aws", "download.oracle.com",
        ".ziglang.org",
        "bun.sh",
        "deno.land", "dl.deno.land",
        "download.swift.org",
        "builds.hex.pm", "repo.hex.pm", "hex.pm",
        // The package registries.
        "registry.npmjs.org",
        "pypi.org", "files.pythonhosted.org",
        "crates.io", "static.crates.io", "index.crates.io",
        "api.nuget.org",
    ];

    /// <summary>Runs the seed; returns the number of entries added.</summary>
    public static async Task<int> MigrateAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (await db.AppSettings.AnyAsync(s => s.Key == MarkerKey, ct))
            return 0;

        var existing = (await db.NetworkPolicyEntries
                .Where(e => e.Installers && e.ListKind == NetworkListKind.Whitelist)
                .Select(e => e.Host)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var added = 0;
        foreach (var host in DefaultHosts.Where(h => !existing.Contains(h)))
        {
            db.NetworkPolicyEntries.Add(new NetworkPolicyEntry
            {
                Id = Guid.NewGuid(),
                Host = host,
                ListKind = NetworkListKind.Whitelist,
                Installers = true,
                CreatedAt = DateTime.UtcNow,
            });
            added++;
        }
        db.AppSettings.Add(new AppSetting { Id = Guid.NewGuid(), Key = MarkerKey, Value = "done" });
        await db.SaveChangesAsync(ct);
        return added;
    }
}
