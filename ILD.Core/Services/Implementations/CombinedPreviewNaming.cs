namespace ILD.Core.Services.Implementations;

/// <summary>
/// Deterministic naming for a combined ("preview together") integration branch.
/// The key is the member ids sorted and joined with <c>-</c>, so the same
/// selection always maps to the same branch regardless of click order. This
/// mirrors the frontend's <c>integrationBranchName()</c> exactly so the drawer's
/// predicted branch matches what the backend actually creates.
/// </summary>
public static class CombinedPreviewNaming
{
    public static string KeyFor(IEnumerable<string> workItemIds)
        => string.Join("-", workItemIds.OrderBy(id => id, StringComparer.Ordinal));

    public static string BranchFor(IEnumerable<string> workItemIds)
        => $"ild/combined-{KeyFor(workItemIds)}";
}
