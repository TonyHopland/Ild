using ILD.WorkItemServer.Hosting;

namespace ILD.Tests.WorkItemServer;

/// <summary>
/// The gate that keeps a reclaim pass and a backup import from overlapping. The pass that would reset
/// restored heartbeats must never run between the import's commit and the refresh of those heartbeats.
/// </summary>
public sealed class ImportGateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task No_pass_starts_while_an_import_is_in_progress_and_one_can_once_it_is_over()
    {
        var gate = new ImportGate();

        var import = await gate.EnterImportAsync(Ct);
        Assert.Null(gate.TryEnterPass());
        import.Dispose();

        using var pass = gate.TryEnterPass();
        Assert.NotNull(pass);
    }

    [Fact]
    public async Task An_import_waits_for_a_pass_already_running()
    {
        var gate = new ImportGate();
        var pass = gate.TryEnterPass();

        var import = gate.EnterImportAsync(Ct);
        Assert.False(import.IsCompleted);
        pass!.Dispose();

        using var held = await import;
        Assert.Null(gate.TryEnterPass());
    }

    [Fact]
    public async Task Releasing_twice_does_not_open_the_gate_to_a_second_holder()
    {
        var gate = new ImportGate();
        var first = await gate.EnterImportAsync(Ct);
        first.Dispose();
        var second = await gate.EnterImportAsync(Ct);

        first.Dispose();

        Assert.Null(gate.TryEnterPass());
        second.Dispose();
    }
}
