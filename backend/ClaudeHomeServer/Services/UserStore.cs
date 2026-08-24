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
            // Разовая миграция: выкидываем легаси-поле NtHash из файла. Само по себе
            // неизвестное поле десериализации не мешает, но пока файл не переписан,
            // NT-хэш продолжает лежать на диске и уезжать в облачный архив.
            if (HasLegacyNtHash())
            {
                Save();
                logger.LogInformation("users.json: удалено легаси-поле NtHash (NT-хэш пароля больше не хранится)");
            }
            MigrateIntroCompleted(logger);
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

    // Миграция IntroCompletedAt (знакомство с ассистентом). Load бежит на КАЖДОМ
    // старте, поэтому условие обязано быть точным: пользователь с дефолтом, но без признака
    // знакомства и без заготовки-ассистента считаются уже прошедшими знакомство — им
    // авто-ассистент не предлагают. Третье слагаемое (AssistantPersonaId == null) критично:
    // без него первый же рестарт проставил бы дату и погасил приглашение всем, кто отложил
    // знакомство, но уже получил заготовку. Идемпотентно: после первого проставления
    // IntroCompletedAt != null, и условие больше не срабатывает.
    private void MigrateIntroCompleted(ILogger logger)
    {
        var migrated = 0;
        foreach (var user in _users)
        {
            if (user.DefaultPersonaId is not null
                && user.IntroCompletedAt is null
                && user.AssistantPersonaId is null)
            {
                user.IntroCompletedAt = DateTime.UtcNow;
                migrated++;
            }
        }
        if (migrated > 0)
        {
            Save();
            logger.LogInformation(
                "users.json: {Count} пользователям проставлен IntroCompletedAt (дефолт уже есть — знакомство не предлагается)",
                migrated);
        }
    }

    // Есть ли в файле на диске легаси-поле NT-хэша (записи, сделанные до его отмены)
    private bool HasLegacyNtHash()
    {
        try
        {
            return File.Exists(_filePath)
                && File.ReadAllText(_filePath).Contains("\"NtHash\"", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false; // нечитаемый файл — не повод падать на старте
        }
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
    /// Устанавливает пароль (bcrypt-хэш) и бампает версию сессий.
    /// </summary>
    public void SetPassword(User user, string password)
    {
        lock (_lock)
        {
            SetPasswordInternal(user, password);
            Save();
        }
    }

    private void SetPasswordInternal(User user, string password)
    {
        user.PasswordHash = _hasher.HashPassword(user, password);
        // Смена пароля обесценивает все ранее выданные токены этого пользователя
        user.TokenVersion++;
    }

    /// <summary>
    /// Совпадает ли версия из токена с текущей версией сессий пользователя.
    /// Неизвестный пользователь — всегда false (удалённый аккаунт не должен ходить по токену).
    /// </summary>
    public bool IsTokenVersionCurrent(string userId, int version)
    {
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == userId);
            return user is not null && user.TokenVersion == version;
        }
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
            // Версию бампаем сами: админский сброс обязан выкидывать пользователя со всех устройств
            user.TokenVersion++;
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

    /// <summary>Сохраняет аккаунт Forgejo (логин + персональный токен) после провижна.</summary>
    public bool SetForgejoAccount(string id, string username, string token, string? password = null)
    {
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            if (user is null) return false;
            user.ForgejoUsername = username;
            user.ForgejoToken = token;
            if (password is not null) user.ForgejoPassword = password;
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
    /// Сохраняет глобальный (per-user) промпт AI-генерации сообщения коммита.
    /// Пустая строка → null (сброс к дефолту). Возвращает false если пользователь не найден.
    /// </summary>
    public bool SetGitCommitPrompt(string id, string? prompt)
    {
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            if (user is null) return false;

            user.GitCommitPrompt = string.IsNullOrWhiteSpace(prompt) ? null : prompt;
            Save();
            return true;
        }
    }

    // Per-user слоты тиров моделей: чтение и патч. null/пусто — наследовать глобальный слот инстанса.
    public (string? Strong, string? Medium, string? Weak) GetModelTiers(string id)
    {
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            return user is null
                ? (null, null, null)
                : (NormalizeTier(user.ModelTierStrong), NormalizeTier(user.ModelTierMedium), NormalizeTier(user.ModelTierWeak));
        }
    }

    /// <summary>
    /// Устанавливает per-user слоты тиров моделей. null = не трогать, "" = очистить к наследованию.
    /// Возвращает false если пользователь не найден.
    /// </summary>
    public bool SetModelTiers(string id, string? strong, string? medium, string? weak)
    {
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            if (user is null) return false;

            if (strong is not null) user.ModelTierStrong = NormalizeTier(strong);
            if (medium is not null) user.ModelTierMedium = NormalizeTier(medium);
            if (weak is not null) user.ModelTierWeak = NormalizeTier(weak);
            Save();
            return true;
        }
    }

    private static string? NormalizeTier(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Состав «Стены» пользователя (id чатов в порядке монет); пусто — не настроена.</summary>
    public IReadOnlyList<string> GetWallChatIds(string id)
    {
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            return user?.WallChatIds ?? [];
        }
    }

    /// <summary>
    /// Сохраняет состав «Стены». Список приходит уже отвалидированным (дедуп, только свои
    /// живые чаты, потолок) — стор лишь фиксирует его. Возвращает false, если пользователь не найден.
    /// </summary>
    public bool SetWallChatIds(string id, IReadOnlyList<string> chatIds)
    {
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            if (user is null) return false;
            // null и пустой список — одно и то же «стена не настроена»: без ?? [] каждый
            // PUT пустого состава поверх null переписывал бы users.json вхолостую
            if ((user.WallChatIds ?? []).SequenceEqual(chatIds)) return true; // без лишней записи файла

            user.WallChatIds = chatIds.Count > 0 ? [.. chatIds] : null;
            Save();
            return true;
        }
    }

    /// <summary>
    /// Личная дефолт-персона пользователя; null — сброс.
    /// Возвращает false если пользователь не найден.
    /// </summary>
    public bool SetDefaultPersona(string id, string? personaId)
    {
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            if (user is null) return false;
            user.DefaultPersonaId = string.IsNullOrWhiteSpace(personaId) ? null : personaId;
            Save();
            return true;
        }
    }

    /// <summary>
    /// Момент завершения знакомства с ассистентом (UTC); null — сброс/не пройдено.
    /// Возвращает false если пользователь не найден.
    /// </summary>
    public bool SetIntroCompleted(string id, DateTime? at)
    {
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            if (user is null) return false;
            user.IntroCompletedAt = at;
            Save();
            return true;
        }
    }

    /// <summary>
    /// Id заготовки-ассистента пользователя; null — сброс.
    /// Возвращает false если пользователь не найден.
    /// </summary>
    public bool SetAssistantPersona(string id, string? personaId)
    {
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            if (user is null) return false;
            user.AssistantPersonaId = string.IsNullOrWhiteSpace(personaId) ? null : personaId;
            Save();
            return true;
        }
    }

    /// <summary>
    /// Сессия незавершённого онбординга пользователя (null — онбординг завершён/сброшен).
    /// Возвращает false если пользователь не найден.
    /// </summary>
    public bool SetOnboardingSession(string id, string? sessionId)
    {
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            if (user is null) return false;
            user.OnboardingSessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId;
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

    /// <summary>
    /// Личный порог автоправила архивации чатов (флаг chat-auto-archive; null — сброс:
    /// правило для чатов вне проектов и наследуемый дефолт проектов не настроено).
    /// </summary>
    public bool SetArchiveAfterDays(string id, int? days)
    {
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            if (user is null) return false;

            user.ArchiveAfterDays = days;
            Save();
            return true;
        }
    }

    /// <summary>
    /// Момент первого прохода автоправила архивации (кнопка «Применить сейчас»):
    /// до него фоновый тик владельца не архивирует ничего.
    /// </summary>
    public bool SetArchiveRuleFirstRunAt(string id, DateTime? atUtc)
    {
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            if (user is null) return false;

            user.ArchiveRuleFirstRunAt = atUtc;
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
