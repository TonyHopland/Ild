namespace ILD.Core.Services.Implementations;

public class AiProviderException : Exception
{
    public AiProviderException(string message) : base(message) { }
    public AiProviderException(string message, Exception inner) : base(message, inner) { }
}
