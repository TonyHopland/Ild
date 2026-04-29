namespace ILD.Data.DTOs;

public record ApiKeyRecord(
    Guid Id,
    string ProviderName,
    string EncryptedKey,
    DateTime CreatedAt
);
