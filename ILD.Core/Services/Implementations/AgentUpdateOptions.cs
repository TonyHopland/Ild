using System.Globalization;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// How the managed coding agents update themselves. Read once at startup, like
/// <see cref="Attachments.AttachmentLimits"/>: the background updater sizes its check interval
/// from it, and it is a deployment decision rather than something to flip at runtime.
/// </summary>
/// <param name="CooldownDays">
/// Days a version must have been published before it is installed automatically; <c>0</c>
/// installs the current latest at once.
/// </param>
public sealed record AgentUpdateOptions(int CooldownDays = AgentUpdateOptions.DefaultCooldownDays)
{
    public const string CooldownDaysVariable = "ILD_AGENT_UPDATE_COOLDOWN_DAYS";

    public const int DefaultCooldownDays = 1;
    public const int MaxCooldownDays = 365;

    /// <summary>
    /// Reads the cooldown as whole days from 0 to <see cref="MaxCooldownDays"/>. A missing value
    /// is the default; an invalid or out-of-range one is the default too, with a warning, so a
    /// typo cannot silently shorten the wait before a release is installed.
    /// </summary>
    public static AgentUpdateOptions FromEnvironment(ILogger? log = null, Func<string, string?>? readVariable = null)
    {
        var raw = (readVariable ?? Environment.GetEnvironmentVariable)(CooldownDaysVariable);
        if (string.IsNullOrWhiteSpace(raw)) return new AgentUpdateOptions();

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days)
            && days is >= 0 and <= MaxCooldownDays)
        {
            return new AgentUpdateOptions(days);
        }

        log?.LogWarning(
            "{Variable} is '{Value}', which is not a whole number from 0 to {Max}; using {Default} day(s)",
            CooldownDaysVariable, raw, MaxCooldownDays, DefaultCooldownDays);
        return new AgentUpdateOptions();
    }
}
