namespace ILD.Core.Services.Implementations;

/// <summary>
/// The user <see cref="AuthService"/> creates on its first sign-in when no such
/// user exists yet. Credentials stay environment variables because they are
/// secrets. Without a <see cref="Password"/> there is no bootstrap user.
/// </summary>
public sealed record BootstrapCredentials(string Username, string? Password)
{
    public const string UsernameVariable = "ILD_USERNAME";
    public const string PasswordVariable = "ILD_PASSWORD";
    public const string DefaultUsername = "admin";

    /// <summary>
    /// The username is trimmed, and blank means <see cref="DefaultUsername"/>. The
    /// password is taken as it is.
    /// </summary>
    public static BootstrapCredentials FromEnvironment(Func<string, string?>? readVariable = null)
    {
        var read = readVariable ?? Environment.GetEnvironmentVariable;
        var username = read(UsernameVariable);
        return new BootstrapCredentials(
            string.IsNullOrWhiteSpace(username) ? DefaultUsername : username.Trim(),
            read(PasswordVariable));
    }
}
