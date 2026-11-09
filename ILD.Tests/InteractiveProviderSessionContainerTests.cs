using ILD.Core.Services.RunHosts.Local;
using System.Net.WebSockets;
using System.Text;
using ILD.Api.Services;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Container;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using ILD.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>The provider sign-in terminal with the container run host: the CLI runs in a short-lived container and what it wrote is kept.</summary>
public sealed class InteractiveProviderSessionContainerTests : IAsyncLifetime
{
    private const string Token = "sk-ant-oat01-abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOP";
    private const string Login = """{"claudeAiOauth":{"accessToken":"a","refreshToken":"r"}}""";

    private readonly string _home = Directory.CreateTempSubdirectory("ild-signin-home-").FullName;
    private FakeDockerApi _api = null!;
    private ContainerRunHost _container = null!;

    public ValueTask InitializeAsync()
    {
        _api = FakeDockerApi.Start();
        InstallClaude();
        _container = new ContainerRunHost(
            ContainerRunHostOptions.From(name => name switch
            {
                "ILD_API_URL" => "http://ild.example:8080",
                "ILD_CONTAINER_SOCKET" => _api.SocketPath,
                "ILD_CONTAINER_RUNNER_IMAGE" => "registry.example/ild-runner:1",
                _ => null,
            }) with { ReadyTimeout = TimeSpan.FromSeconds(10) },
            new ProcessRunner())
        {
            ReadyPollInterval = TimeSpan.FromMilliseconds(10),
            ExitStatusTimeout = TimeSpan.FromMilliseconds(400),
            Agents = () => [],
        };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _container.Dispose();
        await _api.DisposeAsync();
        Directory.Delete(_home, recursive: true);
        Directory.Delete(_agents, recursive: true);
    }

    private static AiProvider Claude() => new()
    {
        Id = Guid.NewGuid(),
        Name = "claude",
        Type = "claude-code",
        BaseUrl = "http://localhost",
        Model = "n/a",
        Config = "{}",
    };

    private readonly string _agents = Directory.CreateTempSubdirectory("ild-signin-agents-").FullName;

    private string DataRoot => _agents;

    // The managed CLI the container installs from the store: a current install on ILD's side is what lets the sign-in start.
    private void InstallClaude()
    {
        var pointer = ManagedAgentInstall.PointerFile(DataRoot, ManagedAgentCatalog.ClaudeCode);
        Directory.CreateDirectory(Path.GetDirectoryName(pointer)!);
        File.WriteAllText(pointer, "v1");
    }

    private InteractiveProviderSessionService Service(IProviderStore? store = null, IRunHost? host = null)
    {
        var scopes = new ServiceCollection().AddSingleton(store ?? Mock.Of<IProviderStore>()).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new InteractiveProviderSessionService(
            NullLogger<InteractiveProviderSessionService>.Instance, scopes: scopes, host: host ?? new RoutingRunHost(new FakeRunHost(), _container))
        { StoreHome = _home, DataRoot = DataRoot };
    }

    private async Task RunToCloseAsync(InteractiveProviderSessionService service, AiProvider provider)
    {
        var (server, client) = WebSocketPair.Create();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var runTask = service.RunAsync(server, provider, initialCols: 200, initialRows: 24, cancellationToken: cts.Token);
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do { result = await client.ReceiveAsync(buffer, cts.Token); }
        while (result.MessageType != WebSocketMessageType.Close);
        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
        await runTask;
    }

    // The CLI prints the token and exits; the container holds a Claude login and nothing else.
    private void AnswerClaude()
    {
        _api.OnExec = async e =>
        {
            if (e.WantsStdin && !e.Tty) await e.ReadStdinToEndAsync();
            if (e.Tty)
            {
                await e.WriteStdoutAsync($"Your token:\r\n{Token}\r\n");
                await e.ExitAsync(0);
                return;
            }
            if (e.Command.TakeLast(2).First() == ".claude/.credentials.json")
                await e.WriteStdoutAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(Login)));
            await e.ExitAsync(e.Command.TakeLast(2).First() == ".claude/.credentials.json" ? 0 : 3);
        };
    }

    [Fact]
    public async Task A_provider_with_a_custom_binary_path_is_refused_and_no_sign_in_container_is_opened()
    {
        var provider = Claude();
        provider.Config = """{"binaryPath":"/opt/claude/bin/claude"}""";
        var (server, client) = WebSocketPair.Create();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var runTask = Service().RunAsync(server, provider, initialCols: 200, initialRows: 24, cancellationToken: cts.Token);
        var text = new StringBuilder();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await client.ReceiveAsync(buffer, cts.Token);
            text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        }
        while (result.MessageType != WebSocketMessageType.Close);
        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
        await runTask;

        Assert.Contains("custom binary path", text.ToString() + result.CloseStatusDescription);
        Assert.DoesNotContain(_api.Created, c => c.StartsWith("containers/ild-signin-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_login_is_kept_the_claude_token_becomes_the_key_and_the_container_is_removed()
    {
        AnswerClaude();
        var provider = Claude();
        var stored = new AiProvider { Id = provider.Id, Name = provider.Name, Type = provider.Type, BaseUrl = "x", Model = "n/a" };
        var store = new Mock<IProviderStore>();
        store.Setup(s => s.GetAiProviderByIdAsync(provider.Id)).ReturnsAsync(stored);

        await RunToCloseAsync(Service(store.Object), provider);

        Assert.Equal(Login, File.ReadAllText(Path.Combine(_home, ".claude", ".credentials.json")));
        Assert.Equal(Token, stored.ApiKey);
        store.Verify(s => s.UpdateAiProviderAsync(stored), Times.Once);
        Assert.Equal(["setup-token"], _api.Execs.First(e => e.Tty).Command.TakeLast(1));
        Assert.DoesNotContain(_api.Containers(), c => c.StartsWith("ild-signin-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_container_is_removed_when_the_terminal_cannot_be_kept()
    {
        _api.OnExec = async e =>
        {
            if (e.WantsStdin && !e.Tty) await e.ReadStdinToEndAsync();
            if (!e.Tty) await e.WriteStderrAsync("base64: boom");
            await e.ExitAsync(e.Tty ? 0 : 1);
        };

        await RunToCloseAsync(Service(), Claude());

        Assert.Empty(Directory.GetFiles(_home, "*", SearchOption.AllDirectories));
        Assert.DoesNotContain(_api.Containers(), c => c.StartsWith("ild-signin-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_container_is_removed_when_the_bridge_throws()
    {
        AnswerClaude();
        var (server, client) = WebSocketPair.Create();
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "gone", CancellationToken.None);
        server.Dispose();

        await Record.ExceptionAsync(() => Service().RunAsync(server, Claude(), 80, 24, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(_api.Containers(), c => c.StartsWith("ild-signin-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_managed_agent_ild_has_not_installed_gets_the_install_message_and_no_container()
    {
        File.Delete(ManagedAgentInstall.PointerFile(DataRoot, ManagedAgentCatalog.ClaudeCode));
        var provider = Claude();
        var (server, client) = WebSocketPair.Create();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = Service().RunAsync(server, provider, 80, 24, cts.Token);
        var buffer = new byte[4096];
        var text = new StringBuilder();
        WebSocketReceiveResult result;
        do
        {
            result = await client.ReceiveAsync(buffer, cts.Token);
            text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        }
        while (result.MessageType != WebSocketMessageType.Close);
        await runTask;

        Assert.Contains("isn't installed", text.ToString());
        Assert.Empty(_api.Created);
    }
}
