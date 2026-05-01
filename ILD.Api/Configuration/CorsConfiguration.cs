namespace ILD.Api.Configuration;

public static class CorsConfiguration
{
    private static readonly string[] DefaultOrigins =
    {
        "http://localhost:3000",
        "http://localhost:5173",
    };

    public static string[] ParseAllowedOrigins(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return DefaultOrigins;
        var parts = csv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();
        return parts.Length == 0 ? DefaultOrigins : parts;
    }
}
