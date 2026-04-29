namespace ILD.Data.DTOs;

public record WebhookPayload(
    string EventType,
    string RepositoryId,
    string? PullRequestId,
    string? PullRequestUrl,
    string? Comment,
    string? MergeStatus
);
