using System.Diagnostics;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Implementations.ModelGateway;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Data.DTOs;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests;

/// <summary>A model gateway that counts the leases it opens and ends.</summary>
internal sealed class CountingGateway : IModelGatewayRegistry
{
    public int Opened { get; private set; }
    public int Ended { get; private set; }

    public ModelGatewayLease Open(Guid providerId, Guid ownerId, string apiUrl, IReadOnlyList<string> modelHosts)
    {
        Opened++;
        return new ModelGatewayLease("token", apiUrl, () => Ended++);
    }

    public GatewayTokenState Lookup(string? presented, out GatewayGrant? grant)
    {
        grant = null;
        return GatewayTokenState.Unknown;
    }

    public bool BlocksHost(Guid? providerId, string host) => false;
}

/// <summary>
/// A CLI adapter that is nothing but a script: what its turn does at each step is set from the test, and everything it does is
/// written to <see cref="Journal"/>, in order, together with what the base does around it (the fork, through the store).
/// </summary>
internal sealed class ScriptedAdapter(IServiceScopeFactory scopes, CountingGateway gateway, List<string>? journal = null) : CliAgentAdapterBase(scopes)
{
    public CountingGateway Gateway => gateway;

    /// <summary>Every step of the turn and of the session protocol around it, in the order they ran.</summary>
    public List<string> Journal { get; } = journal ?? [];

    public bool SupportsNoTools { get; set; } = true;
    public bool SupportsSessionResume { get; set; } = true;
    public bool OffersRetry { get; set; }
    public BlankOutput BlankOutput { get; set; }
    public bool ProviderErrorIsTheFailure { get; set; }
    public string? Fingerprint { get; set; }
    public string? PrepareFailure { get; set; }
    public Exception? ParseThrows { get; set; }
    public Action? AfterTurnEnded { get; set; }
    public Action? OnParse { get; set; }
    public ScriptedTurn? Turn { get; private set; }

    /// <summary>What the turn makes of its standard output; by default all of it is the answer, as plain output.</summary>
    public Func<string, StreamResult> ReadAs { get; set; } = stdout => new StreamResult(stdout, null, null, SawJsonEvents: false, RawStdout: stdout);

    public Func<string, bool> AcceptsSessionId { get; set; } = _ => true;

    /// <summary>What restoring the session finds: false when there is nothing to resume.</summary>
    public bool Restores { get; set; } = true;

    /// <summary>The session <see cref="CliAgentTurn.ReadSessionAsync"/> answers for the one it is given; the same one by default.</summary>
    public Func<string?, string?> ReadsSessionAs { get; set; } = sessionId => sessionId;

    /// <summary>What saving the session says the agent answered.</summary>
    public string? SavedText { get; set; }

    public TokenUsage? Usage { get; set; }

    public override string Name => "Scripted";
    public override string[] SupportedProviderTypes => ["scripted"];
    public override AgentAdapterTraits Traits
        => ClaudeCodeAdapter.TypeTraits with { DisplayName = "Scripted", SupportsNoTools = SupportsNoTools, SupportsSessionResume = SupportsSessionResume };

    protected override string CliName => "scripted";
    protected override string StartHelp => "install it.";
    protected override string? LaunchFingerprint => Fingerprint;

    protected override CliAgentTurn CreateTurn(AgentExecutionContext ctx, string worktreePath, Lazy<ModelGatewayLease?> gateway)
        => Turn = new ScriptedTurn(this, worktreePath, gateway);

    public sealed class ScriptedTurn(ScriptedAdapter adapter, string worktreePath, Lazy<ModelGatewayLease?> gateway) : CliAgentTurn
    {
        private string _stdout = string.Empty;

        /// <summary>The steps of the turn itself, without the session protocol around it.</summary>
        public List<string> Steps { get; } = [];

        /// <summary>How many leases the gateway had opened by the end of each step; this turn reads its own when it launches.</summary>
        public List<string> LeasesOpened { get; } = [];

        public string? LaunchedOn { get; private set; }

        public override string BinaryPath => "/bin/scripted";

        private void Step(string step)
        {
            Steps.Add(step);
            adapter.Journal.Add(step);
        }

        public override Task PrepareAsync()
        {
            Step("prepare");
            LeasesOpened.Add($"prepare:{adapter.Gateway.Opened}");
            return adapter.PrepareFailure is { } failure ? throw Failed(failure) : Task.CompletedTask;
        }

        public override BlankOutput BlankOutput => adapter.BlankOutput;

        public override bool ProviderErrorIsTheFailure => adapter.ProviderErrorIsTheFailure;

        public override bool IsSessionId(string sessionId) => adapter.AcceptsSessionId(sessionId);

        public override Task<bool> RestoreSessionAsync(string sessionId)
        {
            adapter.Journal.Add($"restore:{sessionId}");
            return Task.FromResult(adapter.Restores);
        }

        public override Task<string?> ReadSessionAsync(string? sessionId)
        {
            adapter.Journal.Add($"read:{sessionId ?? "-"}");
            return Task.FromResult(adapter.ReadsSessionAs(sessionId));
        }

        public override Task<string?> SaveSessionAsync(string sessionId)
        {
            adapter.Journal.Add($"save:{sessionId}");
            return Task.FromResult(adapter.SavedText);
        }

        public override Task<AgentLaunch> LaunchAsync()
        {
            Step("launch");
            LaunchedOn = ResumeSessionId;
            _ = gateway.Value;
            LeasesOpened.Add($"launch:{adapter.Gateway.Opened}");
            return Task.FromResult(Launch());
        }

        public override Task<AgentLaunch?> RetryLaunchAsync(Exception failure)
            => Task.FromResult(adapter.OffersRetry ? Launch("retry") : null);

        private AgentLaunch Launch(params string[] arguments)
        {
            var psi = new ProcessStartInfo(BinaryPath) { WorkingDirectory = worktreePath };
            foreach (var argument in arguments) psi.ArgumentList.Add(argument);
            return new AgentLaunch(psi, new Dictionary<string, string?> { ["FROM_TURN"] = "1" });
        }

        public override Task SendInputAsync(IRunProcess process)
        {
            Step("input");
            return Task.CompletedTask;
        }

        public override async Task ReadStdoutAsync(StreamReader stdout)
        {
            Step("stdout");
            _stdout = await stdout.ReadToEndAsync();
            if (TurnEnded is not null) await TurnEnded();
            adapter.AfterTurnEnded?.Invoke();
        }

        public override Task AfterTimeoutAsync(Task stdout)
        {
            Step("timeout");
            return Task.CompletedTask;
        }

        public override StreamResult ParseStream()
        {
            Step("parse");
            adapter.OnParse?.Invoke();
            if (adapter.ParseThrows is { } failure) throw failure;
            return adapter.ReadAs(_stdout);
        }

        public override Task<TokenUsage?> UsageAsync()
        {
            adapter.Journal.Add("usage");
            return Task.FromResult(adapter.Usage);
        }

        public override ValueTask DisposeAsync()
        {
            Step("dispose");
            return ValueTask.CompletedTask;
        }
    }
}
