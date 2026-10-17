using System.Diagnostics;
using Porta.Pty;
public class ZzProbe
{
    [Fact]
    public async Task Probe()
    {
        var res = await Task.WhenAll(Enumerable.Range(0, 400).Select(async _ =>
        {
            var pty = await PtyProvider.SpawnAsync(new PtyOptions { Name = "p", Cols = 80, Rows = 24, Cwd = Path.GetTempPath(), App = "/bin/sh",
                CommandLine = new[] { "-c", "echo boom; exit 3" }, Environment = new Dictionary<string, string>() }, default);
            var exited = new TaskCompletionSource<long>();
            pty.ProcessExited += (_, e) => exited.TrySetResult(Stopwatch.GetTimestamp()); var atSub = pty.WaitForExit(0);
            var buf = new byte[4096];
            try { while (await pty.ReaderStream.ReadAsync(buf) > 0) { } } catch (IOException) { }
            var eof = Stopwatch.GetTimestamp();
            try { var ex = await exited.Task.WaitAsync(TimeSpan.FromSeconds(20)); pty.Dispose(); return (Stopwatch.GetElapsedTime(eof, ex).TotalMilliseconds, atSub ? "" : ""); }
            catch (TimeoutException) { var w = pty.WaitForExit(0); var late = exited.Task.IsCompleted; string code; try { code = pty.ExitCode.ToString(); } catch (Exception e) { code = e.GetType().Name; } pty.Dispose(); return (-1e9, $"missing atSub={atSub} wait={w} late={late} code={code}"); }
        }));
        var gaps = res.Where(r => r.Item2 == "").Select(r => r.Item1).OrderBy(x => x).ToArray();
        var miss = res.Where(r => r.Item2 != "").Select(r => r.Item2).ToArray();
        Assert.Fail($"PROBE n={gaps.Length} min {gaps[0]:F1} median {gaps[gaps.Length/2]:F1} p99 {gaps[(int)(gaps.Length*0.99)]:F1} max {gaps[^1]:F1}; missing {miss.Length}: {string.Join(" | ", miss.Distinct())}");
    }
}
