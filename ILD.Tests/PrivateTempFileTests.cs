using ILD.Core.Services.Implementations.Files;

namespace ILD.Tests;

public sealed class PrivateTempFileTests
{
    [Fact]
    public async Task The_file_is_owner_only_in_an_owner_only_directory_whatever_the_umask()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "file modes");
        using var file = PrivateTempFile.Create("ild-private-test-");

        await using (var sink = file.OpenWrite())
            await sink.WriteAsync("bundle"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file.Path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(file.Path)!));
        await using var read = file.OpenRead();
        Assert.Equal(6, read.Length);
    }

    [Fact]
    public async Task Disposing_deletes_the_file_and_its_directory()
    {
        var file = PrivateTempFile.Create("ild-private-test-");
        await using (var sink = file.OpenWrite())
            await sink.WriteAsync("x"u8.ToArray(), TestContext.Current.CancellationToken);
        var directory = Path.GetDirectoryName(file.Path)!;

        file.Dispose();

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void The_file_is_made_new_and_never_opened_over_another()
    {
        using var file = PrivateTempFile.Create("ild-private-test-");
        file.OpenWrite().Dispose();

        Assert.Throws<IOException>(() => file.OpenWrite());
    }
}
