using System.Text;
using System.Text.RegularExpressions;

namespace ILD.Tests;

/// <summary>
/// Where the agent's processes and files live is the run host's business (ADR-0014's uid split is the
/// <c>local</c> host's way of doing it), so nothing else may start a process, open a PTY, name
/// <c>setpriv</c>, or open a worktree or the agent's home itself: that is what lets a host that puts the run
/// somewhere else plug in. This scans the sources of ILD.Core and ILD.Api and fails on any of those outside
/// <c>RunHosts/Local</c>.
///
/// <para>
/// It has no allowlist beyond that folder: a new spawn site is a violation until it goes through
/// <c>IRunHost</c>. <c>new ProcessStartInfo</c> is not one — it only describes a launch, and the host
/// is what starts it.
/// </para>
///
/// <para>
/// Its one blind spot is a string interpolation hole: <c>$"{…}"</c> is blanked whole, so a spawn written
/// inside one goes unseen. Reading holes back out needs a real lexer, and no spawn has ever been written in one.
/// </para>
/// </summary>
public class AgentIsolationSpawnSiteTests
{
    private static readonly Regex SpawnSite = new(
        @"\bProcess\.Start\s*\(|\bnew\s+Process\b|\bPtyProvider\b|\bsetpriv\b", RegexOptions.Compiled);

    // A read of the worktree or of an agent's home by the orchestrator's own uid and filesystem. Statements are
    // matched whole, so `Directory.Exists(run.WorktreePath)` and a worktree path built three lines up both count.
    private static readonly Regex WorktreeRead = new(
        @"\b(?:Directory|File)\.(?:Exists|ReadAll\w+|Open\w*|Enumerate\w+|GetFiles|GetDirectories)\s*\([^;]*(?:[Ww]orktreePath|worktreePath|[Ww]orktree\b|AgentHome)",
        RegexOptions.Compiled);

    [Fact]
    public void No_process_spawn_PTY_or_setpriv_exists_outside_the_local_run_host()
    {
        var sources = SourceFiles().ToDictionary(f => f, f => Redact(File.ReadAllText(f)), StringComparer.Ordinal);
        var sites = sources
            .SelectMany(entry => SpawnSite.Matches(entry.Value).Select(m => (File: entry.Key, Source: entry.Value, Match: m)))
            .ToList();

        // Redaction or the pattern silently matching nothing would turn this into a test that passes because it
        // looked nowhere.
        Assert.True(
            Regex.IsMatch(Redact(File.ReadAllText(Path.Combine(RepositoryRoot(), "ILD.Core", "Services", "RunHosts", "Local", "LocalRunHost.cs"))), SpawnSite.ToString()),
            "the local run host should still hold the spawn");

        var outside = sites
            .Select(s => $"{Path.GetFileName(s.File)}:{LineOf(s.Source, s.Match.Index)} {s.Match.Value}")
            .ToList();

        Assert.True(outside.Count == 0,
            "A process, PTY or setpriv outside RunHosts/Local; go through IRunHost:\n  " + string.Join("\n  ", outside));
    }

    [Fact]
    public void No_worktree_or_agent_home_is_read_by_path_outside_the_local_run_host()
    {
        var reads = SourceFiles()
            .SelectMany(f =>
            {
                var source = Redact(File.ReadAllText(f));
                return WorktreeRead.Matches(source).Select(m => $"{Path.GetFileName(f)}:{LineOf(source, m.Index)}");
            })
            .ToList();

        Assert.True(reads.Count == 0,
            "A worktree or agent home read by path outside RunHosts/Local; use IRunHost's file members:\n  " + string.Join("\n  ", reads));
    }

    [Theory]
    [InlineData("Process.Start(psi);", true)]
    [InlineData("using var p = new Process { StartInfo = psi };", true)]
    [InlineData("var pty = await PtyProvider.SpawnAsync(options, ct);", true)]
    [InlineData("var wrapped = new[] { \"/usr/bin/setpriv\" }; var s = setpriv;", true)]
    [InlineData("var psi = new ProcessStartInfo(\"git\");", false)]
    [InlineData("await host.StartCommandAsync(RunCommand.Routed(psi));", false)]
    public void The_scan_tells_a_spawn_from_a_description_of_one(string statement, bool spawn)
        => Assert.Equal(spawn, SpawnSite.IsMatch(Redact(statement)));

    [Theory]
    [InlineData("if (!Directory.Exists(run.WorktreePath)) return;", true)]
    [InlineData("var text = File.ReadAllText(Path.Combine(worktreePath, \"a\"));", true)]
    [InlineData("Directory.Exists(logDirectory);", false)]
    [InlineData("await host.DirectoryExistsAsync(worktreePath, ct);", false)]
    public void The_scan_tells_a_worktree_read_from_another_file_check(string statement, bool read)
        => Assert.Equal(read, WorktreeRead.IsMatch(Redact(statement)));

