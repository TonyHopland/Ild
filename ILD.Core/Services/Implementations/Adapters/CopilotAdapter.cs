using System.Diagnostics;
using System.Text;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Core.Services.Implementations.Adapters;

/// <summary>
/// Runs GitHub's <c>copilot</c> CLI in headless (<c>-p</c>/<c>--prompt</c>) mode.
/// Authentication is handled by the CLI itself: the user signs in once via the
/// interactive <c>/login</c> command (a GitHub Copilot subscription), which
/// stores credentials under <c>~/.copilot</c>. Like <see cref="ClaudeCodeAdapter"/>
/// this adapter intentionally ignores <see cref="AiProvider.BaseUrl"/>,
/// <see cref="AiProvider.ApiKey"/> and <see cref="AiProvider.Model"/> — those
/// fields are not meaningful for subscription-based CLI auth.
///
/// The invocation follows GitHub's documented headless form
/// (<c>copilot --allow-all-tools -p "…"</c>): <c>--allow-all-tools</c> is
/// required for programmatic runs so the CLI never blocks on an interactive
/// approval prompt, and each <c>--add-dir</c> trusts a directory for file access.
/// Default output is plain text on stdout, which becomes the node's output.
/// See https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-command-reference.
///
/// Two deliberate limitations versus <see cref="ClaudeCodeAdapter"/>:
/// <list type="bullet">
///   <item><b>Single-turn.</b> Each run is an independent one-shot; the CLI's
///   <c>-p</c> mode does not surface a resumable session id on stdout, so ILD
///   cannot persist/restore a session the way it does for claude-code/opencode/pi.
///   Multi-turn loops re-send context via the prompt rather than resuming.</item>
///   <item><b>Always all-tools.</b> <c>--allow-all-tools</c> is mandatory for
///   headless use, so a Copilot provider runs unrestricted; the per-node tool
///   allowlist (read/write/execute/ild) is not applied (Copilot is excluded from
///   <see cref="ILD.Data.AiToolCatalog"/>'s default-agent set).</item>
/// </list>
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
                proc = Process.Start(BuildRunProcessStartInfo(binaryPath, worktreePath, rendered, ctx.AdditionalAllowedDirectories));
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
                // Strip NUL bytes as the raw CLI stream is captured. Unlike the
                // claude-code/opencode/pi adapters — which parse a JSON event
                // stream and surface only clean assistant text — this adapter
                // forwards copilot's raw terminal stdout/stderr verbatim, and a
                // raw stream can carry a NUL (U+0000). A NUL cannot be stored in
                // a PostgreSQL text/varchar column, so leaving it in the node's
                // output/error makes the engine's persistence SaveChanges throw a
                // DbUpdateException, crashing the run before its result is
                // recorded. Removing it keeps every visible character intact.
                stdout = StripNullChars(await stdoutTask);
                stderr = StripNullChars(await stderrTask);
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

            // Single-turn: no session id is bound (see class remarks), so the
            // result carries none and the run does not chain to a later --resume.
            return process.ExitCode == 0
                ? NodeExecutionResult.Ok(response, rendered)
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

        // Auto-approve tool use (mandatory for headless `-p` runs so the CLI
        // never blocks on an interactive confirmation prompt) and drop ANSI
        // colour so the captured stdout is the model's plain text.
        psi.ArgumentList.Add("--allow-all-tools");
        psi.ArgumentList.Add("--no-color");
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

        // `-p` runs a single turn non-interactively and exits. Keep it last so
        // the prompt text is unambiguously the option's value.
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(renderedPrompt);
        return psi;
    }

    /// <summary>
    /// Remove the NUL (U+0000) character from captured CLI output. It is the one
    /// character a PostgreSQL <c>text</c>/<c>varchar</c> column cannot store, so
    /// it must never reach the node output/error the engine persists. Returns the
    /// input unchanged when it holds no NUL (the common case).
    /// </summary>
    public static string StripNullChars(string value)
        => value.IndexOf('\0') < 0 ? value : value.Replace("\0", string.Empty);

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
