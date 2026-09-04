using Serilog.Events;

namespace ILD.Api.Configuration;

/// <summary>
/// The level the process started at, from ILD_LOG_LEVEL. The live level is a
/// <see cref="Serilog.Core.LoggingLevelSwitch"/> nobody persists, so this is what
/// it returns to on the next restart — and what tells an override from the default.
/// </summary>
public sealed record StartupLogLevel(LogEventLevel Level);
