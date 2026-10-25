using System.Buffers.Binary;
using System.Security.Cryptography;
using ILD.Core.Services.Backup;

namespace ILD.Tests;

public sealed class BackupCipherTests
{
    private const string Pass = "correct horse battery";
    private const int Chunk = 64 * 1024;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<byte[]> EncryptAsync(byte[] plain)
    {
        var file = new MemoryStream();
        await using (var encrypting = await BackupCipher.OpenWriteAsync(file, Pass, "core", Ct))
        {
            await encrypting.WriteAsync(plain, Ct);
            await encrypting.CompleteAsync(Ct);
        }
        return file.ToArray();
    }

    private static async Task<byte[]> DecryptAsync(byte[] file, string passphrase = Pass)
    {
        await using var plain = await BackupCipher.OpenReadAsync(new MemoryStream(file), passphrase, "core", Ct);
        var result = new MemoryStream();
        await plain.CopyToAsync(result, Ct);
        return result.ToArray();
    }

    /// <summary>
    /// The refusal a bad file gets. Its type is compiled into the WorkItem Server's assembly as well, which
    /// this project also references, so it is recognised by name.
    /// </summary>
    private static async Task<Exception> RefusedAsync(Func<Task> open)
    {
        var refused = await Assert.ThrowsAnyAsync<Exception>(open);
        Assert.Equal("BackupRefusedException", refused.GetType().Name);
        return refused;
    }

    /// <summary>The offset and length of each sealed chunk, length prefix included.</summary>
    private static List<(int Start, int Length)> Chunks(byte[] file)
    {
        var offset = 7 + 4 + BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(7));
        var chunks = new List<(int, int)>();
        while (offset < file.Length)
        {
            var length = 4 + BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(offset));
            chunks.Add((offset, length));
            offset += length;
        }
        return chunks;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Chunk)]
    [InlineData(Chunk + 1)]
    [InlineData(3 * Chunk + 17)]
    public async Task What_goes_in_comes_out_whatever_its_length(int length)
    {
        var plain = RandomNumberGenerator.GetBytes(length);

        Assert.Equal(plain, await DecryptAsync(await EncryptAsync(plain)));
    }

    [Fact]
    public async Task A_file_cut_at_a_chunk_boundary_is_refused()
    {
        var file = await EncryptAsync(RandomNumberGenerator.GetBytes(3 * Chunk));
        var last = Chunks(file).Last();

        await RefusedAsync(() => DecryptAsync(file[..last.Start]));
    }

    [Fact]
    public async Task Chunks_swapped_or_bytes_added_after_the_end_are_refused()
    {
        var file = await EncryptAsync(RandomNumberGenerator.GetBytes(3 * Chunk));
        var chunks = Chunks(file);
        var swapped = file[..chunks[0].Start]
            .Concat(file.AsSpan(chunks[1].Start, chunks[1].Length).ToArray())
            .Concat(file.AsSpan(chunks[0].Start, chunks[0].Length).ToArray())
            .Concat(file[(chunks[1].Start + chunks[1].Length)..]).ToArray();

        await RefusedAsync(() => DecryptAsync(swapped));
        await RefusedAsync(() => DecryptAsync([.. file, 0]));
    }

    [Fact]
    public async Task A_backup_of_another_database_is_refused_before_the_passphrase_is_tried()
    {
        var file = await EncryptAsync([1, 2, 3]);

        var refused = await RefusedAsync(
            () => BackupCipher.OpenReadAsync(new MemoryStream(file), "wrong, even so", "workitems", Ct));

        Assert.Contains("'core'", refused.Message);
    }

    [Fact]
    public async Task A_wrong_passphrase_is_refused_as_such()
    {
        var file = await EncryptAsync([1, 2, 3]);

        var refused = await RefusedAsync(() => DecryptAsync(file, "another passphrase!"));

        Assert.Contains("passphrase", refused.Message);
    }
}
