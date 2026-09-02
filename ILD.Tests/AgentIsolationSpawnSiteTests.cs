using System.Text;
using System.Text.RegularExpressions;

namespace ILD.Tests;

/// <summary>
/// ADR-0014's capability drop is an invariant over <em>every</em> orchestrator-side
/// spawn, but nothing about constructing a <c>Process</c> forces a caller to
/// remember it — the Cmd node executor ran unwrapped because the rule lived only in
/// prose. This scans ILD.Core's sources and fails on a spawn no <c>AgentIsolation</c>
/// wrap reaches, so a fourth site cannot drift the way the third did.
///
/// <para>
/// It has no allowlist: a correctly wrapped new site passes on its own and never
/// needs this test edited. What it asks for is that the wrap be visible in the spawn
/// statement — inline, or delegated to a builder that hands back a wrapped
/// <c>Process</c>/<c>ProcessStartInfo</c>. A source scan has no types to follow, so
/// wrapping several statements earlier reads as a violation here; that is the point,
/// since it also reads that way to the next person editing the method.
/// </para>
///
/// <para>
/// Its one blind spot is a string interpolation hole: <c>$"{…}"</c> is blanked
/// whole, so a spawn written inside one goes unseen. Reading holes back out needs
/// a real lexer, and no spawn has ever been written in one.
/// </para>
/// </summary>
public class AgentIsolationSpawnSiteTests
{
    // The two wraps that actually rewrite a spawn through setpriv. Reading a path
    // off AgentIsolation is not isolation, so those calls must not count.
    private static readonly string[] WrapCalls =
    {
        "AgentIsolation.Route(",
        "AgentIsolation.DropInheritedCapabilities(",
    };

    // Every process starts life at one of these two, so covering construction covers
    // spawning: an instance p.Start() is always preceded by a `new Process`.
    private static readonly Regex SpawnSite =
        new(@"\bProcess\.Start\s*\(|\bnew\s+Process\b", RegexOptions.Compiled);

    // A launcher must HAND BACK the wrapped spawn, so its return type carries the
    // whole restriction. Without it, every method that merely contains a wrapped
    // spawn — ProcessRunner.RunAsync, AIProviderService.RunShellAsync,
    // CmdNodeExecutor.RunProcessAsync — reads as a launcher, and a later spawn
    // statement passes by naming one of them: the drift this test exists to catch.
    private const string Returns =
        @"\b(?:(?:Task|ValueTask)\s*<\s*Process(?:StartInfo)?\??\s*>|Process(?:StartInfo)?\??)\s+";

    private static readonly Regex BlockBodied =
        new(Returns + @"(?<name>[A-Za-z_]\w*)\s*(<[^()<>]*>)?\s*\([^;{}]*\)\s*(where[^;{}]*)?\{", RegexOptions.Compiled);

    private static readonly Regex ExpressionBodied =
        new(Returns + @"(?<name>[A-Za-z_]\w*)\s*(<[^()<>]*>)?\s*\([^;{}]*\)\s*=>(?<body>[^;{}]*);", RegexOptions.Compiled);

    [Fact]
    public void Every_process_spawn_in_ILD_Core_is_wrapped_by_AgentIsolation()
    {
        var sources = CoreSourceFiles().ToDictionary(f => f, f => Redact(File.ReadAllText(f)), StringComparer.Ordinal);
        var launchers = LauncherNames(sources.Values);

        var sites = sources
            .SelectMany(entry => SpawnSite.Matches(entry.Value).Select(m => (File: entry.Key, Source: entry.Value, Match: m)))
            .ToList();

        // Redaction or the spawn pattern silently matching nothing would turn this
        // into a test that passes because it looked nowhere.
        Assert.True(sites.Count >= 5, $"expected ILD.Core to still spawn processes, found {sites.Count} sites");

        var unwrapped = sites
            .Where(s => !IsWrapped(Statement(s.Source, s.Match.Index), launchers))
            .Select(s => $"{Path.GetFileName(s.File)}:{LineOf(s.Source, s.Match.Index)}")
            .ToList();

        Assert.True(unwrapped.Count == 0,
            "Process spawned without an AgentIsolation wrap (ADR-0014):\n  " + string.Join("\n  ", unwrapped));
    }

