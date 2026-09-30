using Aiagent.Models;
using Aiagent.Services.Storage;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Aiagent.Services.Auth;

public class JsonAuthService : IAuthService
{
    private const string UsersFile = "auth/users.json";
    private const string SessionsFile = "auth/sessions.json";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

    private readonly ILogger<JsonAuthService> _logger;
    private readonly IStorageService _storage;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private ConcurrentDictionary<string, User> _users = new();
    private ConcurrentDictionary<string, UserSession> _sessions = new();
    private bool _loaded;

    public JsonAuthService(IStorageService storage, ILogger<JsonAuthService> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    private async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        await _lock.WaitAsync();
        try
        {
            if (_loaded) return;

            if (await _storage.ExistsAsync(UsersFile))
            {
                var json = await _storage.ReadTextAsync(UsersFile);
                var list = JsonSerializer.Deserialize<List<User>>(json) ?? new();
                _users = new ConcurrentDictionary<string, User>(
                    list.ToDictionary(u => u.PhoneNumber));
            }

            if (await _storage.ExistsAsync(SessionsFile))
            {
                var json = await _storage.ReadTextAsync(SessionsFile);
                var list = JsonSerializer.Deserialize<List<UserSession>>(json) ?? new();
                _sessions = new ConcurrentDictionary<string, UserSession>(
                    list
                        .Where(s => s.ExpiresAt > DateTimeOffset.UtcNow)
                        .ToDictionary(s => s.Token));
            }

            _loaded = true;
            _logger.LogInformation(
                "Auth data loaded: {Users} users, {Sessions} active sessions",
                _users.Count, _sessions.Count);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> RegisterAsync(string phone, string password, string? displayName)
    {
        await EnsureLoadedAsync();

        if (!IsValidPhone(phone))
            return false;

        if (_users.ContainsKey(phone))
            return false;

        var user = new User
        {
            PhoneNumber = phone,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            DisplayName = displayName
        };

        _users[phone] = user;
        await PersistUsersAsync();
        return true;
    }

    public async Task<(bool, string?, string?)> LoginAsync(
        string phone, string password, string? ip, string? userAgent)
    {
        await EnsureLoadedAsync();

        if (!_users.TryGetValue(phone, out var user))
            return (false, null, "شماره موبایل یا رمز عبور اشتباه است.");

        if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
            return (false, null, "شماره موبایل یا رمز عبور اشتباه است.");

        var token = GenerateSecureToken();
        var session = new UserSession
        {
            Token = token,
            PhoneNumber = phone,
            ExpiresAt = DateTimeOffset.UtcNow.Add(SessionLifetime),
            IpAddress = ip,
            UserAgent = userAgent
        };

        _sessions[token] = session;
        await PersistSessionsAsync();

        return (true, token, null);
    }

    public async Task<UserSession?> ValidateTokenAsync(string token)
    {
        await EnsureLoadedAsync();

        if (!_sessions.TryGetValue(token, out var session))
            return null;

        if (session.ExpiresAt < DateTimeOffset.UtcNow)
        {
            _sessions.TryRemove(token, out _);
            return null;
        }

        return session;
    }

    public async Task LogoutAsync(string token)
    {
        await EnsureLoadedAsync();
        _sessions.TryRemove(token, out _);
        await PersistSessionsAsync();
    }

    public async Task<User?> GetUserAsync(string phone)
    {
        await EnsureLoadedAsync();
        return _users.TryGetValue(phone, out var u) ? u : null;
    }

    // === helpers ===
    private static bool IsValidPhone(string phone)
        => System.Text.RegularExpressions.Regex.IsMatch(phone, @"^09\d{9}$");

    private static string GenerateSecureToken()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    private async Task PersistUsersAsync()
    {
        var json = JsonSerializer.Serialize(_users.Values.ToList(),
            new JsonSerializerOptions { WriteIndented = true });
        await _storage.WriteTextAsync(UsersFile, json);
    }

    private async Task PersistSessionsAsync()
    {
        var json = JsonSerializer.Serialize(_sessions.Values.ToList(),
            new JsonSerializerOptions { WriteIndented = true });
        await _storage.WriteTextAsync(SessionsFile, json);
    }
}