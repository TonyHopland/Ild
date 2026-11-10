using ILD.Core.Services.Implementations.Storage;

namespace ILD.Tests;

public sealed class AppendLogTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("ild-append-log-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static void Append(FileStream stream, string text)
    {
        using (stream) stream.Write(System.Text.Encoding.UTF8.GetBytes(text));
    }

    [Fact]
    public void Creates_the_file_and_appends_to_it()
    {
        var path = Path.Combine(_directory, "web.log");

        Append(AppendLog.Open(_directory, path), "one\n");
        Append(AppendLog.Open(_directory, path), "two\n");

        Assert.Equal("one\ntwo\n", File.ReadAllText(path));
    }

    [Fact]
    public void Refuses_a_link_and_leaves_what_it_points_at_alone()
    {
        var target = Path.Combine(_directory, "target.txt");
        File.WriteAllText(target, "kept");
        var link = Path.Combine(_directory, "web.log");
        File.CreateSymbolicLink(link, target);

        Assert.Throws<InvalidOperationException>(() => AppendLog.Open(_directory, link));

        Assert.Equal("kept", File.ReadAllText(target));
    }

    [Fact]
    public void Refuses_a_path_outside_the_directory()
    {
        var inner = Directory.CreateDirectory(Path.Combine(_directory, "logs")).FullName;

        Assert.Throws<InvalidOperationException>(() => AppendLog.Open(inner, Path.Combine(inner, "..", "escaped.log")));
        Assert.Throws<InvalidOperationException>(() => AppendLog.Open(inner, Path.Combine(inner, "sub", "x.log")));
        Assert.False(File.Exists(Path.Combine(_directory, "escaped.log")));
    }

    [Fact]
    public void Refuses_a_pipe_without_blocking()
    {
        if (!OperatingSystem.IsLinux()) return;
        var fifo = Path.Combine(_directory, "web.log");
        using (var mk = System.Diagnostics.Process.Start("mkfifo", fifo)!) mk.WaitForExit();

        Assert.ThrowsAny<Exception>(() => AppendLog.Open(_directory, fifo));
    }
}
