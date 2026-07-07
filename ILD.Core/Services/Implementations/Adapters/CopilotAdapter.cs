using System.Diagnostics;
using System.Text;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Core.Services.Implementations.Adapters;

/// <summary>
/// Runs GitHub's <c>copilot</c> CLI in headless (<c>--prompt</c>) mode.
/// Authentication is handled by the CLI itself: the user signs in once via the
/// interactive <c>/login</c> command (a GitHub Copilot subscription), which
/// stores credentials under <c>~/.copilot</c>. Like <see cref="ClaudeCodeAdapter"/>
/// this adapter intentionally ignores <see cref="AiProvider.BaseUrl"/>,
/// <see cref="AiProvider.ApiKey"/> and <see cref="AiProvider.Model"/> — those
/// fields are not meaningful for subscription-based CLI auth.
/// </summary>
public sealed class CopilotAdapter : CliAgentAdapterBase
{
    public CopilotAdapter()
    {
    }

    public CopilotAdapter(IServiceScopeFactory scopeFactory)
        : base(scopeFactory)
    {
    }

    public override string Name => "Copilot";
    public override string[] SupportedProviderTypes => ["copilot"];

    public override async Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext ctx)
    {
        try
        {
            var rendered = await RenderPromptAsync(ctx.Prompt, ctx.RunContext);
            var binaryPath = AiProviderConfig.Parse(ctx.Provider.Config)
                .BinaryPathOr(ManagedAgentInstall.ResolveCommand(ManagedAgentCatalog.Copilot));

            var worktreePath = ctx.RunContext.WorktreePath;
            if (string.IsNullOrEmpty(worktreePath) || !Directory.Exists(worktreePath))
                return NodeExecutionResult.Fail(
                    "[copilot-error] AI node requires a valid worktree path; refusing to run outside the loop's worktree.");

            Process? proc;
            try
            {
                proc = Process.Start(BuildRunProcessStartInfo(binaryPath, worktreePath, rendered, ctx.SessionId, ctx.AdditionalAllowedDirectories));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                return NodeExecutionResult.Fail(
                    $"[copilot-error] cannot start '{binaryPath}' — install or update GitHub Copilot from the AI Provider page (or make sure the copilot CLI is on PATH), and make sure you are logged in (open the provider terminal and run '/login'). Details: {ex.Message}");
            }

            using var process = proc ?? throw new InvalidOperationException("Process.Start returned null");

            var stdoutTask = ReadStreamAsync(process.StandardOutput, ctx.ProgressCallback, ctx.Cancel);
            var stderrTask = process.StandardError.ReadToEndAsync(ctx.Cancel);

            try
            {
                await process.WaitForExitAsync(ctx.Cancel);
            }
            catch (OperationCanceledException)
            {
                KillProcessTree(process);
                return NodeExecutionResult.Fail("copilot timed out");
            }

            string stdout;
            string stderr;
            try
            {
                stdout = await stdoutTask;
                stderr = await stderrTask;
            }
            catch (OperationCanceledException)
            {
                KillProcessTree(process);
                return NodeExecutionResult.Fail("copilot stream read timed out");
            }

            var response = stdout.Trim();
            if (string.IsNullOrWhiteSpace(response))
            {
                response = !string.IsNullOrWhiteSpace(stderr)
                    ? $"[copilot] no assistant text response. stderr: {stderr.Trim()}"
                    : "[copilot] no assistant text response from model";
            }

            return process.ExitCode == 0
                ? NodeExecutionResult.Ok(response, rendered, ctx.SessionId, ctx.IncomingSessionId, AdapterUsageParser.Parse(stdout))
                : NodeExecutionResult.Fail($"exit={process.ExitCode} stderr={stderr}", response);
        }
        catch (Exception ex)
        {
            return NodeExecutionResult.Fail($"[copilot-error] {ex.Message}");
        }
    }

    public static ProcessStartInfo BuildRunProcessStartInfo(
        string binaryPath,
        string worktreePath,
        string renderedPrompt,
        string? sessionId,
        IReadOnlyList<string>? additionalAllowedDirectories = null)
    {
        var psi = new ProcessStartInfo(binaryPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = worktreePath,
        };

        // Disable ANSI colour so the captured stdout is the model's plain text.
        psi.EnvironmentVariables["NO_COLOR"] = "1";

        // Auto-approve tool use so the headless run isn't blocked on interactive
        // confirmation prompts, and trust the worktree so file/command tools can
        // operate inside it.
        psi.ArgumentList.Add("--allow-all-tools");
        psi.ArgumentList.Add("--add-dir");
        psi.ArgumentList.Add(worktreePath);

        // Per-turn extra grants (ADR-0011): e.g. the Chat Context's open work
        // item active-run worktree. Each becomes its own `--add-dir` so the agent
        // can reach the absolute path without changing its cwd. Skip the worktree
        // itself (already added) and any duplicate.
        if (additionalAllowedDirectories is not null)
        {
            foreach (var dir in additionalAllowedDirectories)
            {
                if (string.IsNullOrWhiteSpace(dir)
                    || string.Equals(dir, worktreePath, StringComparison.Ordinal))
                    continue;
                psi.ArgumentList.Add("--add-dir");
                psi.ArgumentList.Add(dir);
            }
        }

        // Continue a prior turn's session when one is bound, so multi-turn loops
        // keep the agent's context.
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            psi.ArgumentList.Add("--resume");
            psi.ArgumentList.Add(sessionId);
        }

        // `--prompt` runs a single turn non-interactively and exits. Keep it last
        // so the prompt text is unambiguously the option's value.
        psi.ArgumentList.Add("--prompt");
        psi.ArgumentList.Add(renderedPrompt);
        return psi;
    }

    private static async Task<string> ReadStreamAsync(
        StreamReader reader,
        Func<string, Task>? progressCallback,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            sb.AppendLine(line);
            if (progressCallback is not null && line.Length > 0)
                await progressCallback(line).ConfigureAwait(false);
        }
        return sb.ToString();
    }
}
