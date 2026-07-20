namespace ClaudeHomeServer.Models;

public class User
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Username { get; set; } = "";
    // Отображаемое имя («Григорий») — показывается вместо логина в приветствии и меню
    // аватара. null/пусто — показываем Username. Логин остаётся идентификатором входа.
    public string? DisplayName { get; set; }
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "user";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    // NT-хэш для NTLM WebDAV: MD4(UTF-16LE(password)); null — если пользователь ещё не логинился после обновления
    public byte[]? NtHash { get; set; }
    // Per-user override фич-флагов поверх дефолтов из FeatureFlagCatalog; null/отсутствует — все по дефолту
    public Dictionary<string, bool>? FeatureFlags { get; set; }
    // Per-user пороги индикатора заполнения контекста (проценты); null — дефолты фронта
    public ContextThresholds? ContextThresholds { get; set; }
    // IANA-таймзона пользователя (например "Europe/Moscow") — фронт присылает при старте;
    // нужна планировщику для перевода локальных сроков задач в UTC. null — считаем UTC
    public string? TimeZone { get; set; }
    // Среда исполнения процессов пользователя (claude, терминал, dev-серверы):
    // local — на машине сервера с полным доступом; container — в общей Docker-песочнице.
    // Меняется только пока у пользователя нет чатов (корни проектов и профили сред различаются)
    public string ExecutionEnvironment { get; set; } = ExecutionEnvironments.Local;
    // Аккаунт на локальном git-сервере Forgejo (провижнится лениво при первом git/init).
    // Токен — персональный PAT со scope write:repository; хранится открыто (решение
    // владельца, консистентно с остальным users.json), в git/логи не попадает
    public string? ForgejoUsername { get; set; }
    public string? ForgejoToken { get; set; }
    // Пароль веб-входа в Forgejo (открыто, как токен) — приватные репо анониму отдают 404
    public string? ForgejoPassword { get; set; }
}

// Значения User.ExecutionEnvironment
public static class ExecutionEnvironments
{
    public const string Local = "local";
    public const string Container = "container";
    public static bool IsValid(string? value) => value is Local or Container;
}

// Пороги подсветки индикатора контекста: warn — янтарь, danger — красный
public record ContextThresholds(int WarnPct, int DangerPct);