    [Theory]
    // Inline, as ProcessRunner and AIProviderService do it.
    [InlineData("Process.Start(AgentIsolation.DropInheritedCapabilities(psi));", true)]
    // Through an object initializer, as the Cmd node executor does it.
    [InlineData("using var p = new Process { StartInfo = AgentIsolation.Route(psi), EnableRaisingEvents = true };", true)]
    // Through a builder, as WorktreePreviewService does it.
    [InlineData("var proc = Process.Start(BuildWrapped(step)) ?? throw new InvalidOperationException(\"no\");", true)]
    [InlineData("var p = new Process(); p.StartInfo = psi;", false)]
    [InlineData("Process.Start(psi);", false)]
    // A builder that does not wrap must not launder the spawn.
    [InlineData("Process.Start(BuildPlain(step));", false)]
    // A method that merely CONTAINS a wrapped spawn is not a launcher — naming it
    // must not vouch for a second, unwrapped one.
    [InlineData("Process.Start(RunAsync().StartInfo);", false)]
    // AgentIsolation is more than the wrap; the rest of it isolates nothing.
    [InlineData("Process.Start(new ProcessStartInfo(AgentIsolation.ScratchRoot));", false)]
    public void The_scan_tells_a_wrapped_spawn_from_a_drifted_one(string statement, bool wrapped)
    {
        var launchers = LauncherNames(new[]
        {
            Redact("class F { ProcessStartInfo BuildWrapped(S s) => AgentIsolation.Route(Psi(s)); }"),
            Redact("class F { ProcessStartInfo BuildPlain(S s) { return Psi(s); } }"),
            Redact("class F { async Task<R> RunAsync(S s) { return R(Process.Start(AgentIsolation.Route(Psi(s)))); } }"),
        });
        var source = Redact(statement);
        var site = SpawnSite.Match(source);

        Assert.True(site.Success);
        Assert.Equal(wrapped, IsWrapped(Statement(source, site.Index), launchers));
    }

    [Fact]
    public void A_spawn_named_only_in_a_message_is_not_a_spawn()
    {
        // Every CLI adapter carries `throw new InvalidOperationException("Process.Start
        // returned null")`, so a scan that reads string literals reports six phantom
        // sites and gets muted.
        Assert.False(SpawnSite.IsMatch(Redact("""
            var p = proc ?? throw new InvalidOperationException("Process.Start returned null");
            """)));
    }

    [Theory]
    // Plain, verbatim, raw, interpolated — plus the two that used to run the scan
    // off the end of the literal: a verbatim string opening on an escaped quote,
    // and one closing on a backslash behind an @$ prefix.
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
        var source = Redact(literal + "\nProcess.Start(AgentIsolation.Route(psi));");
        var sites = SpawnSite.Matches(source);

        // One site and not two, because a spawn named inside a literal is not one.
        // One site and not zero, because a redactor that runs past a literal's end
        // blanks the real code after it — and this scan cannot report a site it has
        // already erased, which is the one way it fails silently.
        Assert.Single(sites);
        Assert.True(IsWrapped(Statement(source, sites[0].Index), Array.Empty<string>()));
    }

    private static bool IsWrapped(string statement, IReadOnlyCollection<string> launchers) =>
        WrapCalls.Any(w => statement.Contains(w, StringComparison.Ordinal))
        || launchers.Any(name => Regex.IsMatch(statement, $@"\b{Regex.Escape(name)}\s*\("));

    /// <summary>
    /// Methods that return a wrapped spawn — the builders and launchers a spawn
    /// statement may legitimately delegate to. A launcher that also spawns
    /// unwrapped launders nothing: that spawn is still reported at its own
    /// statement, which is where the fix belongs.
    /// </summary>
    private static HashSet<string> LauncherNames(IEnumerable<string> redactedSources)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in redactedSources)
        {
            foreach (Match m in ExpressionBodied.Matches(source))
            {
                if (Wraps(m.Groups["body"].Value)) names.Add(m.Groups["name"].Value);
            }
            foreach (Match m in BlockBodied.Matches(source))
            {
                if (Wraps(Block(source, m.Index + m.Length - 1))) names.Add(m.Groups["name"].Value);
            }
        }
        return names;

        static bool Wraps(string body) => WrapCalls.Any(w => body.Contains(w, StringComparison.Ordinal));
    }

    /// <summary>The braced block opening at <paramref name="open"/>.</summary>
    private static string Block(string source, int open)
    {
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..i];
        }
        return source[open..];
    }

    /// <summary>
    /// The spawn's own statement: from the construction to the semicolon that ends
    /// it, so an argument, an object initializer and a trailing `?? throw` all count
    /// as part of the spawn, while a wrap left behind in an earlier statement does not.
    /// </summary>
    private static string Statement(string source, int start)
    {
        var depth = 0;
        for (var i = start; i < source.Length; i++)
        {
            if (source[i] is '(' or '{' or '[') depth++;
            else if (source[i] is ')' or '}' or ']') depth--;
            else if (source[i] == ';' && depth <= 0) return source[start..i];
        }
        return source[start..];
    }

    private static int LineOf(string source, int index) => source.Take(index).Count(c => c == '\n') + 1;

    private static IEnumerable<string> CoreSourceFiles()
    {
        var core = Path.Combine(RepositoryRoot(), "ILD.Core");
        return Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            .OrderBy(f => f, StringComparer.Ordinal);
    }

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
