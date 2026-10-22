namespace ILD.Data.Enums;

/// <summary>
/// Who merges an AutoMerge pull request. The PR node records
/// <see cref="Native"/> or <see cref="IldPending"/> when it opens the PR; the
/// PR heartbeat moves a pending one through its single merge attempt.
/// </summary>
public enum PrAutoMerge
{
    /// <summary>The provider's own auto-merge is on; ILD never merges.</summary>
    Native,

    /// <summary>The provider's auto-merge could not be turned on; ILD merges once CI is green.</summary>
    IldPending,

    /// <summary>ILD has claimed its one attempt and is merging.</summary>
    IldMerging,

    IldMerged,

    /// <summary>ILD's attempt failed; a human has to merge. Never retried.</summary>
    IldFailed,
}
