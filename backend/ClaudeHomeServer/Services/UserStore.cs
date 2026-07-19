using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using Microsoft.AspNetCore.Identity;

namespace ClaudeHomeServer.Services;

public class UserStore
{
    private readonly string _filePath;
    private readonly PasswordHasher<User> _hasher = new();
    private List<User> _users = [];
    // DevPassword работает только когда задан в конфиге (обычно только в Development)
    private readonly string? _devPassword;
    // UserStore — Singleton, шарится между конкурентными HTTP-запросами. Все чтения/мутации
    // _users и запись файла идут под этим локом, иначе возможны IOException на File.WriteAllText
    // из двух потоков и "Collection was modified" в JsonSerializer. Лок реентерабельный, поэтому
    // мутирующие методы спокойно вызывают Save() уже из-под взятого лока.
    private readonly object _lock = new();

    public UserStore(IConfiguration config, IHostEnvironment env, ILogger<UserStore> logger)
    {
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        var dataDir = Path.GetDirectoryName(dataPath) ?? Path.Combine(AppContext.BaseDirectory, "data");
        _filePath = Path.Combine(dataDir, "users.json");

        // DevPassword — мастер-пароль для всех аккаунтов; допустим ТОЛЬКО в Development.
        // В проде игнорируем, даже если задан в конфиге (иначе один пароль открывает всё).
        var devPassword = config["Auth:DevPassword"];
        if (!string.IsNullOrEmpty(devPassword))
        {
            if (env.IsDevelopment())
                _devPassword = devPassword;
            else
                logger.LogWarning("Auth:DevPassword задан вне среды Development — ПРОИГНОРИРОВАН из соображений безопасности.");
        }

        Load(logger); // конструктор однопоточен — отдельный лок не нужен
    }

    private void Load(ILogger logger)
    {
        // Повреждённый файл JsonFileStore сохранит в .bak и вернёт null — тогда создаём дефолтного пользователя
        var doc = JsonFileStore.Load<UsersFile>(_filePath, JsonOptions, logger);
        if (doc is not null)
        {
            _users = doc.Users ?? [];
            return;
        }

        // Случайный пароль вместо предсказуемого admin/admin: печатается в лог ОДИН раз.
        // Предсказуемый дефолт = открытый вход при первом старте на любом стенде.
        var generatedPassword = Convert.ToBase64String(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(12))
            .Replace("+", "").Replace("/", "").Replace("=", "");
        var admin = new User { Username = "admin", Role = "admin" };
        SetPasswordInternal(admin, generatedPassword);
        _users = [admin];
        Save();

        logger.LogWarning(
            "\n╔══════════════════════════════════════════════╗\n" +
            "║  СОЗДАН ПОЛЬЗОВАТЕЛЬ ПО УМОЛЧАНИЮ           ║\n" +
            "║  Логин: admin                               ║\n" +
            "║  Пароль: {Password}\n" +
            "║  Пароль показан ОДИН раз — смените после входа ║\n" +
            "╚══════════════════════════════════════════════╝", generatedPassword);
    }

    private void Save()
    {
        lock (_lock)
        {
            JsonFileStore.Save(_filePath, new UsersFile { Users = _users }, JsonOptions);
        }
    }

    public User? FindByUsername(string username)
    {
        lock (_lock)
            return _users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
    }

