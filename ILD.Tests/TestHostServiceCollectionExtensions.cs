using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ILD.Tests;

internal static class TestHostServiceCollectionExtensions
{
    public static void RemoveHostedService<TService>(this IServiceCollection services)
        where TService : class, IHostedService
    {
        var descriptors = services
            .Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(TService))
            .ToList();

        foreach (var descriptor in descriptors)
        {
            services.Remove(descriptor);
        }
    }

    public static void ReplaceSingleton<TService>(this IServiceCollection services, TService implementation)
        where TService : class
    {
        services.RemoveAll<TService>();
        services.AddSingleton(implementation);
    }

    public static void GuardExternalServices(this IServiceCollection services)
    {
        services.ReplaceSingleton<IProcessRunner>(new ThrowingProcessRunner());
        services.ReplaceSingleton<IAIProviderService>(new ThrowingAiProviderService());
        services.ReplaceSingleton<IWorktreePreviewService>(new ThrowingWorktreePreviewService());
        services.ReplaceSingleton<IRemoteProvider>(new ThrowingRemoteProvider());
        services.ReplaceSingleton<IAgentAdapterRegistry>(new ThrowingAgentAdapterRegistry());
    }

    private static InvalidOperationException Unexpected(string serviceName)
        => new($"Test host attempted to use {serviceName}. Override it explicitly in the test factory.");

    private sealed class ThrowingProcessRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> args,
            string? workingDirectory = null,
            CancellationToken ct = default,
            IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => throw Unexpected(nameof(IProcessRunner));
    }

    private sealed class ThrowingAiProviderService : IAIProviderService
    {
        public Task<string> CompleteAsync(string prompt, string? providerId = null, CancellationToken cancellationToken = default)
            => throw Unexpected(nameof(IAIProviderService));

        public Task<string> RenderPromptAsync(string template, LoopRunContext context)
            => throw Unexpected(nameof(IAIProviderService));

        public Task<bool> ValidatePromptTemplateAsync(string template)
            => throw Unexpected(nameof(IAIProviderService));

        public Task<IEnumerable<string>> GetAvailableProvidersAsync()
            => throw Unexpected(nameof(IAIProviderService));

        public Task<IEnumerable<string>> GetAvailableToolsAsync()
            => throw Unexpected(nameof(IAIProviderService));

        public Task<ToolExecutionResult> ExecuteToolAsync(string toolName, string arguments, string worktreePath)
            => throw Unexpected(nameof(IAIProviderService));
    }

    private sealed class ThrowingWorktreePreviewService : IWorktreePreviewService
    {
        public Task<WorktreePreviewResponse> GetStatusAsync(string worktreePath, CancellationToken cancellationToken = default)
            => throw Unexpected(nameof(IWorktreePreviewService));

        public Task<WorktreePreviewResponse> StartAsync(string worktreePath, WorktreePreviewStartOptions? options = null, CancellationToken cancellationToken = default)
            => throw Unexpected(nameof(IWorktreePreviewService));

        public Task<WorktreePreviewResponse> StopAsync(string worktreePath, CancellationToken cancellationToken = default)
            => throw Unexpected(nameof(IWorktreePreviewService));

        public Task<WorktreePreviewResponse> StartServiceAsync(string worktreePath, string serviceName, WorktreePreviewStartOptions? options = null, CancellationToken cancellationToken = default)
            => throw Unexpected(nameof(IWorktreePreviewService));

        public Task<WorktreePreviewResponse> StopServiceAsync(string worktreePath, string serviceName, CancellationToken cancellationToken = default)
            => throw Unexpected(nameof(IWorktreePreviewService));

        public Task<string?> GetServiceConfigAsync(string worktreePath, string serviceName, string? profileName = null, CancellationToken cancellationToken = default)
            => throw Unexpected(nameof(IWorktreePreviewService));

        public Task UpdateServiceConfigAsync(string worktreePath, string serviceName, string serviceConfigJson, string? profileName = null, CancellationToken cancellationToken = default)
            => throw Unexpected(nameof(IWorktreePreviewService));

        public Task<string?> GetServiceLogAsync(string worktreePath, string serviceName, int maxBytes = 64 * 1024, CancellationToken cancellationToken = default)
            => throw Unexpected(nameof(IWorktreePreviewService));

        public Task<WorktreeInstallResult> InstallAsync(string worktreePath, string? profileName = null, CancellationToken cancellationToken = default)
            => throw Unexpected(nameof(IWorktreePreviewService));

        public Task<WorktreePreviewValidationResult> ValidateConfigAsync(string worktreePath, string? profileName = null, CancellationToken cancellationToken = default)
            => throw Unexpected(nameof(IWorktreePreviewService));

        public bool IsPreviewRunning(string worktreePath)
            => false;
    }

    private sealed class ThrowingRemoteProvider : IRemoteProvider
    {
        public Task<RemotePrResult> CreatePullRequestAsync(string repoUrl, string sourceBranch, string targetBranch, string title, string body)
            => throw Unexpected(nameof(IRemoteProvider));

        public Task<bool> MergePullRequestAsync(string repoUrl, string prNumber)
            => throw Unexpected(nameof(IRemoteProvider));

        public Task<bool> EnablePullRequestAutoMergeAsync(string repoUrl, string prNumber)
            => throw Unexpected(nameof(IRemoteProvider));

        public Task<IEnumerable<RemotePrComment>> GetPullRequestCommentsAsync(string repoUrl, string prNumber)
            => throw Unexpected(nameof(IRemoteProvider));

        public Task RegisterWebhookAsync(string repoUrl, string callbackUrl)
            => throw Unexpected(nameof(IRemoteProvider));

        public Task UnregisterWebhookAsync(string repoUrl, string callbackUrl)
            => throw Unexpected(nameof(IRemoteProvider));

        public Task<RemotePrStatus> GetPullRequestStatusAsync(string repoUrl, string prNumber)
            => throw Unexpected(nameof(IRemoteProvider));

        public Task<RemotePrSnapshot?> GetPullRequestSnapshotAsync(string repoUrl, string prNumber)
            => throw Unexpected(nameof(IRemoteProvider));

        public Task<bool> DeleteBranchAsync(string repoUrl, string branchName)
            => throw Unexpected(nameof(IRemoteProvider));

        public Task<bool> CreatePullRequestCommentAsync(string repoUrl, string prNumber, string body)
            => throw Unexpected(nameof(IRemoteProvider));
    }

    private sealed class ThrowingAgentAdapterRegistry : IAgentAdapterRegistry
    {
        public Func<IAgentAdapter> ResolveForProvider(AiProvider provider)
            => throw Unexpected(nameof(IAgentAdapterRegistry));

        public string[] GetAllSupportedProviderTypes()
            => throw Unexpected(nameof(IAgentAdapterRegistry));
    }
}