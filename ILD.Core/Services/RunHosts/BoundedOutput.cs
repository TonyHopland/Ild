namespace ILD.Core.Services.RunHosts;

/// <summary>Reads what a command prints for a log or a console, keeping the first part and dropping the rest, so a command that never stops printing cannot grow ILD's memory.</summary>
internal static class BoundedOutput
{
    public const string Marker = "\n…output truncated\n";

    /// <summary>
    /// Reads <paramref name="reader"/> to its end in blocks (a line without a newline is bounded too). The first
    /// <paramref name="limit"/> characters go to <paramref name="keep"/>, then <see cref="Marker"/> once; the rest is read
    /// and thrown away, so the command is never blocked on a full pipe.
    /// </summary>
    public static async Task PumpAsync(TextReader reader, long limit, Func<string, Task> keep, CancellationToken ct = default)
    {
        var buffer = new char[8 * 1024];
        long kept = 0;
        var marked = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            var take = (int)Math.Min(read, limit - kept);
            if (take > 0)
            {
                kept += take;
                await keep(new string(buffer, 0, take));
            }
            if (take < read && !marked)
            {
                marked = true;
                await keep(Marker);
            }
        }
    }
}
