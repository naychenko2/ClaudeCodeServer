using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services.ChildMode;

/// <summary>
/// Проверки входа админ-ручки детского профиля (KM-B1e, ADR-041 «Контракт», ручка №10).
/// Чистая функция: принимает тело + живого пользователя стора + пул подписок, возвращает
/// отказ {code, message} или null. Коды — буквально из ADR: фронт переводит их в свой текст
/// (features/childMode/ChildProfileEditor). Порядок — по таблице ADR: среда, провайдер,
/// окна/пространства, опекуны.
/// Живёт отдельно от контроллера: та же валидация нужна ручке кабинета опекуна волны 2
/// (IGuardianAccess.UpdateProfileAsync, заметка «Детский режим — волна 2»).
/// </summary>
public static class ChildProfileValidator
{
    public sealed record Reject(string Code, string Message);

    // Известные ключи пространств (ChildSpaces, ADR-041 §6): волна 1 — «Уроки»; «studio»
    // заложен, чтобы формат не менять. Сторонний ключ — invalid_space: лимиты по нему не
    // прочитает ни один потребитель, а опечатка админа в профиле висела бы вечно.
    private static readonly HashSet<string> KnownSpaces = new(StringComparer.Ordinal)
    {
        ChildSpaces.Lessons,
        ChildSpaces.Studio,
    };

    public static Reject? Validate(ChildProfileDto req, User user, IUserStore users, ClaudeSubscriptionPool pool)
    {
        // Ход ребёнка идёт в песочнице (И2/И4): среда после появления чатов не меняется
        // (guard в UsersController.Update), поэтому локальному пользователю режим не включается.
        if (user.ExecutionEnvironment != ExecutionEnvironments.Container)
            return new("not_container", "Детский режим включается только пользователям со средой исполнения container.");

        // Ход ребёнка никогда не идёт по подписке (ADR-041 §4): ключ обязан резолвиться
        // в запись пула с непустым ApiKey. Пустой providerKey — тот же отказ: он ни в какую
        // запись не резолвится.
        if (pool.All.FirstOrDefault(s => s.Key == req.ProviderKey && !string.IsNullOrWhiteSpace(s.ApiKey)) is null)
            return new("provider_not_api_key", "Провайдер должен быть записью пула подписок с API-ключом.");

        if (ValidateSpaces(req.Spaces) is { } spaceError) return spaceError;

        // Опекун — реальный пользователь: уведомление стоп-сигнала и кабинет волны 2
        // должны указывать на живую учётку, а не в пустоту.
        foreach (var guardianId in req.GuardianUserIds ?? [])
        {
            if (users.GetById(guardianId) is null)
                return new("guardian_not_found", "Среди опекунов есть id несуществующего пользователя.");
        }

        return null;
    }

    // Пространства: ключ — из KnownSpaces (пустой/сторонний — invalid_space), окна — формат
    // «HH:mm» и ISO-дни 1..7 (invalid_window). Пустой список/отсутствие окон — законное
    // значение SpaceLimits («окон нет»), не отказ: отказывать вне окна — дело политики (И3).
    private static Reject? ValidateSpaces(Dictionary<string, ChildSpaceLimitsDto>? spaces)
    {
        foreach (var (key, limits) in spaces ?? [])
        {
            if (string.IsNullOrWhiteSpace(key) || !KnownSpaces.Contains(key))
                return new("invalid_space", $"Неизвестное пространство '{key}'. Сейчас допустимы только {string.Join(", ", KnownSpaces)}.");
            if (limits is null)
                return new("invalid_space", $"Пространство '{key}' задано без лимитов.");
            foreach (var window in limits.Windows ?? [])
            {
                if (window is null)
                    return new("invalid_window", "Окно не задано.");
                // Тот же разбор границы, что у DailyTimeWindow.TryParseBounds и ChildModePolicy:
                // «25:00» и мусор — не окно. To < From — переход через полночь — законное окно.
                if (!TimeOnly.TryParseExact(window.From, "HH:mm", out _) || !TimeOnly.TryParseExact(window.To, "HH:mm", out _))
                    return new("invalid_window", "Окно времени должно быть в формате ЧЧ:ММ.");
                if (window.Weekdays is { } days && days.Any(d => d is < 1 or > 7))
                    return new("invalid_window", "Дни недели задаются числами 1..7 (ISO).");
            }
        }
        return null;
    }
}

// Wire-контракт ручки №10 (ADR-041, «DTO»): имена полей — буквально из ADR, их же зеркалит
// фронт (frontend/src/features/childMode/types.ts). Живёт рядом с валидатором: это одна
// контрактная пара — тело и его проверки. resetRules — только во входе, в ответе его нет.
// Все необязательные поля — nullable: тело {enabled:false} — законный запрос (ADR-041),
// и авто-валидация MVC ([ApiController]) не имеет права отбивать его до контроллера:
// не-nullable reference-поле стало бы «обязательным». Отсутствующее/null поле разбирают
// валидатор и маппинг (пустой список/пустой словарь), а не тип записи.
public record ChildProfileDto(
    bool Enabled,
    List<string>? GuardianUserIds,
    string? ProviderKey,
    string? Model,
    Dictionary<string, ChildSpaceLimitsDto>? Spaces,
    decimal? DailyBudgetUsd,
    decimal? MonthlyBudgetUsd,
    int? WarnMinutes,
    bool? ResetRules);
public record ChildSpaceLimitsDto(List<ChildTimeWindowDto>? Windows, int? DailyMinutes);
public record ChildTimeWindowDto(string? From, string? To, List<int>? Weekdays);
// Ответ 200 ручки №10: { profile, tutorPersonaId }. Profile — null только когда профиля нет
// (выключение пользователя, который им и не был); при включении — всегда целый.
public record ChildProfileResponse(ChildProfileDto? Profile, string TutorPersonaId);
