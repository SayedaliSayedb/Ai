namespace Aiagent.Models;

public sealed class User
{
    public required string PhoneNumber { get; init; }
    public required string PasswordHash { get; init; }
    public string? DisplayName { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool IsActive { get; set; } = true;
}

public sealed class UserSession
{
    public required string Token { get; init; }
    public required string PhoneNumber { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public required DateTimeOffset ExpiresAt { get; init; }
    public string? UserAgent { get; set; }
    public string? IpAddress { get; set; }
}