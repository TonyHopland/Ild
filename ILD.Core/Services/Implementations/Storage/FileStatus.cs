using System.Runtime.InteropServices;

namespace ILD.Core.Services.Implementations.Storage;

internal enum FileKind
{
    File,
    Directory,
    Link,
    Other,
}

/// <summary>What the disk holds for one directory entry, the entry itself and never what a link points at.</summary>
internal readonly record struct FileStatusInfo(ulong Device, ulong Inode, uint Links, long AllocatedBytes, FileKind Kind)
{
    public (ulong Device, ulong Inode) Id => (Device, Inode);
}

/// <summary>
/// <c>statx(2)</c> with <c>AT_SYMLINK_NOFOLLOW</c>: the allocated size and inode
/// identity .NET does not expose. Linux only; elsewhere <see cref="IsSupported"/>
/// is false and nothing may be estimated in its place.
/// </summary>
internal static class FileStatus
{
    public const int ENOENT = 2;
    private const int EIO = 5;

    private const int AT_FDCWD = -100;
    private const int AT_SYMLINK_NOFOLLOW = 0x100;
    private const uint STATX_TYPE = 0x1;
    private const uint STATX_NLINK = 0x4;
    private const uint STATX_INO = 0x100;
    private const uint STATX_BLOCKS = 0x400;
    private const uint STATX_BASIC_STATS = 0x7ff;
    private const uint Required = STATX_TYPE | STATX_NLINK | STATX_INO | STATX_BLOCKS;

    private const ushort S_IFMT = 0xF000;
    private const ushort S_IFDIR = 0x4000;
    private const ushort S_IFREG = 0x8000;
    private const ushort S_IFLNK = 0xA000;

    /// <summary><c>struct statx</c> from the kernel uapi: fixed-width fields, the same layout on every architecture.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxBuffer
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint Nlink;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Ino;
        [FieldOffset(48)] public ulong Blocks;
        [FieldOffset(136)] public uint DevMajor;
        [FieldOffset(140)] public uint DevMinor;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(int dirfd, string pathname, int flags, uint mask, out StatxBuffer statxbuf);

    private static readonly Lazy<bool> Supported = new(Probe);

    public static bool IsSupported => Supported.Value;

    private static bool Probe()
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            // Any failure, not only ENOSYS: a sandbox may refuse statx with EPERM,
            // and every figure would then read as an unreadable 0.
            return TryGet("/", out _) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// 0 with <paramref name="status"/> filled in, else the errno — <see cref="ENOENT"/>
    /// when the entry is gone. A filesystem that does not report every field needed
    /// is an error too (<c>EIO</c>), since a figure without them would be a guess.
    /// </summary>
    public static int TryGet(string path, out FileStatusInfo status)
    {
        status = default;
        if (statx(AT_FDCWD, path, AT_SYMLINK_NOFOLLOW, STATX_BASIC_STATS, out var buffer) != 0)
            return Marshal.GetLastPInvokeError();
        if ((buffer.Mask & Required) != Required)
            return EIO;

        var kind = (buffer.Mode & S_IFMT) switch
        {
            S_IFDIR => FileKind.Directory,
            S_IFREG => FileKind.File,
            S_IFLNK => FileKind.Link,
            _ => FileKind.Other,
        };
        status = new FileStatusInfo(
            ((ulong)buffer.DevMajor << 32) | buffer.DevMinor,
            buffer.Ino,
            buffer.Nlink,
            checked((long)buffer.Blocks * 512),
            kind);
        return 0;
    }
}
