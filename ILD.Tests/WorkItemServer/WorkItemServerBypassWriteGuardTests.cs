using System.Text.RegularExpressions;

namespace ILD.Tests.WorkItemServer;

/// <summary>
/// Work item history is captured by the DbContext's SaveChanges, and a bulk
/// write (ExecuteUpdate, ExecuteDelete, raw SQL) goes around it. The old value of
/// a change is read off the previous history row, so one bulk write to WorkItems
/// without its own history rows makes every later change of that field show the
/// wrong "before". This pins every bulk write in the WorkItem server per file, so
/// a new one cannot land without someone deciding whether it needs history.
/// </summary>
public class WorkItemServerBypassWriteGuardTests
{
    // Each of these has been checked: the WorkItems writes among them (the Running
    // claim and the PR record in WorkItemService, the applied edit proposal in
    // WorkItemEditProposalService) write their history explicitly; WorkItemRows'
    // claim is a row lock that changes nothing; the rest touch other tables.
    private static readonly Dictionary<string, int> Pinned = new(StringComparer.Ordinal)
    {
        ["Services/WorkItemAttachmentService.cs"] = 1,
        ["Services/WorkItemEditProposalService.cs"] = 4,
        ["Services/WorkItemRows.cs"] = 1,
        ["Services/WorkItemService.cs"] = 2,
    };

    private static readonly Regex BulkWrite =
        new(@"\.\s*Execute(?:Update|Delete|Sql\w*?)(?:Async)?\s*[(<]", RegexOptions.Compiled);

    private static readonly Regex Comments =
        new(@"/\*.*?\*/|//[^\n]*", RegexOptions.Compiled | RegexOptions.Singleline);

    [Fact]
    public void Every_bulk_write_in_the_WorkItem_server_is_pinned()
    {
        var root = Path.Combine(RepositoryRoot(), "ILD.WorkItemServer");
        var found = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(f => !f.Split('/').Any(part => part is "obj" or "bin" or "Migrations"))
            .ToDictionary(
                f => f,
                f => BulkWrite.Matches(Comments.Replace(File.ReadAllText(Path.Combine(root, f)), " ")).Count,
                StringComparer.Ordinal)
            .Where(e => e.Value > 0)
            .ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);

        var drift = Pinned.Keys.Union(found.Keys)
            .Order(StringComparer.Ordinal)
            .Where(f => Pinned.GetValueOrDefault(f) != found.GetValueOrDefault(f))
            .Select(f => $"ILD.WorkItemServer/{f}: pinned {Pinned.GetValueOrDefault(f)}, found {found.GetValueOrDefault(f)}")
            .ToList();

        Assert.True(drift.Count == 0,
            "ExecuteUpdate/ExecuteDelete/ExecuteSql* calls changed. They skip the change tracker, so the history "
            + "interceptor never sees them. If a call writes WorkItems, add its history writes in the same "
            + "transaction (only when it changed a row); then update the pinned list in "
            + nameof(WorkItemServerBypassWriteGuardTests) + ".\n  " + string.Join("\n  ", drift));
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ILD.sln"))) return dir.FullName;
        }
        throw new InvalidOperationException($"no ILD.sln above {AppContext.BaseDirectory}");
    }
}
