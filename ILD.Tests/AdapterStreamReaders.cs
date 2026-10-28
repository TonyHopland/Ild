using System.Text;

namespace ILD.Tests;

/// <summary>A CLI's stdout as the parsers read it, without a process behind it.</summary>
internal static class AdapterStreamReaders
{
    public static StreamReader Of(params string[] lines)
        => new(new MemoryStream(Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n")))));
}
