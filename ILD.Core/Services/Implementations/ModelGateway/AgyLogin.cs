namespace ILD.Core.Services.Implementations.ModelGateway;

/// <summary>
/// The files the Antigravity CLI writes its Google sign-in to when it has no keyring, relative to the home, as seen written by
/// agy 1.5.16 after a sign-in in a provider terminal. The terminal import (<see cref="CredentialImport"/>) and the copy a run is
/// given both read this list, so neither depends on the other's namespace.
/// </summary>
public static class AgyLogin
{
    /// <summary>JSON with a <c>token</c> key (and <c>auth_method</c>, <c>id_token_email</c>): the sign-in itself.</summary>
    public const string TokenFile = ".gemini/antigravity-cli/antigravity-oauth-token";

    /// <summary>The account the token belongs to. Of no use without <see cref="TokenFile"/>.</summary>
    public const string ScopeFile = ".config/antigravity/credential-user-scope";

    public static readonly IReadOnlyList<string> Files = [TokenFile, ScopeFile];
}
