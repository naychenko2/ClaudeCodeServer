namespace ClaudeHomeServer.Models;

/// <summary>
/// Профиль детского режима пользователя (ADR-041 §1). null на User — обычный пользователь.
/// Включает только админ (PUT /api/users/{id}/child-profile); ограничения читаются по стору,
/// не по claim. Модель без логики: проверки и пороги живут в политике (IChildModePolicy)
/// и в точках записи. Поле добавочное: старые записи users.json читаются с null,
/// BackupSchema.Version не двигается.
/// </summary>
public class ChildProfile
{
    public bool Enabled { get; set; }
    // Опекун (связь «родитель → ребёнок» для кабинета В2 и уведомлений стоп-сигнала).
    // В волне 1 заполняет админ; доступ к переписке опекуну не даёт (ADR-041 §7).
    public List<string> GuardianUserIds { get; set; } = [];
    // Закреплённая персона-тьютор, владелец — сам ребёнок. Поля детского чата сверяются
    // с ней при запуске хода (И1-бис).
    public string TutorPersonaId { get; set; } = "";
    // Запись пула ClaudeSubscriptionPool с ApiKey и без OAuthToken (И2): ход ребёнка
    // никогда не идёт по потребительской подписке.
    public string ProviderKey { get; set; } = "";
    // Закреплённая модель «Уроков» (например "sonnet"); слоты тиров ребёнка не используются.
    public string Model { get; set; } = "";
    // Предметы ребёнка (срез §1): порядок списка = порядок плиток на главной.
    // Поле добавочное: старые записи users.json читаются с пустым списком,
    // BackupSchema.Version не двигается. Засев предметов — миграция (Т1b), не политика:
    // неявного «пусто = три по умолчанию» здесь нет.
    public List<ChildSubject> Subjects { get; set; } = [];
    // Лимиты по пространствам. Ключи — строки ("lessons", позже "studio"), не enum:
    // Студия волны 2 добавляется записью без миграции формата.
    public Dictionary<string, SpaceLimits> Spaces { get; set; } = new();
    // Бюджеты расхода владельца (USD). Проверяются в точке запуска хода через IOwnerSpendReader;
    // null — лимита нет, но без читателя бюджета ход ребёнка всё равно отказывается (fail-closed).
    public decimal? DailyBudgetUsd { get; set; }
    public decimal? MonthlyBudgetUsd { get; set; }
    // Порог предупреждения «осталось минут ≤ N» (по умолчанию 5).
    public int WarnMinutes { get; set; } = 5;
    // Итог онбординга правил: момент согласия (UTC) и сводка, которую подтвердил ребёнок.
    // Пишет их только POST /api/child/rules/agree; лимиты она не меняет никогда.
    // null — правила не согласованы, «Уроки» открывают только онбординг.
    public DateTime? RulesAgreedAt { get; set; }
    public string? RulesSummary { get; set; }
}

/// <summary>
/// Лимиты одного детского пространства (ключ ChildProfile.Spaces).
/// </summary>
public class SpaceLimits
{
    // Разрешённые окна дня. Пусто/нет поля — окон нет (не «запрещено всё» — это задаёт
    // политика fail-closed на нечитаемом профиле, а не здесь).
    public List<TimeWindow> Windows { get; set; } = [];
    // Дневной лимит присутствия в пространстве, минуты (считается heartbeat'ом, а не
    // длительностью ходов). null — лимита нет.
    public int? DailyMinutes { get; set; }
}

/// <summary>
/// Окно времени в локальной таймзоне ребёнка (User.TimeZone, иначе DefaultTimeZone инстанса).
/// Разбор и проверка — за Core-хелпером DailyTimeWindow (KM-B1c), здесь только запись.
/// </summary>
public class TimeWindow
{
    // Начало и конец "HH:mm". To &lt; From — окно через полночь.
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    // ISO-дни недели (1=Пн … 7=Вс), как у TaskRecurrence. null/пусто — каждый день.
    public List<int>? Weekdays { get; set; }
}

/// <summary>
/// Предмет ребёнка в профиле (срез §1): в записи лежат маршрут модели и ссылка на
/// персону-репетитора. Характер репетитора (имя, аватар, голос, промпт) живёт в обычной
/// персоне ребёнка, здесь — только ссылка и маршрут. Класс без логики: уникальность Id,
/// конкретность Model, ключи Color и непустые поля проверяют валидатор и политика (Т2a), не здесь.
/// </summary>
public class ChildSubject
{
    // Белый список ключей цвета плитки — не hex: цвет это ключ палитры токенов,
    // сопоставление ключ → пара C.* живёт на фронте (срез §2). Читает его валидатор (Т2a) и фронт.
    public static readonly IReadOnlyList<string> ColorKeys = ["info", "rose", "success", "warning", "accent"];

    // Slug [a-z0-9-]{1,32}, стабилен; ключ Session.ChildSubject. Уникален в профиле.
    public string Id { get; set; } = "";
    // Название плитки («Окружающий мир»), 1..40.
    public string Label { get; set; } = "";
    // 1..3 символа на плитке («Аа», «×÷»).
    public string Glyph { get; set; } = "";
    // Ключ цвета из ColorKeys.
    public string Color { get; set; } = "";
    // Персона-репетитор ребёнка (владелец — сам ребёнок); провижнер создаёт её при добавлении
    // предмета (Т2). У разных предметов может быть одна и та же персона.
    public string TutorPersonaId { get; set; } = "";
    // Маршрут: запись ClaudeSubscriptionPool с ApiKey или ключ LlmProviders (срез §3).
    public string ProviderKey { get; set; } = "";
    // Конкретный id модели; маркеры tier:*/preset:* отказывает валидатор (Т5).
    public string Model { get; set; } = "";
    // Согласие админа на не-Anthropic маршрут (срез §3): момент (UTC) и автор.
    // Пишет его сохранение предмета с acknowledgeRouteRisk; согласие сбрасывается при смене ProviderKey.
    public DateTime? RouteAcknowledgedAt { get; set; }
    public string? RouteAcknowledgedBy { get; set; }
    // Выключенный предмет скрыт с главной, его чаты не стартуют.
    public bool Enabled { get; set; } = true;
}

// Значения ключей ChildProfile.Spaces и Session.ChildSpace (ADR-041): строки, не enum —
// Студия волны 2 добавляется записью в профиль без миграции формата сторов.
public static class ChildSpaces
{
    public const string Lessons = "lessons";
    // Волна 2: закладываем ключ сейчас, чтобы формат не менять (ADR-041 §6).
    public const string Studio = "studio";
}
