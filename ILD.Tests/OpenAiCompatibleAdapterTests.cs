using FluentAssertions;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using Microsoft.Extensions.Http;
using System.Net;
using System.Text.Json;

namespace ILD.Tests;

public class OpenAiCompatibleAdapterTests
{
    [Fact]
    public async Task ExecuteAsync_on_first_execution_renders_initial_prompt_and_succeeds()
    {
        var handler = new TestHttpHandler(responseContent: "hello My Task");
        var factory = BuildFactory(handler);
        var adapter = new OpenAiCompatibleAdapter(factory);

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
        var handler = new TestHttpHandler(responseContent: "continued");
        var factory = BuildFactory(handler);
        var adapter = new OpenAiCompatibleAdapter(factory);

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
        var handler = new TestHttpHandler(responseContent: "done");
        var factory = BuildFactory(handler);
        var adapter = new OpenAiCompatibleAdapter(factory);

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
        var handler = new TestHttpHandler(responseContent: "fallback");
        var factory = BuildFactory(handler);
        var adapter = new OpenAiCompatibleAdapter(factory);

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
        var factory = BuildFactory(handler);
        var adapter = new OpenAiCompatibleAdapter(factory);

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

    [Fact]
    public async Task ExecuteAsync_streams_sse_chunks_to_progress_callback()
    {
        var chunks = new[] { "Hello", " ", "world", "!" };
        var handler = new SseStreamHttpHandler(chunks);
        var factory = BuildFactory(handler);
        var adapter = new OpenAiCompatibleAdapter(factory);

        var progressChunks = new System.Collections.Concurrent.ConcurrentBag<string>();

        var ctx = new AgentExecutionContext(
            Provider: new AiProvider { Name = "test", Type = "openai", BaseUrl = "https://api.test", Model = "gpt-4" },
            InitialPrompt: "test",
            LoopPrompt: "",
            RunContext: new LoopRunContext(
                Guid.NewGuid(), Guid.NewGuid(), "Title", "desc", "/tmp", "main", new List<string>(), null),
            ExecutionCount: 1,
            Cancel: CancellationToken.None,
            ProgressCallback: (line) =>
            {
                progressChunks.Add(line);
                return Task.CompletedTask;
            });

        var result = await adapter.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
        result.Output.Should().Be("Hello world!");
        progressChunks.Should().HaveCountGreaterThanOrEqualTo(4);
    }

    private static TestHttpClientFactory BuildFactory(HttpMessageHandler handler)
    {
        return new TestHttpClientFactory(handler);
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public TestHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string? name = null)
        {
            return new HttpClient(_handler);
        }
    }

    private sealed class TestHttpHandler : HttpMessageHandler
    {
        private readonly string _responseContent;
        private readonly HttpStatusCode _statusCode;
        public List<string> RequestBodies { get; } = new();

        public TestHttpHandler(string responseContent = "ok", int statusCode = 200)
        {
            _responseContent = responseContent;
            _statusCode = (HttpStatusCode)statusCode;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync());
            if ((int)_statusCode != 200)
                return new HttpResponseMessage(_statusCode);

            var sseData = $"data: {JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = _responseContent } } } })}\n\ndata: [done]\n\n";
            var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(sseData));
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new StreamContent(ms);
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return response;
        }
    }

    private sealed class SseStreamHttpHandler : HttpMessageHandler
    {
        private readonly string[] _chunks;

        public SseStreamHttpHandler(string[] chunks)
        {
            _chunks = chunks;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var ms = new MemoryStream();
            var writer = new StreamWriter(ms);
            foreach (var chunk in _chunks)
            {
                var sseLine = $"data: {JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = chunk } } } })}\n\n";
                writer.Write(sseLine);
            }
            writer.Write("data: [done]\n\n");
            writer.Flush();
            ms.Position = 0;

            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new StreamContent(ms);
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        }
    }
}
