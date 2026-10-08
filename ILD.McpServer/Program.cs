using System.Text;
using ILD.McpServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// MCP servers communicate over stdio. All informational logging MUST go to
// stderr or it will corrupt the JSON-RPC framing on stdout.
var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

var apiUrl = Environment.GetEnvironmentVariable("ILD_API_URL")
    ?? "http://localhost:5000";
var apiToken = Environment.GetEnvironmentVariable("ILD_API_TOKEN") ?? "";
var runId = Environment.GetEnvironmentVariable("ILD_LOOP_RUN_ID");
var chatSessionId = Environment.GetEnvironmentVariable("ILD_CHAT_SESSION_ID");
var chatTurnId = Environment.GetEnvironmentVariable("ILD_CHAT_TURN_ID");

builder.Services.AddSingleton(new IldClientOptions(apiUrl, apiToken, runId, chatSessionId, chatTurnId));
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
    if (!string.IsNullOrEmpty(opts.ChatTurnId))
        c.DefaultRequestHeaders.Add("X-ILD-Chat-Turn-Id", opts.ChatTurnId);
});

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
