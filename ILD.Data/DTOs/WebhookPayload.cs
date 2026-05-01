using System.ComponentModel.DataAnnotations;

namespace ILD.Data.DTOs;

public record WebhookPayload(
    [property: Required, StringLength(128, MinimumLength = 1)] string EventType,
    [property: Required, StringLength(256, MinimumLength = 1)] string RepositoryId,
    string? PullRequestId,
    string? PullRequestUrl,
    string? Comment,
    string? MergeStatus
);
