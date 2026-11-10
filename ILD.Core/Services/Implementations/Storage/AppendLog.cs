using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ILD.Core.Services.Implementations.Storage;

/// <summary>
/// Opens a log file for append inside ILD's own process without following a link at the final
/// component. The name of the file comes from a worktree the agent wrote, so a link left in the
/// log folder must never carry the write somewhere else.
/// </summary>
internal static class AppendLog
{
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenFile(string pathname, int flags, int mode);

    private const int O_WRONLY = 0x1;

    private static int Flags()
    {
        if (OperatingSystem.IsMacOS())
            return O_WRONLY | 0x200 /*CREAT*/ | 0x8 /*APPEND*/ | 0x100 /*NOFOLLOW*/ | 0x1000000 /*CLOEXEC*/ | 0x4 /*NONBLOCK*/;
        // O_NOFOLLOW differs by architecture: x86 has 0x20000, arm and the generic table 0x8000.
        var noFollow = RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.X86 ? 0x20000 : 0x8000;
        return O_WRONLY | 0x40 /*CREAT*/ | 0x400 /*APPEND*/ | noFollow | 0x80000 /*CLOEXEC*/ | 0x800 /*NONBLOCK*/;
    }

    /// <summary>
    /// The stream for <paramref name="path"/>, created with mode 0600 when missing. Throws
    /// <see cref="InvalidOperationException"/> when the path is outside <paramref name="directory"/>
    /// or is a link or anything but a regular file.
    /// </summary>
    public static FileStream Open(string directory, string path)
    {
        var full = Path.GetFullPath(path);
        var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!string.Equals(Path.GetDirectoryName(full), expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Log path '{path}' is not inside the log folder.");

        if (OperatingSystem.IsWindows())
        {
            if (new FileInfo(full).LinkTarget != null)
                throw new InvalidOperationException($"Log path '{path}' is a link.");
            return new FileStream(full, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        }

        var descriptor = OpenFile(full, Flags(), Convert.ToInt32("600", 8));
        if (descriptor < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            // ELOOP: the final component is a link.
            if (errno == 40 || (OperatingSystem.IsMacOS() && errno == 62))
                throw new InvalidOperationException($"Log path '{path}' is a link.");
            throw new IOException($"Cannot open log '{path}' (errno {errno}).");
        }

        var handle = new SafeFileHandle(descriptor, ownsHandle: true);
        try
        {
            if (FileStatus.IsSupported && (FileStatus.TryGet(full, out var status) != 0 || status.Kind != FileKind.File))
                throw new InvalidOperationException($"Log path '{path}' is a link or not a regular file.");
            return new FileStream(handle, FileAccess.Write, bufferSize: 4096);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }
}
