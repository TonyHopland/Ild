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
    /// The worktree retention sweeper reclaims a run's worktree/branch and
    /// deletes the run once its work item is Done and the run finished more than
    /// this many days ago. <c>0</c> reclaims it on the next sweep after its work
    /// item is Done.
    /// </summary>
    public const string RunRetentionDays = "run.retentionDays";
    public const int DefaultRunRetentionDays = 0;

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
    /// deletes it. <c>0</c> keeps the log forever, and
    /// <see cref="MaxNetworkLogRetentionDays"/> is the ceiling both the write
    /// path and the read path hold the value to — a longer window than that
    /// cannot be turned into a cutoff date at all.
    /// </summary>
    public const string NetworkLogRetentionDays = "network.logRetentionDays";
    public const int DefaultNetworkLogRetentionDays = 30;
    public const int MaxNetworkLogRetentionDays = 3650;

    /// <summary>Whether a model titles each chat from its first exchange; off by default.</summary>
    public const string ChatSmartTitles = "chat.smartTitles";
    public const bool DefaultChatSmartTitles = false;

    /// <summary>The provider tag chat titles run on, resolved as an AI node's tag is.</summary>
    public const string ChatTitleProviderTag = "chat.titleProviderTag";
    public const string DefaultChatTitleProviderTag = "";

    /// <summary>How many of a chat's messages may each be followed by a title attempt while it is untitled.</summary>
    public const string ChatTitleMaxAttempts = "chat.titleMaxAttempts";
    public const int DefaultChatTitleMaxAttempts = 3;
    public const int MaxChatTitleMaxAttempts = 10;

    /// <summary>Whether unstarred chats with no new message for <see cref="ChatAutoDeleteInactiveDays"/> are deleted; off by default.</summary>
    public const string ChatAutoDeleteInactive = "chat.autoDeleteInactive";
    public const bool DefaultChatAutoDeleteInactive = false;

    /// <summary>
    /// Days since a chat's newest message (or, with none, its creation) after which
    /// it counts as inactive. <see cref="MaxChatAutoDeleteInactiveDays"/> is the
    /// ceiling both the write path and the read path hold the value to.
    /// </summary>
    public const string ChatAutoDeleteInactiveDays = "chat.autoDeleteInactiveDays";
    public const int DefaultChatAutoDeleteInactiveDays = 14;
    public const int MaxChatAutoDeleteInactiveDays = 3650;

    /// <summary>
    /// The most characters a global variable's value may have when it is
    /// created or edited. Lowering it leaves existing values alone; the next
    /// edit of one must fit. The ceiling is the column width.
    /// </summary>
    public const string GlobalVariableMaxLength = "globalVariables.maxLength";
    public const int DefaultGlobalVariableMaxLength = 4000;
    public const int MaxGlobalVariableMaxLength = 8192;
}

/// <summary>
/// Reads scheduler runtime settings from the AppSettings table, with safe defaults.
/// </summary>
public interface ISchedulerSettingsService
{
    Task<int> GetMaxConcurrentAsync(CancellationToken ct = default);
    Task<bool> GetIsPausedAsync(CancellationToken ct = default);

    /// <summary>
    /// Run retention window in days: a run is reclaimed once its work item is Done and it finished more than this many days ago; <c>0</c> means the next sweep after Done.
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
    /// Network log retention window in days, never above
    /// <see cref="AppSettingKeys.MaxNetworkLogRetentionDays"/>; <c>0</c> means
    /// never sweep.
    /// </summary>
    Task<int> GetNetworkLogRetentionDaysAsync(CancellationToken ct = default);

    /// <summary>Whether inactive, unstarred chats are deleted automatically.</summary>
    Task<bool> GetChatAutoDeleteInactiveAsync(CancellationToken ct = default);

    /// <summary>
    /// Days without a new message after which a chat counts as inactive, between 1
    /// and <see cref="AppSettingKeys.MaxChatAutoDeleteInactiveDays"/>.
    /// </summary>
    Task<int> GetChatAutoDeleteInactiveDaysAsync(CancellationToken ct = default);

    /// <summary>
    /// The most characters a global variable's value may have, between 1 and
    /// <see cref="AppSettingKeys.MaxGlobalVariableMaxLength"/>.
    /// </summary>
    Task<int> GetGlobalVariableMaxLengthAsync(CancellationToken ct = default);
}
