using System.Collections;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ILD.McpServer;

/// <summary>
/// <c>ild-mcp-server --launch &lt;file&gt;</c>: replace this process with the MCP
/// server the JSON file describes, <c>{ "command": "…", "args": [ … ], "env": { … } }</c>.
/// An agent CLI that would otherwise carry a custom server's arguments and
/// environment in its own argv (codex) is handed only the file's path, so the
/// server's secrets stay in a file the agent can read but not change (ADR-0014).
///
/// The process image is replaced in place (<c>execve</c>): the server keeps this
/// PID, so the agent's kill of its MCP child reaches the server, and stdin, stdout
/// and stderr pass straight through for the MCP stdio transport. The server's
/// environment is this process's own overlaid by the file's <c>env</c>.
///
/// Nothing is written to stdout. A failure is one <c>ild-mcp-launch:</c> line on
/// stderr naming at most the file and the command, never an argument or an
/// environment value, and the exit code says which: 2 for an unusable file, 127
/// for a command that is not found, 126 for one that cannot be run.
/// </summary>
public static class McpServerLauncher
{
    public const string Flag = "--launch";

    private const int BadFile = 2;
    private const int CannotRun = 126;
    private const int NotFound = 127;

    private const int ENOENT = 2;
    private const int ENOTDIR = 20;
    private const int SIGPIPE = 13;

    /// <summary>Run the launch <paramref name="args"/> name; returns only when the server could not be started.</summary>
    public static int Run(string[] args)
    {
        if (args.Length != 2)
            return Fail(BadFile, $"usage: {Flag} <file>");

        var path = args[1];
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(BadFile, $"cannot read {path}");
        }
        catch (JsonException)
        {
            return Fail(BadFile, $"{path} is not valid JSON");
        }

        string command;
        List<string> arguments;
        Dictionary<string, string> environment;
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Fail(BadFile, $"{path} is not a JSON object");
            if (!root.TryGetProperty("command", out var commandElement)
                || commandElement.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(commandElement.GetString()))
                return Fail(BadFile, $"{path} names no command");
            command = commandElement.GetString()!;

            if (ReadArguments(root) is not { } fileArguments)
                return Fail(BadFile, $"{path}: args must be an array of strings");
            arguments = fileArguments;

            environment = InheritedEnvironment();
            if (!OverlayEnvironment(root, environment))
                return Fail(BadFile, $"{path}: env must be an object of strings");
        }

        var candidates = command.Contains('/')
            ? [command]
            : SearchPath(command, environment.GetValueOrDefault("PATH"));
        if (candidates.Count == 0)
            return Fail(NotFound, $"{command}: command not found");

        string?[] argv = [command, .. arguments, null];
        string?[] envp = [.. environment.Select(kv => $"{kv.Key}={kv.Value}"), null];

        // The runtime ignores SIGPIPE, and an ignored signal stays ignored across
        // execve, so the server would start with a disposition it never chose.
        signal(SIGPIPE, IntPtr.Zero);

        var errno = ENOENT;
        foreach (var candidate in candidates)
        {
            execve(candidate, argv, envp);
            var error = Marshal.GetLastPInvokeError();
            // Like execvp: a candidate that is not there lets the search go on,
            // while one that is there but cannot be run is the answer.
            if (errno is ENOENT or ENOTDIR)
                errno = error;
        }

        return errno is ENOENT or ENOTDIR
            ? Fail(NotFound, $"{command}: command not found")
            : Fail(CannotRun, $"cannot run {command}: {Marshal.GetPInvokeErrorMessage(errno)}");
    }

    private static List<string>? ReadArguments(JsonElement root)
    {
        if (!root.TryGetProperty("args", out var args) || args.ValueKind == JsonValueKind.Null)
            return [];
        if (args.ValueKind != JsonValueKind.Array)
            return null;

        var list = new List<string>();
        foreach (var arg in args.EnumerateArray())
        {
            if (arg.ValueKind != JsonValueKind.String)
                return null;
            list.Add(arg.GetString()!);
        }
        return list;
    }

    private static bool OverlayEnvironment(JsonElement root, Dictionary<string, string> environment)
    {
        if (!root.TryGetProperty("env", out var env) || env.ValueKind == JsonValueKind.Null)
            return true;
        if (env.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var variable in env.EnumerateObject())
        {
            if (variable.Value.ValueKind != JsonValueKind.String)
                return false;
            environment[variable.Name] = variable.Value.GetString()!;
        }
        return true;
    }

    // Read once into a copy: .NET keeps its own view of the environment, and only
    // an explicit envp hands the server what this process holds plus the file's.
    private static Dictionary<string, string> InheritedEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
            environment[(string)variable.Key] = (string?)variable.Value ?? "";
        return environment;
    }

    private static List<string> SearchPath(string command, string? path)
        => (path ?? "")
            .Split(':')
            .Select(directory => Path.Combine(directory.Length == 0 ? "." : directory, command))
            .Where(File.Exists)
            .ToList();

    private static int Fail(int exitCode, string message)
    {
        Console.Error.WriteLine($"ild-mcp-launch: {message}");
        return exitCode;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int execve(string path, string?[] argv, string?[] envp);

    [DllImport("libc")]
    private static extern IntPtr signal(int signum, IntPtr handler);
}
