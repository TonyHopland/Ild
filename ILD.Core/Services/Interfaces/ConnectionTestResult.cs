namespace ILD.Core.Services.Interfaces;

public enum ConnectionTestOutcome
{
    Ok,
    Unreachable,
    MissingApiKey,
    InvalidApiKey,
    AccessDenied,
    NotFound,
    BranchMissing,
    Misconfigured,
    Error,
}

/// <summary>
/// What a connection test found. <paramref name="Message"/> is one sentence for
/// the user; <paramref name="Detail"/> is the raw evidence it was read from
/// (git's stderr, the forge's status and response text, the exception chain),
/// so a classification that guessed wrong still shows what actually happened.
/// </summary>
public sealed record ConnectionTestResult(ConnectionTestOutcome Outcome, string Message, string? Detail)
{
    public const int MaxDetailLength = 1000;

    public bool Ok => Outcome == ConnectionTestOutcome.Ok;

    private readonly string? _detail = Cap(Detail);

    public string? Detail { get => _detail; init => _detail = Cap(value); }

    private static string? Cap(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return null;
        var trimmed = detail.Trim();
        return trimmed.Length <= MaxDetailLength ? trimmed : trimmed[..(MaxDetailLength - 1)] + "…";
    }
}
