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

    /// <summary>
    /// A sanitized name has to be a legal multipart part name, not just a legal
    /// file name: the work-item leg forwards it to the WorkItem server as a form
    /// part, and <see cref="MultipartFormDataContent"/> throws on a name it cannot
    /// put in a Content-Disposition header (a quote does exactly that) — which
    /// would surface as a 500 on an ordinary attachment rather than an upload.
    /// </summary>
    [Theory]
    [InlineData("my\"quoted\".png")]
    [InlineData("back\\slash.png")]
    [InlineData("new\nline.png")]
    [InlineData("../../etc/passwd")]
    [InlineData("board shot.png")]
    public void A_sanitized_name_is_always_a_legal_multipart_part_name(string raw)
    {
        var sanitized = AttachmentIntake.SanitizeFileName(raw);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("x"), "file", sanitized);

        Assert.Equal(sanitized, WorkItemAttachmentStore.SanitizeFileName(raw));
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

    /// <summary>
    /// The upload directory sits inside a tree the agent can reach, so it could
    /// plant a symlink where an upload is about to land. Following it would let
    /// the agent choose a file for the orchestrator to overwrite, which is why
    /// the create is <c>O_CREAT|O_EXCL</c> rather than a check followed by a
    /// truncating open.
    /// </summary>
    [Fact]
    public async Task An_upload_does_not_follow_a_symlink_planted_at_its_name()
    {
        Directory.CreateDirectory(_dir);
        var elsewhere = Path.Combine(Path.GetTempPath(), $"ild-attachment-target-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(elsewhere, "do not overwrite me");
        File.CreateSymbolicLink(Path.Combine(_dir, "sketch.png"), elsewhere);

        try
        {
            var saved = await AttachmentIntake.SaveAsync(_dir, [Upload("sketch.png", "pixels")]);

            Assert.Equal("do not overwrite me", await File.ReadAllTextAsync(elsewhere));
            var attachment = Assert.Single(saved);
            Assert.NotEqual(Path.Combine(_dir, "sketch.png"), attachment.StoredPath);
            Assert.Equal("pixels", await File.ReadAllTextAsync(attachment.StoredPath));
        }
        finally
        {
            File.Delete(elsewhere);
        }
    }

    [Fact]
    public async Task A_failed_write_leaves_no_half_upload_behind()
    {
        var failing = new UploadedFile("broken.bin", "application/octet-stream", 8, new ThrowingStream());

        await Assert.ThrowsAsync<IOException>(() => AttachmentIntake.SaveAsync(_dir, [failing]));

        // Nothing references a partial file, so it would sit there forever.
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task A_failed_upload_takes_the_files_that_already_landed_with_it()
    {
        var failing = new UploadedFile("broken.bin", "application/octet-stream", 8, new ThrowingStream());

        await Assert.ThrowsAnyAsync<Exception>(
            () => AttachmentIntake.SaveAsync(_dir, [Upload("landed.txt", "ok"), failing]));

        // The caller only ever receives the whole list, so a file written before
        // the failure is referenced by nothing — and retrying would leave a
        // suffixed duplicate of it beside the real one.
        Assert.Empty(Directory.GetFiles(_dir));
    }

    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("boom");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
