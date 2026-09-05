namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Well-known <see cref="ILD.Data.Entities.AppSetting"/> keys.
/// </summary>
public static class AppSettingKeys
{
    public const string SchedulerMaxConcurrent = "scheduler.maxConcurrent";
    public const string SchedulerIsPaused = "scheduler.isPaused";

    public const int DefaultMaxConcurrent = 5;
    public const bool DefaultIsPaused = false;

    /// <summary>
    /// Days after a run reaches a terminal state before the worktree retention
    /// sweeper reclaims its worktree/branch and deletes the run. <c>0</c>
    /// disables automatic reclamation entirely.
    /// </summary>
    public const string RunRetentionDays = "run.retentionDays";
    public const int DefaultRunRetentionDays = 30;

    // WorkItem server connection settings (previously stored per RemoteProvider).
    public const string WorkItemServerUrl = "workItemServer.url";
    public const string WorkItemServerApiKey = "workItemServer.apiKey";
    public const string WorkItemServerPollIntervalSeconds = "workItemServer.pollIntervalSeconds";
    public const string WorkItemServerGraceIntervalSeconds = "workItemServer.graceIntervalSeconds";

    public const int DefaultPollIntervalSeconds = 60;
    public const int DefaultGraceIntervalSeconds = 5;

    /// <summary>
    /// Seconds between PR heartbeat poller passes. The poller fetches a fresh PR
    /// snapshot for every run parked at a PR node awaiting merge.
    /// </summary>
    public const string PrHeartbeatSeconds = "pr.heartbeatSeconds";
    public const int DefaultPrHeartbeatSeconds = 60;

    /// <summary>
    /// How many AI nodes a run may execute between human interactions before
    /// the engine parks it for a person. Counted per run and reset by every
    /// human touch, so a conversational graph that alternates AI and Human
    /// never approaches it — only a graph looping through AI nodes on its own
    /// does. <c>0</c> is not allowed; the run would never get to start.
    /// </summary>
    public const string MaxAiTraversals = "ai.maxTraversals";
    public const int DefaultMaxAiTraversals = 25;

    /// <summary>
    /// Whether ILD retries a run parked by a Provider Interruption
    /// (<c>HaltReason.Throttled</c>) on its own, rather than leaving it for a
    /// person to Resume. Off by default, and a run whose automatic retries run
    /// out parks for a human anyway — which is what happens for every such park
    /// while this is off.
    /// </summary>
    public const string ThrottleAutoResume = "throttle.autoResume";
    public const bool DefaultThrottleAutoResume = false;

    /// <summary>
    /// Minutes a Provider Interruption park waits before each automatic retry.
    /// A reset time stated in the provider's own notice can push an attempt
    /// later than this, never earlier.
    /// </summary>
    public const string ThrottleRetryDelayMinutes = "throttle.retryDelayMinutes";
    public const int DefaultThrottleRetryDelayMinutes = 60;

    /// <summary>
    /// Automatic retries a run may spend before it is left parked for a person,
    /// counted — like <see cref="MaxAiTraversals"/> — since a human last touched
    /// the run.
    /// </summary>
    public const string ThrottleMaxRetries = "throttle.maxRetries";
    public const int DefaultThrottleMaxRetries = 6;

    /// <summary>
    /// Days a sign-in survives without being used. Re-evaluated on every
    /// request, so lowering it signs idle devices out immediately. <c>0</c>
    /// disables idle expiry.
    /// </summary>
    public const string SessionIdleDays = "session.idleDays";
    public const int DefaultSessionIdleDays = 30;

    /// <summary>
    /// Days a sign-in survives however active it is. Fixed when the session is
    /// created, so a change applies to sign-ins made after it. <c>0</c> disables
    /// the absolute cap.
    /// </summary>
    public const string SessionMaxDays = "session.maxDays";
    public const int DefaultSessionMaxDays = 90;

    /// <summary>
    /// How the egress proxy judges an agent connection: <c>off</c> records every
    /// destination and allows it, <c>whitelist</c> allows only listed hosts,
    /// <c>blacklist</c> blocks only listed hosts. The lists themselves live in
    /// <c>NetworkPolicyEntries</c>, not in a setting value.
    /// </summary>
    public const string NetworkMode = "network.mode";
    public const string DefaultNetworkMode = "off";

    /// <summary>
    /// Days a destination stays in the network log before the retention sweeper
    /// deletes it. <c>0</c> keeps the log forever.
    /// </summary>
    public const string NetworkLogRetentionDays = "network.logRetentionDays";
    public const int DefaultNetworkLogRetentionDays = DefaultRunRetentionDays;
}

/// <summary>
/// Reads scheduler runtime settings from the AppSettings table, with safe defaults.
/// </summary>
public interface ISchedulerSettingsService
{
    Task<int> GetMaxConcurrentAsync(CancellationToken ct = default);
    Task<bool> GetIsPausedAsync(CancellationToken ct = default);

    /// <summary>
    /// Run retention window in days; <c>0</c> means never auto-reclaim.
    /// </summary>
    Task<int> GetRunRetentionDaysAsync(CancellationToken ct = default);

    /// <summary>Seconds between PR heartbeat poller passes (minimum 1).</summary>
    Task<int> GetPrHeartbeatSecondsAsync(CancellationToken ct = default);

    /// <summary>AI nodes a run may execute between human interactions (minimum 1).</summary>
    Task<int> GetMaxAiTraversalsAsync(CancellationToken ct = default);

    /// <summary>Whether ILD retries a Provider Interruption park itself instead of a person.</summary>
    Task<bool> GetThrottleAutoResumeAsync(CancellationToken ct = default);

    /// <summary>Minutes between automatic retries of a Provider Interruption park (minimum 1).</summary>
    Task<int> GetThrottleRetryDelayMinutesAsync(CancellationToken ct = default);

    /// <summary>Automatic retries a run may spend before it parks for a person (minimum 1).</summary>
    Task<int> GetThrottleMaxRetriesAsync(CancellationToken ct = default);

    /// <summary>
    /// Network log retention window in days; <c>0</c> means never sweep.
    /// </summary>
    Task<int> GetNetworkLogRetentionDaysAsync(CancellationToken ct = default);
}
