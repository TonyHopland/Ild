namespace ILD.Core.DTOs;

public record AuthResult(
    bool Success,
    string? SessionToken,
    string? Username,
    string? ErrorMessage
);