    // Без лока намеренно: метод не трогает _users (user приходит снаружи), а bcrypt-проверка
    // дорогая по дизайну — держать на ней общий лок значило бы сериализовать все логины.
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
        lock (_lock)
        {
            SetPasswordInternal(user, password);
            Save();
        }
    }

    /// <summary>
    /// Лениво вычисляет и сохраняет NT-хэш если его ещё нет.
    /// Вызывается при успешном логине, чтобы активировать NTLM WebDAV.
    /// </summary>
    public void EnsureNtHash(User user, string plainPassword)
    {
        lock (_lock)
        {
            if (user.NtHash is { Length: 16 }) return;
            user.NtHash = WebDav.NtlmHelper.ComputeNtHash(plainPassword);
            Save();
        }
    }

    private void SetPasswordInternal(User user, string password)
    {
        user.PasswordHash = _hasher.HashPassword(user, password);
        user.NtHash       = WebDav.NtlmHelper.ComputeNtHash(password);
    }

    public User? GetById(string id)
    {
        lock (_lock)
            return _users.FirstOrDefault(u => u.Id == id);
    }

    public User? GetFirst()
    {
        lock (_lock)
            return _users.FirstOrDefault();
    }

    // Возвращаем снимок: вызывающий итерирует его вне лока, поэтому отдаём копию,
    // а не view на живой _users (иначе конкурентная мутация → "Collection was modified").
    public IReadOnlyList<User> GetAll()
    {
        lock (_lock)
            return _users.ToList();
    }

    public User Add(string username, string password, string role,
        string executionEnvironment = ExecutionEnvironments.Local)
    {
        lock (_lock)
        {
            if (_users.Any(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Пользователь '{username}' уже существует");

            var user = new User { Username = username, Role = role, ExecutionEnvironment = executionEnvironment };
            SetPasswordInternal(user, password);
            _users.Add(user);
            Save();
            return user;
        }
    }

    public bool Update(string id, string? username, string? role, string? executionEnvironment = null)
    {
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            if (user is null) return false;

            if (username is not null && !string.Equals(username, user.Username, StringComparison.OrdinalIgnoreCase))
            {
                if (_users.Any(u => u.Id != id && string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"Пользователь '{username}' уже существует");
                user.Username = username;
            }

            if (role is not null && role != user.Role)
            {
                // Понижение роли admin → user: проверяем что останется хотя бы один admin
                if (user.Role == "admin" && role == "user" && !HasOtherAdmin(id))
                    throw new InvalidOperationException("Нельзя понизить роль единственного администратора");
                user.Role = role;
            }

            // Guard «нет чатов» — на вызывающей стороне (UsersController): стору сессии не видны
            if (executionEnvironment is not null)
                user.ExecutionEnvironment = executionEnvironment;

            Save();
            return true;
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            if (user is null) return false;

            if (user.Role == "admin" && !HasOtherAdmin(id))
                throw new InvalidOperationException("Нельзя удалить единственного администратора");

            _users.Remove(user);
            Save();
            return true;
        }
    }

    public bool ResetPassword(string id, string newPassword)
    {
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            if (user is null) return false;

            user.PasswordHash = _hasher.HashPassword(user, newPassword);
            // NtHash пересчитается лениво при следующем логине
            user.NtHash = null;
            Save();
            return true;
        }
    }

    public bool ChangePassword(string id, string currentPassword, string newPassword)
    {
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            if (user is null) return false;
            if (!VerifyPassword(user, currentPassword)) return false;

            SetPasswordInternal(user, newPassword);
            Save();
            return true;
        }
    }

    /// <summary>
    /// Устанавливает per-user override фич-флага. Возвращает false если пользователь не найден.
    /// </summary>
    public bool SetFeatureFlag(string id, string key, bool enabled)
    {
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            if (user is null) return false;

            (user.FeatureFlags ??= new())[key] = enabled;
            Save();
            return true;
        }
    }

    /// <summary>
    /// Сохраняет IANA-таймзону пользователя (для планировщика напоминаний).
    /// Возвращает false если пользователь не найден.
    /// </summary>
    public bool SetTimeZone(string id, string timeZone)
    {
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            if (user is null) return false;
            if (user.TimeZone == timeZone) return true; // без лишней записи файла

            user.TimeZone = timeZone;
            Save();
            return true;
        }
    }

    /// <summary>
    /// Устанавливает per-user пороги индикатора контекста (null — сброс к дефолтам).
    /// Возвращает false если пользователь не найден.
    /// </summary>
    public bool SetContextThresholds(string id, ContextThresholds? thresholds)
    {
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            if (user is null) return false;

            user.ContextThresholds = thresholds;
            Save();
            return true;
        }
    }

    // Вызывается только из Update/Delete, уже из-под взятого лока — отдельная синхронизация не нужна.
    private bool HasOtherAdmin(string excludeId) =>
        _users.Any(u => u.Id != excludeId && u.Role == "admin");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}

file sealed class UsersFile
{
    public int Version { get; set; } = 1;

    [JsonPropertyName("users")]
    public List<User> Users { get; set; } = [];
}
