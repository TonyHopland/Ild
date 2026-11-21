namespace ILD.Core.Services.Implementations;

/// <summary>
/// A text answer (Respond) was sent with no text. Nothing was written and the run
/// still waits; the message is for the person who sent it.
/// </summary>
public sealed class BlankHumanAnswerException : InvalidOperationException
{
    public BlankHumanAnswerException() : base("Type an answer before sending.")
    {
    }
}
