namespace ILD.Data.DTOs;

public record RemotePrResult(
    string? Url,
    string? HtmlUrl,
    RemotePrStatus Status,
    string? Error
);

public enum RemotePrStatus
{
    Open,
    Closed,
    Merged
}

public record RemotePrComment(
    string Id,
    string Body,
    string Author,
    DateTime CreatedAt
);
