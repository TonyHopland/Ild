namespace ILD.Data.DTOs;

public record AuthResult(
    bool Success,
    string? SessionToken,
    string? Username,
    string? ErrorMessage
);
