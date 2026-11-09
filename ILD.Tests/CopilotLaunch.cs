using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Implementations.ModelGateway;
using ILD.Core.Services.RunHosts;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests;

/// <summary>Runs one Copilot launch through the real adapter and an isolated run host, for the tests that check what the run is given.</summary>
internal static class CopilotLaunch
{
    public static async Task RunAsync(
        IRunHost host, Guid run, string worktree, string readRoot, string caFile, string ildUrl)
    {
        var services = new ServiceCollection();
        services.AddSingleton(host);
        services.AddSingleton<IModelGatewayRegistry>(new ModelGatewayRegistry());
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var environment = new TestProcessEnvironment
        {
            { "ILD_AGENT_READ_ROOT", readRoot },
            { "ILD_API_URL", ildUrl },
            { GatewayTlsEndpoint.PortVariable, "8442" },
            { GatewayTlsEndpoint.CaFileVariable, caFile },
        };
        var context = new AgentExecutionContext(
            Provider: new AiProvider { Id = Guid.NewGuid(), Name = "copilot", Type = "copilot", BaseUrl = string.Empty, Model = string.Empty, Config = "{\"binaryPath\":\"/bin/copilot\"}" },
            Prompt: "hi",
            RunContext: new LoopRunContext(run, Guid.NewGuid().ToString(), "t", "d", worktree, "main", [], null),
            ExecutionCount: 0,
            Cancel: TestContext.Current.CancellationToken);

        await new CopilotAdapter(scopes, environment: environment).ExecuteAsync(context);
    }
}
