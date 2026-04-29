using FluentAssertions;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using System.Net;
using System.Text.Json;

namespace ILD.Tests;

public class OpenAiCompatibleAdapterTests
{
    [Fact]
    public async Task ExecuteAsync_on_first_execution_renders_initial_prompt_and_succeeds()
    {
        var handler = new TestHttpHandler(
            responseJson: """{"choices":[{"message":{"content":"hello My Task"}}]}""");
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") };
        var adapter = new OpenAiCompatibleAdapter(http);

        var ctx = new AgentExecutionContext(
            Provider: new AiProvider { Name = "test", Type = "openai", BaseUrl = "https://api.test", Model = "gpt-4" },
            InitialPrompt: "Hello {{WorkItem.Title}}",
            LoopPrompt: "Continue",
            RunContext: new LoopRunContext(
                Guid.NewGuid(), Guid.NewGuid(), "My Task", "desc", "/tmp", "main", new List<string>(), null),
            ExecutionCount: 1,
            Cancel: CancellationToken.None);

        var result = await adapter.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
        result.Output.Should().Be("hello My Task");
        handler.RequestBodies.Count.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_on_loopback_uses_loop_prompt()
    {
        var handler = new TestHttpHandler(
            responseJson: """{"choices":[{"message":{"content":"continued"}}]}""");
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") };
        var adapter = new OpenAiCompatibleAdapter(http);

        var ctx = new AgentExecutionContext(
            Provider: new AiProvider { Name = "test", Type = "openai", BaseUrl = "https://api.test", Model = "gpt-4" },
            InitialPrompt: "Initial prompt",
            LoopPrompt: "Loop prompt",
            RunContext: new LoopRunContext(
                Guid.NewGuid(), Guid.NewGuid(), "Title", "desc", "/tmp", "main", new List<string>(), null),
            ExecutionCount: 2,
            Cancel: CancellationToken.None);

        var result = await adapter.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
        result.Output.Should().Be("continued");
        handler.RequestBodies[0].Should().Contain("Loop prompt");
    }

    [Fact]
    public async Task ExecuteAsync_reads_config_from_json_and_falls_back_to_typed_fields()
    {
        var handler = new TestHttpHandler(
            responseJson: """{"choices":[{"message":{"content":"done"}}]}""");
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://config-override/") };
        var adapter = new OpenAiCompatibleAdapter(http);

        var ctx = new AgentExecutionContext(
            Provider: new AiProvider
            {
                Name = "test",
                Type = "openai",
                BaseUrl = "https://fallback",
                ApiKey = "fallback-key",
                Model = "fallback-model",
                Config = """{"baseUrl":"https://config-override","apiKey":"config-key","model":"config-model"}"""
            },
            InitialPrompt: "test",
            LoopPrompt: "",
            RunContext: new LoopRunContext(
                Guid.NewGuid(), Guid.NewGuid(), "Title", "desc", "/tmp", "main", new List<string>(), null),
            ExecutionCount: 1,
            Cancel: CancellationToken.None);

        var result = await adapter.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
        handler.RequestBodies[0].Should().Contain("config-model");
        handler.RequestBodies[0].Should().NotContain("fallback-model");
    }

    [Fact]
    public async Task ExecuteAsync_uses_typed_fields_when_config_is_empty()
    {
        var handler = new TestHttpHandler(
            responseJson: """{"choices":[{"message":{"content":"fallback"}}]}""");
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://fallback/") };
        var adapter = new OpenAiCompatibleAdapter(http);

        var ctx = new AgentExecutionContext(
            Provider: new AiProvider
            {
                Name = "test",
                Type = "openai",
                BaseUrl = "https://fallback",
                ApiKey = "fallback-key",
                Model = "fallback-model",
                Config = null
            },
            InitialPrompt: "test",
            LoopPrompt: "",
            RunContext: new LoopRunContext(
                Guid.NewGuid(), Guid.NewGuid(), "Title", "desc", "/tmp", "main", new List<string>(), null),
            ExecutionCount: 1,
            Cancel: CancellationToken.None);

        var result = await adapter.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
        handler.RequestBodies[0].Should().Contain("fallback-model");
    }

    [Fact]
    public async Task ExecuteAsync_when_llm_fails_returns_failure_result()
    {
        var handler = new TestHttpHandler(statusCode: 500);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") };
        var adapter = new OpenAiCompatibleAdapter(http);

        var ctx = new AgentExecutionContext(
            Provider: new AiProvider { Name = "test", Type = "openai", BaseUrl = "https://api.test", Model = "gpt-4" },
            InitialPrompt: "test",
            LoopPrompt: "",
            RunContext: new LoopRunContext(
                Guid.NewGuid(), Guid.NewGuid(), "Title", "desc", "/tmp", "main", new List<string>(), null),
            ExecutionCount: 1,
            Cancel: CancellationToken.None);

        var result = await adapter.ExecuteAsync(ctx);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNull();
    }

    private sealed class TestHttpHandler : HttpMessageHandler
    {
        private readonly string _responseJson;
        private readonly HttpStatusCode _statusCode;
        public List<string> RequestBodies { get; } = new();

        public TestHttpHandler(string responseJson = """{"choices":[{"message":{"content":"ok"}}]}""", int statusCode = 200)
        {
            _responseJson = responseJson;
            _statusCode = (HttpStatusCode)statusCode;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync());
            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseJson)
            };
        }
    }
}
