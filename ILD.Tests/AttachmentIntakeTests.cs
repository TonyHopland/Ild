using System.Text;
using ILD.Core.Services.Attachments;
using ILD.WorkItemServer.Services;

namespace ILD.Tests;

/// <summary>
/// The rules that make an uploaded file safe to write and useful to hand an
/// agent: a client-supplied name is never a path, uploads have a ceiling, and a
/// second file with the same name does not overwrite the first.
///
/// The WorkItem server keeps its own copy of the sanitizer (it takes no project
/// references, ADR-0001), so the two are held to the same cases here — that is
/// the only thing stopping them drifting.
/// </summary>
public class AttachmentIntakeTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ild-attachment-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\windows\\system32\\config", "config")]
    [InlineData("/etc/shadow", "shadow")]
    [InlineData("C:\\Users\\tony\\secret.txt", "secret.txt")]
    [InlineData("..", "attachment")]
    [InlineData(".", "attachment")]
    [InlineData("...", "attachment")]
    [InlineData("/", "attachment")]
    [InlineData("", "attachment")]
    [InlineData("   ", "attachment")]
    [InlineData("sketch.png", "sketch.png")]
    [InlineData("My Screenshot (2).PNG", "My Screenshot (2).PNG")]
    [InlineData("re;boot&$(whoami).log", "re_boot__(whoami).log")]
    public void Sanitize_reduces_a_name_to_one_harmless_path_segment(string raw, string expected)
    {
        Assert.Equal(expected, AttachmentIntake.SanitizeFileName(raw));
        // The WorkItem server's independent copy must agree, case for case.
        Assert.Equal(expected, WorkItemAttachmentStore.SanitizeFileName(raw));
    }

    [Fact]
    public void Sanitize_truncates_a_long_name_but_keeps_its_extension()
    {
        var sanitized = AttachmentIntake.SanitizeFileName(new string('a', 400) + ".png");

        Assert.EndsWith(".png", sanitized);
        Assert.True(sanitized.Length <= 120, $"name was {sanitized.Length} chars");
        Assert.Equal(sanitized, WorkItemAttachmentStore.SanitizeFileName(new string('a', 400) + ".png"));
    }

    [Fact]
    public async Task Save_writes_files_inside_the_target_directory_only()
    {
        var saved = await AttachmentIntake.SaveAsync(_dir, [Upload("../../escape.txt", "x")]);

        var attachment = Assert.Single(saved);
        Assert.Equal("escape.txt", attachment.FileName);
        Assert.Equal(Path.Combine(_dir, "escape.txt"), attachment.StoredPath);
        Assert.True(File.Exists(attachment.StoredPath));
        Assert.False(File.Exists(Path.Combine(_dir, "..", "..", "escape.txt")));
    }

    [Fact]
    public async Task Save_keeps_both_files_when_two_uploads_share_a_name()
    {
        var saved = await AttachmentIntake.SaveAsync(
            _dir, [Upload("shot.png", "first"), Upload("shot.png", "second")]);

        Assert.Equal(["shot.png", "shot-2.png"], saved.Select(a => a.FileName));
        Assert.Equal("first", await File.ReadAllTextAsync(saved[0].StoredPath));
        Assert.Equal("second", await File.ReadAllTextAsync(saved[1].StoredPath));
    }

    [Fact]
    public async Task Save_records_size_and_content_type()
    {
        var saved = await AttachmentIntake.SaveAsync(_dir, [Upload("log.txt", "hello", "text/plain")]);

        var attachment = Assert.Single(saved);
        Assert.Equal("text/plain", attachment.ContentType);
        Assert.Equal(5, attachment.SizeBytes);
    }

    [Fact]
    public async Task Save_rejects_an_oversized_file_before_writing_anything()
    {
        var oversized = new UploadedFile(
            "huge.bin", "application/octet-stream", AttachmentIntake.MaxBytesPerFile + 1, new MemoryStream());

        await Assert.ThrowsAsync<AttachmentRejectedException>(
            () => AttachmentIntake.SaveAsync(_dir, [Upload("fine.txt", "ok"), oversized]));

        Assert.False(File.Exists(Path.Combine(_dir, "fine.txt")));
    }

    [Fact]
    public async Task Save_rejects_more_files_than_one_request_may_carry()
    {
        var tooMany = Enumerable.Range(0, AttachmentIntake.MaxFilesPerRequest + 1)
            .Select(i => Upload($"f{i}.txt", "x"))
            .ToList();

        await Assert.ThrowsAsync<AttachmentRejectedException>(() => AttachmentIntake.SaveAsync(_dir, tooMany));
    }

    private static UploadedFile Upload(string name, string content, string? contentType = null)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new UploadedFile(name, contentType, bytes.Length, new MemoryStream(bytes));
    }
}
