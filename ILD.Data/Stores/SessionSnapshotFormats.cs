using System.Security.Cryptography;

namespace ILD.Data.Stores;

/// <summary>What a stored session snapshot's raw content is, how it is compressed, and how its tail is fingerprinted.</summary>
public static class SessionSnapshotFormats
{
    /// <summary>A Claude Code session file, raw JSONL.</summary>
    public const string ClaudeJsonl = "claude-jsonl";

    /// <summary>A pi session file, raw JSONL.</summary>
    public const string PiJsonl = "pi-jsonl";

    /// <summary>A codex rollout file, raw JSONL.</summary>
    public const string CodexJsonl = "codex-jsonl";

    /// <summary>The JSON <c>opencode export</c> prints.</summary>
    public const string OpenCodeJson = "opencode-json";

    /// <summary>Content the content migration could not interpret: kept as it was, never restored.</summary>
    public const string Raw = "raw";

    /// <summary>The codec chunk data is compressed with.</summary>
    public const string Brotli = "br";

    /// <summary>How many trailing raw bytes <see cref="TailHash"/> covers at most.</summary>
    public const int TailWindow = 4096;

    /// <summary>The fingerprint of the last (up to <see cref="TailWindow"/>) raw bytes stored: lowercase hex SHA-256.</summary>
    public static string TailHash(ReadOnlySpan<byte> tail) => Convert.ToHexStringLower(SHA256.HashData(tail));
}
