using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ILD.Data.Backup;

namespace ILD.Core.Services.Backup;

/// <summary>What a passphrase has to be for a backup to be made with it.</summary>
public static class PassphrasePolicy
{
    public const int MinLength = 12;
}

/// <summary>
/// The file a person keeps: "ILDBAK1", a header, then the archive in AES-256-GCM chunks under a key
/// PBKDF2-SHA256 derives from the passphrase (600,000 iterations, fresh salt per file). Nothing about
/// the content is readable without the passphrase, not even which tables it holds.
///
/// <para>Each chunk is authenticated with its position and whether it is the last, and the header is
/// authenticated with every chunk, so a file that was cut short, reordered or altered anywhere fails
/// rather than restoring something other than what was backed up. A wrong passphrase fails on the
/// first chunk, before anything has been read out.</para>
/// </summary>
public static class BackupCipher
{
    private const string Magic = "ILDBAK1";
    private const string Kdf = "pbkdf2-sha256";
    private const int Iterations = 600_000;
    private const int MaxIterations = 10_000_000;
    private const int ChunkSize = 64 * 1024;
    private const int MaxHeaderBytes = 4096;
    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int PrefixBytes = NonceBytes - sizeof(uint);
    private const int TagBytes = 16;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal sealed record Header(string Kdf, int Iterations, byte[] Salt, byte[] NoncePrefix, int ChunkSize, string Database);

    internal sealed record Sealed(Header Header, byte[] HeaderHash, byte[] Key);

    /// <summary>The bytes written to <paramref name="output"/> through the returned stream are encrypted; call <see cref="EncryptingStream.CompleteAsync"/> when done.</summary>
    public static async Task<EncryptingStream> OpenWriteAsync(Stream output, string passphrase, string database, CancellationToken ct)
    {
        var header = new Header(Kdf, Iterations, RandomNumberGenerator.GetBytes(SaltBytes),
            RandomNumberGenerator.GetBytes(PrefixBytes), ChunkSize, database);
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, Json);
        var key = await DeriveKeyAsync(passphrase, header);

        var head = new byte[Magic.Length + 4 + headerBytes.Length];
        Encoding.ASCII.GetBytes(Magic, head);
        BinaryPrimitives.WriteInt32BigEndian(head.AsSpan(Magic.Length), headerBytes.Length);
        headerBytes.CopyTo(head.AsSpan(Magic.Length + 4));
        await output.WriteAsync(head, ct);

