namespace ILD.Data.DTOs;

/// <summary>How an agent run gets the credential for its model (ADR-0031).</summary>
public enum CredentialPath
{
    /// <summary>The CLI holds a placeholder and the model gateway adds the real credential: the agent cannot read it.</summary>
    Gateway,

    /// <summary>The CLI is given a read-only copy of the credential for that run: the agent can read it, but not change the shared store.</summary>
    ReadOnlyCopy,
}
