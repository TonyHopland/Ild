namespace ILD.Core.Services.Implementations;

/// <summary>
/// A human answer was not delivered because the run it names is not the work
/// item's active run waiting on a person. Nothing was written. The message is
/// for the person who sent the answer.
/// </summary>
public sealed class HumanFeedbackRefusedException : InvalidOperationException
{
    public HumanFeedbackRefusedException(string message) : base(message)
    {
    }
}
