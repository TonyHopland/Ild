using ILD.McpServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Length > 0 && args[0] == McpServerLauncher.Flag)
    return McpServerLauncher.Run(args);

await RunServerAsync(args);
return 0;

// Kept out of the top-level statements so that launcher mode never loads the host.
static async Task RunServerAsync(string[] args)
{
    // MCP servers communicate over stdio. All informational logging MUST go to
    // stderr or it will corrupt the JSON-RPC framing on stdout.
    var builder = Host.CreateApplicationBuilder(args);

    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

    builder.Services.AddSingleton(IldClientOptions.Resolve(Environment.GetEnvironmentVariable, Console.Error));
    builder.Services.AddHttpClient<IldClient>((sp, c) =>
    {
        var opts = sp.GetRequiredService<IldClientOptions>();
        c.BaseAddress = new Uri(opts.ApiUrl.TrimEnd('/') + "/");
        if (!string.IsNullOrEmpty(opts.ApiToken))
            c.DefaultRequestHeaders.Add("Authorization", "Bearer " + opts.ApiToken);
        if (!string.IsNullOrEmpty(opts.LoopRunId))
            c.DefaultRequestHeaders.Add("X-ILD-Run-Id", opts.LoopRunId);
        if (!string.IsNullOrEmpty(opts.ChatSessionId))
            c.DefaultRequestHeaders.Add("X-ILD-Chat-Session-Id", opts.ChatSessionId);
    });

    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

    await builder.Build().RunAsync();
}
