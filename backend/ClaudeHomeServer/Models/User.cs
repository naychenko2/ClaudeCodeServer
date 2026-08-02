namespace ClaudeHomeServer.Models;

public class User
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Username { get; set; } = "";
    // Отображаемое имя («Григорий») — показывается вместо логина в приветствии и меню
    // аватара. null/пусто — показываем Username. Логин остаётся идентификатором входа.
    public string? DisplayName { get; set; }
    public string PasswordHash { get; set; } = "";
    // Версия сессий: растёт при КАЖДОЙ смене пароля (своей и админского сброса). Выданный
    // токен несёт её в claim tv, и токен со старой версией отвергается — так смена пароля
    // отзывает входы на других устройствах. У записей, созданных до появления поля, — 0.
    public int TokenVersion { get; set; }
    public string Role { get; set; } = "user";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    // NT-хэш пароля (легаси-поле NtHash) здесь больше не хранится: NTLM WebDAV делает
    // Negotiate/SSPI и хэш никто не читал, а users.json уезжает в облачный бэкап —
    // MD4(UTF-16LE(password)) ломается перебором и годится для pass-the-hash.
    // Остатки поля вычищает разовая миграция в UserStore.Load.
    // Per-user override фич-флагов поверх дефолтов из FeatureFlagCatalog; null/отсутствует — все по дефолту
    public Dictionary<string, bool>? FeatureFlags { get; set; }
    // Per-user слоты тиров моделей. null/пусто — наследовать глобальный слот инстанса.
    public string? ModelTierStrong { get; set; }
    public string? ModelTierMedium { get; set; }
    public string? ModelTierWeak { get; set; }
    // Per-user пороги индикатора заполнения контекста (проценты); null — дефолты фронта
    public ContextThresholds? ContextThresholds { get; set; }
    // Глобальный (per-user) промпт AI-генерации сообщения коммита; null/пусто — дефолт.
    // Проект может переопределить своим Project.CommitPromptOverride.
    public string? GitCommitPrompt { get; set; }
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
    // Состав «Стены» (фича wall): id чатов в порядке монет рельсы. null/пусто — стена
    // не настроена. Мёртвые id (чат удалён/протух) не каскадятся — фильтруются лениво
    // при чтении и вычищаются при следующем сохранении (MyWallController).
    public List<string>? WallChatIds { get; set; }
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
