using System.Diagnostics;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Tests;

/// <summary>
/// With no <c>binaryPath</c> configured, an adapter launches the managed install
/// under the data root its supplied environment names in <c>ILD_DATA_PATH</c>.
/// The staged binary only records that it ran.
/// </summary>
public sealed class ManagedAgentCommandEnvironmentTests : IDisposable
{
    private readonly string _dataRoot = Directory.CreateTempSubdirectory("ild-managed-data-").FullName;
    private readonly string _worktree = Directory.CreateTempSubdirectory("ild-managed-wt-").FullName;

    public void Dispose()
    {
        foreach (var dir in new[] { _dataRoot, _worktree })
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Theory]
    [InlineData("claude-code")]
    [InlineData("copilot")]
    [InlineData("opencode")]
    [InlineData("pi")]
    public async Task The_managed_install_under_the_supplied_data_path_is_launched(string type)
    {
        var agent = ManagedAgentCatalog.Find(type)!;
        var marker = Path.Combine(_worktree, "launched");
        StageManagedInstall(agent, marker);
        var environment = new TestProcessEnvironment { { "ILD_DATA_PATH", _dataRoot } };

        IAgentAdapter adapter = type switch
        {
            "claude-code" => new ClaudeCodeAdapter(environment: environment),
            "copilot" => new CopilotAdapter(environment: environment),
            "opencode" => new OpenCodeAdapter(environment: environment),
            _ => new PiAdapter(environment: environment),
        };

        await adapter.ExecuteAsync(new AgentExecutionContext(
            Provider: new AiProvider
            {
                Name = $"{type}-managed",
                Type = type,
                BaseUrl = string.Empty,
                Model = string.Empty,
                Config = null,
            },
            Prompt: "fix it",
            RunContext: new LoopRunContext(Guid.NewGuid(), "wi", "t", "d", _worktree, "main", new List<string>(), null),
            ExecutionCount: 1,
            Cancel: CancellationToken.None));

        Assert.True(File.Exists(marker), $"the {type} install under the supplied ILD_DATA_PATH was not launched");
    }

    private void StageManagedInstall(ManagedAgent agent, string marker)
    {
        var binary = ManagedAgentInstall.BinaryIn(ManagedAgentInstall.VersionDir(_dataRoot, agent, "v1"), agent);
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        File.WriteAllText(binary, $"#!/bin/sh\necho launched > '{marker}'\n");
        using (var chmod = Process.Start("chmod", ["+x", binary]))
            chmod.WaitForExit();
        File.WriteAllText(ManagedAgentInstall.PointerFile(_dataRoot, agent), "v1");
    }
}