    [Fact]
    public void A_spawn_named_only_in_a_message_is_not_a_spawn()
    {
        // Every CLI adapter once carried `throw new InvalidOperationException("Process.Start
        // returned null")`, so a scan that reads string literals reports phantom sites and gets muted.
        Assert.DoesNotMatch(SpawnSite, Redact("""
            var p = proc ?? throw new InvalidOperationException("Process.Start returned null");
            """));
    }

    [Theory]
    // Plain, verbatim, raw and interpolated literals, and a comment. A verbatim
    // string opening on an escaped quote and one closing on a backslash behind an
    // @$ prefix guard the end of the literal: misread, the scan runs past it.
    [InlineData("var s = \"Process.Start(x)\";")]
    [InlineData("var s = @\"Process.Start(x)\";")]
    [InlineData("var s = @\"\"\"Process.Start(x)\";")]
    [InlineData("var s = \"\"\"Process.Start(x)\"\"\";")]
    [InlineData("var s = $\"{n} Process.Start(x)\";")]
    [InlineData("var s = @$\"x\\\";")]
    [InlineData("var s = $@\"x\\\";")]
    [InlineData("var c = '\"'; // Process.Start(x)")]
    public void Redaction_hides_a_literal_without_swallowing_the_code_after_it(string literal)
    {
        var source = Redact(literal + "\nProcess.Start(psi);");

        // One site and not two, because a spawn named inside a literal is not one.
        // One site and not zero, because a redactor that runs past a literal's end
        // blanks the real code after it — and this scan cannot report a site it has
        // already erased, which is the one way it fails silently.
        Assert.Single(SpawnSite.Matches(source));
    }

    private static IEnumerable<string> SourceFiles()
    {
        var root = RepositoryRoot();
        return new[] { "ILD.Core", "ILD.Api" }
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            .Where(f => !f.Replace(Path.DirectorySeparatorChar, '/').Contains("/RunHosts/Local/", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal);
    }

    private static int LineOf(string source, int index) => source.Take(index).Count(c => c == '\n') + 1;

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ILD.sln"))) return dir.FullName;
        }
        // Passing because the sources are unreachable would be indistinguishable from
        // passing because they are clean.
        throw new InvalidOperationException($"no ILD.sln above {AppContext.BaseDirectory}");
    }

    /// <summary>
    /// Blanks comments and the contents of string/char literals, preserving length
    /// and line breaks so offsets still map to lines.
    /// </summary>
    private static string Redact(string source)
    {
        var outp = new StringBuilder(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            var rest = source.Length - i;
            if (rest >= 2 && source[i] == '/' && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') { outp.Append(' '); i++; }
                i--;
                continue;
            }
            if (rest >= 2 && source[i] == '/' && source[i + 1] == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? source.Length : end + 2;
                for (; i < end; i++) outp.Append(source[i] == '\n' ? '\n' : ' ');
                i--;
                continue;
            }
            if (source[i] is '"' or '\'')
            {
                i = BlankLiteral(source, i, outp);
                continue;
            }
            outp.Append(source[i]);
        }
        return outp.ToString();
    }

    /// <summary>Blanks the literal opening at <paramref name="start"/>; returns its last index.</summary>
    private static int BlankLiteral(string source, int start, StringBuilder outp)
    {
        var quote = source[start];
        // The prefix is @ and $ in either order, and only @ makes a string
        // verbatim — a check of the single preceding character misses @$"…\".
        // A verbatim string is also never raw: @"""x" opens with an escaped
        // quote, not a """ fence. Reading either wrong runs this past the
        // literal's end, blanking the real code after it — and a spawn site
        // blanked is a spawn site this scan reports as absent.
        var verbatim = IsVerbatim(source, start);
        var fence = quote == '"' && !verbatim ? QuoteRun(source, start) : 1;
        var raw = fence >= 3;

        outp.Append(quote, raw ? fence : 1);
        for (var i = start + (raw ? fence : 1); i < source.Length; i++)
        {
            if (raw)
            {
                if (source[i] == '"' && QuoteRun(source, i) >= fence)
                {
                    outp.Append('"', fence);
                    return i + fence - 1;
                }
            }
            else if (source[i] == '\\' && !verbatim)
            {
                outp.Append(' ', Math.Min(2, source.Length - i));
                i++;
                continue;
            }
            else if (source[i] == quote)
            {
                if (verbatim && i + 1 < source.Length && source[i + 1] == quote)
                {
                    outp.Append("  ");
                    i++;
                    continue;
                }
                outp.Append(quote);
                return i;
            }
            outp.Append(source[i] == '\n' ? '\n' : ' ');
        }
        return source.Length - 1;
    }

    /// <summary>Whether the literal opening at <paramref name="start"/> carries an <c>@</c> prefix.</summary>
    private static bool IsVerbatim(string source, int start)
    {
        for (var i = start - 1; i >= 0 && source[i] is '@' or '$'; i--)
        {
            if (source[i] == '@') return true;
        }
        return false;
    }

    private static int QuoteRun(string source, int i)
    {
        var n = 0;
        while (i + n < source.Length && source[i + n] == '"') n++;
        return n;
    }
}
