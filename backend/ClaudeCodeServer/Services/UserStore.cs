using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCodeServer.Models;
using Microsoft.AspNetCore.Identity;

namespace ClaudeCodeServer.Services;

public class UserStore
{
    private readonly string _filePath;
    private readonly PasswordHasher<User> _hasher = new();
    private List<User> _users = [];
    // DevPassword работает только когда задан в конфиге (обычно только в Development)
    private readonly string? _devPassword;

    public UserStore(IConfiguration config, ILogger<UserStore> logger)
    {
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        var dataDir = Path.GetDirectoryName(dataPath) ?? Path.Combine(AppContext.BaseDirectory, "data");
        _filePath = Path.Combine(dataDir, "users.json");
        _devPassword = config["Auth:DevPassword"];
        Load(logger);
    }

    private void Load(ILogger logger)
    {
        if (File.Exists(_filePath))
        {
            try
            {
                var json = File.ReadAllText(_filePath);
                var doc = JsonSerializer.Deserialize<UsersFile>(json, JsonOptions);
                _users = doc?.Users ?? [];
                return;
            }
            catch { /* повреждённый файл — пересоздадим */ }
        }

        var admin = new User { Username = "admin", Role = "admin" };
        SetPasswordInternal(admin, "admin");
        _users = [admin];
        Save();

        logger.LogWarning(
            "\n╔══════════════════════════════════════════════╗\n" +
            "║  СОЗДАН ПОЛЬЗОВАТЕЛЬ ПО УМОЛЧАНИЮ           ║\n" +
            "║  Логин: admin   Пароль: admin               ║\n" +
            "║  Смените пароль в data/users.json           ║\n" +
            "╚══════════════════════════════════════════════╝");
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(
            new UsersFile { Users = _users }, JsonOptions));
    }

    public User? FindByUsername(string username) =>
        _users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));

    public bool VerifyPassword(User user, string password)
    {
        if (_devPassword != null && password == _devPassword) return true;
        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        return result != PasswordVerificationResult.Failed;
    }

    /// <summary>
    /// Устанавливает пароль: bcrypt-хэш + NT-хэш для NTLM WebDAV.
    /// </summary>
    public void SetPassword(User user, string password)
    {
        SetPasswordInternal(user, password);
        Save();
    }

    /// <summary>
    /// Лениво вычисляет и сохраняет NT-хэш если его ещё нет.
    /// Вызывается при успешном логине, чтобы активировать NTLM WebDAV.
    /// </summary>
    public void EnsureNtHash(User user, string plainPassword)
    {
        if (user.NtHash is { Length: 16 }) return;
        user.NtHash = WebDav.NtlmHelper.ComputeNtHash(plainPassword);
        Save();
    }

    private void SetPasswordInternal(User user, string password)
    {
        user.PasswordHash = _hasher.HashPassword(user, password);
        user.NtHash       = WebDav.NtlmHelper.ComputeNtHash(password);
    }

    public User? GetById(string id) =>
        _users.FirstOrDefault(u => u.Id == id);

    public User? GetFirst() => _users.FirstOrDefault();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}

file sealed class UsersFile
{
    public int Version { get; set; } = 1;

    [JsonPropertyName("users")]
    public List<User> Users { get; set; } = [];
}
