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
/// Tests build it from the evidence as found; <see cref="IConnectionTester"/>
/// masks the credential and only then trims and caps it to
/// <see cref="MaxDetailLength"/>, because a cut can leave a fragment no mask matches.
/// </summary>
public sealed record ConnectionTestResult(ConnectionTestOutcome Outcome, string Message, string? Detail)
{
    public const int MaxDetailLength = 1000;

    public bool Ok => Outcome == ConnectionTestOutcome.Ok;
}
