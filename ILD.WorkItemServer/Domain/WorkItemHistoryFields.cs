using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ILD.WorkItemServer.Domain;

/// <summary>
/// The work item fields history tracks, by the names history rows carry, and the
/// one way their values are written down: enums by name, Guids in lowercase "D"
/// form, the JSON columns as stored, unset as null. The baseline seed in
/// <see cref="WorkItemHistoryBaseline"/> writes the same encoding in SQL.
/// </summary>
public static class WorkItemHistoryFields
{
    public const string Title = "title";
    public const string Description = "description";
    public const string Status = "status";
    public const string Priority = "priority";
    public const string Tags = "tags";
    public const string Dependencies = "dependencies";
    public const string Repository = "repository";
    public const string AiProviderOverride = "aiProviderOverride";
    public const string AiProviderOverrideId = "aiProviderOverrideId";
    public const string BranchNameOverride = "branchNameOverride";
    public const string BaseBranchOverride = "baseBranchOverride";

    /// <summary>
    /// An event, not a field: a PR linked to the item, or seen merged. Its value is
    /// the PR's URL and is never paired with an old value.
    /// </summary>
    public const string PullRequest = "pullRequest";

    public const string PullRequestLinked = "linked";
    public const string PullRequestMerged = "merged";

    public const int MaxReasonLength = 2000;

    private static readonly (string Field, string Property)[] Tracked =
    {
        (Title, nameof(WorkItem.Title)),
        (Description, nameof(WorkItem.Description)),
        (Status, nameof(WorkItem.Status)),
        (Priority, nameof(WorkItem.Priority)),
        (Tags, nameof(WorkItem.TagsJson)),
        (Dependencies, nameof(WorkItem.DependenciesJson)),
        (Repository, nameof(WorkItem.RepositoryId)),
        (AiProviderOverride, nameof(WorkItem.AiProviderOverride)),
        (AiProviderOverrideId, nameof(WorkItem.AiProviderOverrideId)),
        (BranchNameOverride, nameof(WorkItem.BranchNameOverride)),
        (BaseBranchOverride, nameof(WorkItem.BaseBranchOverride)),
    };

    /// <summary>Every tracked field of an item's values, encoded.</summary>
    public static IReadOnlyList<(string Field, string? Value)> Of(PropertyValues values)
        => Tracked.Select(t => (t.Field, Encode(values[t.Property]))).ToList();

    /// <summary>The tracked fields whose encoded value differs between two sets of an item's values, with the new value.</summary>
    public static IReadOnlyList<(string Field, string? Value)> Changed(PropertyValues before, PropertyValues after)
        => Of(before).Zip(Of(after))
            .Where(pair => pair.First.Value != pair.Second.Value)
            .Select(pair => pair.Second)
            .ToList();

    public static string? Encode(object? value) => value switch
    {
        null => null,
        string s => s,
        Enum e => e.ToString(),
        Guid g => g.ToString("D"),
        _ => throw new ArgumentException($"No history encoding for {value.GetType().Name}.", nameof(value)),
    };

    /// <summary>A reason as it is stored: blank is none, and a long one is cut to fit.</summary>
    public static string? NormalizeReason(string? reason)
        => string.IsNullOrWhiteSpace(reason) ? null
            : reason.Length > MaxReasonLength ? reason[..MaxReasonLength]
            : reason;
}