        return new EncryptingStream(output, new Sealed(header, SHA256.HashData(head), key));
    }

    /// <summary>
    /// A stream of the archive. Refuses a file that is not a backup of <paramref name="database"/> and a
    /// wrong passphrase before returning; anything wrong further in surfaces as the stream is read.
    /// </summary>
    public static async Task<Stream> OpenReadAsync(Stream encrypted, string passphrase, string database, CancellationToken ct)
    {
        var magic = new byte[Magic.Length];
        var lengthBytes = new byte[4];
        byte[] headerBytes;
        Header header;
        try
        {
            await encrypted.ReadExactlyAsync(magic, ct);
            if (Encoding.ASCII.GetString(magic) != Magic) throw BackupRefusedException.NotABackup();
            await encrypted.ReadExactlyAsync(lengthBytes, ct);
            var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
            if (length is <= 0 or > MaxHeaderBytes) throw BackupRefusedException.NotABackup();
            headerBytes = new byte[length];
            await encrypted.ReadExactlyAsync(headerBytes, ct);
            header = JsonSerializer.Deserialize<Header>(headerBytes, Json) ?? throw BackupRefusedException.NotABackup();
        }
        catch (Exception ex) when (ex is EndOfStreamException or JsonException)
        {
            throw BackupRefusedException.NotABackup();
        }

        if (header.Kdf != Kdf || header.Iterations is < 1 or > MaxIterations || header.Salt?.Length != SaltBytes
            || header.NoncePrefix?.Length != PrefixBytes || header.ChunkSize is < 1 or > ChunkSize || header.Database is null)
            throw BackupRefusedException.NotABackup();
        if (header.Database != database)
            throw new BackupRefusedException($"This is a backup of the '{header.Database}' database, not of '{database}'.");

        var head = new byte[Magic.Length + 4 + headerBytes.Length];
        Encoding.ASCII.GetBytes(Magic, head);
        lengthBytes.CopyTo(head.AsSpan(Magic.Length));
        headerBytes.CopyTo(head.AsSpan(Magic.Length + 4));
        var stream = new DecryptingStream(encrypted, new Sealed(header, SHA256.HashData(head), await DeriveKeyAsync(passphrase, header)));
        await stream.ReadFirstChunkAsync(ct);
        return stream;
    }

    private static Task<byte[]> DeriveKeyAsync(string passphrase, Header header)
        => Task.Run(() => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase), header.Salt, header.Iterations, HashAlgorithmName.SHA256, 32));

    private static byte[] Nonce(Sealed seal, uint counter)
    {
        var nonce = new byte[NonceBytes];
        seal.Header.NoncePrefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(PrefixBytes), counter);
        return nonce;
    }

    private static byte[] AssociatedData(Sealed seal, uint counter, bool last)
    {
        var data = new byte[seal.HeaderHash.Length + sizeof(uint) + 1];
        seal.HeaderHash.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(seal.HeaderHash.Length), counter);
        data[^1] = last ? (byte)1 : (byte)0;
        return data;
    }

    public sealed class EncryptingStream : ForwardOnlyStream
    {
        private readonly Stream _output;
        private readonly Sealed _seal;
        private readonly byte[] _buffer = new byte[ChunkSize];
        private int _filled;
        private uint _counter;
        private bool _completed;

        internal EncryptingStream(Stream output, Sealed seal) : base(canRead: false, canWrite: true)
        {
            _output = output;
            _seal = seal;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            while (data.Length > 0)
            {
                // A full buffer waits until more arrives: the last chunk is the one Complete writes.
                if (_filled == _buffer.Length) await EmitAsync(last: false, ct);
                var take = Math.Min(data.Length, _buffer.Length - _filled);
                data[..take].CopyTo(_buffer.AsMemory(_filled));
                _filled += take;
                data = data[take..];
            }
        }

        /// <summary>Writes the last chunk. A stream that is not completed leaves a file that cannot be restored.</summary>
        public async Task CompleteAsync(CancellationToken ct)
        {
            if (_completed) return;
            _completed = true;
            await EmitAsync(last: true, ct);
        }

        private async Task EmitAsync(bool last, CancellationToken ct)
        {
            var sealedChunk = new byte[_filled + TagBytes];
            using (var aes = new AesGcm(_seal.Key, TagBytes))
                aes.Encrypt(Nonce(_seal, _counter), _buffer.AsSpan(0, _filled), sealedChunk.AsSpan(0, _filled),
                    sealedChunk.AsSpan(_filled), AssociatedData(_seal, _counter, last));
            var length = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, sealedChunk.Length);
            await _output.WriteAsync(length, ct);
            await _output.WriteAsync(sealedChunk, ct);
            _filled = 0;
            _counter++;
        }

    }

    private sealed class DecryptingStream(Stream input, Sealed seal) : ForwardOnlyStream(canRead: true, canWrite: false)
    {
        private byte[] _chunk = [];
        private int _position;
        private uint _counter;
        private bool _last;

        public async Task ReadFirstChunkAsync(CancellationToken ct)
        {
            try
            {
                await NextChunkAsync(ct);
            }
            catch (CryptographicException)
            {
                throw new BackupRefusedException("The passphrase is wrong, or the backup file is damaged.");
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct = default)
        {
            if (destination.Length == 0) return 0;
            while (_position == _chunk.Length)
            {
                if (_last) return 0;
                try { await NextChunkAsync(ct); }
                catch (CryptographicException) { throw Damaged(); }
            }

            var take = Math.Min(destination.Length, _chunk.Length - _position);
            _chunk.AsMemory(_position, take).CopyTo(destination);
            _position += take;
            return take;
        }

        private async Task NextChunkAsync(CancellationToken ct)
        {
            var lengthBytes = new byte[4];
            try { await input.ReadExactlyAsync(lengthBytes, ct); }
            catch (EndOfStreamException) { throw Damaged(); }
            var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
            if (length is < TagBytes || length > ChunkSize + TagBytes) throw Damaged();
            var sealedChunk = new byte[length];
            try { await input.ReadExactlyAsync(sealedChunk, ct); }
            catch (EndOfStreamException) { throw Damaged(); }

            // Whether this is the last chunk is part of what its tag covers, so try each reading
            // of it rather than trusting anything in the file to say.
            var plain = new byte[length - TagBytes];
            var last = false;
            using var aes = new AesGcm(seal.Key, TagBytes);
            try
            {
                aes.Decrypt(Nonce(seal, _counter), sealedChunk.AsSpan(0, plain.Length), sealedChunk.AsSpan(plain.Length), plain,
                    AssociatedData(seal, _counter, false));
            }
            catch (CryptographicException)
            {
                last = true;
                aes.Decrypt(Nonce(seal, _counter), sealedChunk.AsSpan(0, plain.Length), sealedChunk.AsSpan(plain.Length), plain,
                    AssociatedData(seal, _counter, true));
            }

            if (last && await input.ReadAsync(new byte[1], ct) != 0) throw Damaged();
            _chunk = plain;
            _position = 0;
            _last = last;
            _counter++;
        }

        private static BackupRefusedException Damaged()
            => new("The backup file is damaged, cut short or has been altered.");

    }
}
