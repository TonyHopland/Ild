namespace ILD.Core.DTOs;

public record WebhookPayload(
    string EventType,
    string RepositoryId,
    string? PullRequestId,
    string? PullRequestUrl,
    string? Comment,
    string? MergeStatus
);
