namespace ILD.Core.DTOs;

public record ApiKeyRecord(
    Guid Id,
    string ProviderName,
    string EncryptedKey,
    DateTime CreatedAt
);
