using Aiagent.Models;

namespace Aiagent.Services.Auth;

public interface IAuthService
{
    Task<(bool Success, string? Token, string? Error)> LoginAsync(
        string phoneNumber, string password, string? ip, string? userAgent);

    Task<UserSession?> ValidateTokenAsync(string token);

    Task LogoutAsync(string token);

    Task<bool> RegisterAsync(string phoneNumber, string password, string? displayName);

    Task<User?> GetUserAsync(string phoneNumber);
}