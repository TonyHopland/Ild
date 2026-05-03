using FluentAssertions;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using System.Net;
using System.Text.Json;

namespace ILD.Tests;

public class OpenAiCompatibleAdapterTests
{
    [Fact]
    public async Task ExecuteAsync_uses_adapter_config_temperature_and_maxTokens()
    {
        var capturedBodyJson = "";
        var handler = new BodyCapturingHandler(
            setBody: (json) => capturedBodyJson = json,
            responseStreamFactory: () => CreateSseResponseStream("hello world"));

        var factory = new TestHttpClientFactory(handler);
        var adapter = new OpenAiCompatibleAdapter(factory);

        var ctx = new AgentExecutionContext(
            Provider: new AiProvider
            {
                Name = "test",
                Type = "openai",
                BaseUrl = "http://test.api",
                ApiKey = "sk-test",
                Model = "gpt-4"
            },
            InitialPrompt: "test prompt",
            LoopPrompt: "test prompt",
            RunContext: new LoopRunContext(
                Guid.NewGuid(), Guid.NewGuid(), "Title", "Desc", "/tmp", "main", new List<string>(), null),
            ExecutionCount: 1,
            Cancel: CancellationToken.None,
            AdapterConfig: new Dictionary<string, object?>
            {
                ["temperature"] = 0.3,
                ["maxTokens"] = 2048
            });

        var result = await adapter.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
        capturedBodyJson.Should().Contain("\"temperature\":0.3");
        capturedBodyJson.Should().Contain("\"maxTokens\":2048");
    }

    [Fact]
    public async Task ExecuteAsync_uses_provider_config_as_default_temperature_and_maxTokens()
    {
        var capturedBodyJson = "";
        var handler = new BodyCapturingHandler(
            setBody: (json) => capturedBodyJson = json,
            responseStreamFactory: () => CreateSseResponseStream("hello"));

        var factory = new TestHttpClientFactory(handler);
        var adapter = new OpenAiCompatibleAdapter(factory);

        var ctx = new AgentExecutionContext(
            Provider: new AiProvider
            {
                Name = "test",
                Type = "openai",
                BaseUrl = "http://test.api",
                ApiKey = "sk-test",
                Model = "gpt-4",
                Config = "{\"temperature\":0.2,\"maxTokens\":1024}"
            },
            InitialPrompt: "test prompt",
            LoopPrompt: "test prompt",
            RunContext: new LoopRunContext(
                Guid.NewGuid(), Guid.NewGuid(), "Title", "Desc", "/tmp", "main", new List<string>(), null),
            ExecutionCount: 1,
            Cancel: CancellationToken.None,
            AdapterConfig: null);

        var result = await adapter.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
        capturedBodyJson.Should().Contain("\"temperature\":0.2");
        capturedBodyJson.Should().Contain("\"maxTokens\":1024");
    }

    [Fact]
    public async Task ExecuteAsync_node_config_overrides_provider_config_defaults()
    {
        var capturedBodyJson = "";
        var handler = new BodyCapturingHandler(
            setBody: (json) => capturedBodyJson = json,
            responseStreamFactory: () => CreateSseResponseStream("hello"));

        var factory = new TestHttpClientFactory(handler);
        var adapter = new OpenAiCompatibleAdapter(factory);

        var ctx = new AgentExecutionContext(
            Provider: new AiProvider
            {
                Name = "test",
                Type = "openai",
                BaseUrl = "http://test.api",
                ApiKey = "sk-test",
                Model = "gpt-4",
                Config = "{\"temperature\":0.2,\"maxTokens\":1024}"
            },
            InitialPrompt: "test prompt",
            LoopPrompt: "test prompt",
            RunContext: new LoopRunContext(
                Guid.NewGuid(), Guid.NewGuid(), "Title", "Desc", "/tmp", "main", new List<string>(), null),
            ExecutionCount: 1,
            Cancel: CancellationToken.None,
            AdapterConfig: new Dictionary<string, object?>
            {
                ["temperature"] = 0.9
            });

        var result = await adapter.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
        capturedBodyJson.Should().Contain("\"temperature\":0.9");
        capturedBodyJson.Should().Contain("\"maxTokens\":1024");
    }

    [Fact]
    public async Task ExecuteAsync_uses_default_temperature_and_maxTokens_when_not_configured()
    {
        var capturedBodyJson = "";
        var handler = new BodyCapturingHandler(
            setBody: (json) => capturedBodyJson = json,
            responseStreamFactory: () => CreateSseResponseStream("response text"));

        var factory = new TestHttpClientFactory(handler);
        var adapter = new OpenAiCompatibleAdapter(factory);

        var ctx = new AgentExecutionContext(
            Provider: new AiProvider
            {
                Name = "test",
                Type = "openai",
                BaseUrl = "http://test.api",
                ApiKey = "sk-test",
                Model = "gpt-4"
            },
            InitialPrompt: "test prompt",
            LoopPrompt: "test prompt",
            RunContext: new LoopRunContext(
                Guid.NewGuid(), Guid.NewGuid(), "Title", "Desc", "/tmp", "main", new List<string>(), null),
            ExecutionCount: 1,
            Cancel: CancellationToken.None,
            AdapterConfig: null);

        var result = await adapter.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
        capturedBodyJson.Should().Contain("\"temperature\":0.7");
        capturedBodyJson.Should().Contain("\"maxTokens\":4096");
    }

    static MemoryStream CreateSseResponseStream(string content)
    {
        var json = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content } } } });
        var data = $"data: {json}\r\ndata: [done]\r\n";
        return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(data));
    }

    sealed class TestHttpClientFactory : IHttpClientFactory
    {
        readonly HttpMessageHandler handler;
        public TestHttpClientFactory(HttpMessageHandler handler) => this.handler = handler;
        public HttpClient CreateClient(string? name = null) => new(handler);
    }

    sealed class BodyCapturingHandler : HttpMessageHandler
    {
        readonly Action<string> setBody;
        readonly Func<Stream> responseStreamFactory;

        public BodyCapturingHandler(Action<string> setBody, Func<Stream> responseStreamFactory)
        {
            this.setBody = setBody;
            this.responseStreamFactory = responseStreamFactory;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content != null)
            {
                var json = await request.Content.ReadAsStringAsync();
                setBody(json);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(responseStreamFactory())
            };
        }
    }
}