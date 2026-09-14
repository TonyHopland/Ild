using ILD.WorkItemServer.Services;

namespace ILD.Tests.WorkItemServer;

/// <summary>
/// The store's delete paths are best-effort by contract: once a work item row is
/// gone nothing will ever ask for its bytes again, so a failure is recorded and
/// swallowed rather than raised.
///
/// <para>
/// The logger is absent in every construction but the server's own, so the
/// logging must not be the thing that breaks the swallowing.
/// <c>WorkItemService.DeleteAsync</c> calls <see cref="WorkItemAttachmentStore.DeleteAll"/>
/// after the row is committed and before the dependency scrub, so a throw there
/// would fail a delete that had already succeeded and skip the scrub that keeps
/// dangling references from wedging dependents.
/// </para>
/// </summary>
public sealed class WorkItemAttachmentStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ild-store-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The ceiling is enforced against what actually arrives, not against what the
    /// request claimed. Both callers check a declared length first, but a declared
    /// length is the client's claim about the body — the store owns the constant,
    /// so it owns the guarantee.
    /// </summary>
    [Fact]
    public async Task An_upload_past_the_ceiling_is_refused_even_when_its_declared_length_lied()
    {
        var store = new WorkItemAttachmentStore(_root);
        var oversized = new MemoryStream(new byte[WorkItemAttachmentStore.MaxBytesPerFile + 1]);

        await Assert.ThrowsAsync<AttachmentTooLargeException>(
            () => store.SaveAsync("42", "a1", oversized));

        // Refused *and* not occupying the disk it was refused for.
        Assert.Null(store.Open("42", "a1"));
    }

    [Fact]
    public async Task An_upload_at_the_ceiling_is_still_accepted()
    {
        var store = new WorkItemAttachmentStore(_root);

        var written = await store.SaveAsync(
            "42", "a1", new MemoryStream(new byte[WorkItemAttachmentStore.MaxBytesPerFile]));

        Assert.Equal(WorkItemAttachmentStore.MaxBytesPerFile, written);
    }

    [Fact]
    public async Task A_delete_that_cannot_remove_the_bytes_stays_silent_without_a_logger()
    {
        // No logger, exactly as every caller outside the server constructs it.
        var store = new WorkItemAttachmentStore(_root);
        await store.SaveAsync("42", "a1", new MemoryStream([1]));

        // A directory standing where the file should be: unlink refuses it, which
        // is the failure these catch blocks exist to absorb.
        var path = Path.Combine(_root, "attachments", "42", "a1");
        File.Delete(path);
        Directory.CreateDirectory(path);

        Assert.Null(Record.Exception(() => store.Delete("42", "a1")));
    }

    [Fact]
    public async Task A_delete_that_cannot_remove_the_bytes_still_reports_through_a_logger()
    {
        var log = new CountingLogger();
        var store = new WorkItemAttachmentStore(_root, log);
        await store.SaveAsync("42", "a1", new MemoryStream([1]));

        var path = Path.Combine(_root, "attachments", "42", "a1");
        File.Delete(path);
        Directory.CreateDirectory(path);

        store.Delete("42", "a1");

        // Swallowed, but not silently: orphaned bytes have to be diagnosable.
        Assert.Equal(1, log.Warnings);
    }

    private sealed class CountingLogger : Microsoft.Extensions.Logging.ILogger<WorkItemAttachmentStore>
    {
        public int Warnings { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning) Warnings++;
        }
    }
}
