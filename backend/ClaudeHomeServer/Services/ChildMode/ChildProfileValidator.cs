using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Knowledge;
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

    public static Reject? Validate(ChildProfileDto req, User user, IUserStore users, ClaudeSubscriptionPool pool,
        LlmProviderRegistry providers)
    {
        // Ход ребёнка идёт в песочнице (И2/И4): среда после появления чатов не меняется
        // (guard в UsersController.Update), поэтому локальному пользователю режим не включается.
        if (user.ExecutionEnvironment != ExecutionEnvironments.Container)
            return new("not_container", "Детский режим включается только пользователям со средой исполнения container.");

        // Согласие на риск маршрутов — одно на профиль ребёнка (ADR-041 ред. 4, решение 26):
        // трёхзначный acknowledgeRouteRisk в теле — true «поставить», отсутствие поля «не трогать»,
        // false «отзывать». Здесь, до записи, действительным считается живое согласие профиля, только
        // если тело его прямо не отзывает: решение о самой паре (момент и автор) — под замком стора
        // (UserStore.SetChildProfile), здесь только то, чем проверять маршруты этого тела.
        var consent = req.AcknowledgeRouteRisk switch
        {
            true => true,
            false => false,
            _ => user.ChildMode?.RouteRiskAcknowledgedAt is not null,
        };

        // Маршрут профиля — тот, что несёт чат правил (тот же ChildRoute.Evaluate, что на запуске).
        // Профиль без маршрутов («не настроен») допустим: пустые providerKey и model не резолвятся
        // ни в какую запись, а нечитаемый профиль уже закрыт политикой (ChildModePolicy.IsValid →
        // DenyAll, уроки и чат правил не стартуют). Отказывать при включении — значило бы запрещать
        // настройку по частям: админ не смог бы сохранить лимиты, ещё не выбрав поставщика.
        if (!string.IsNullOrWhiteSpace(req.ProviderKey) || !string.IsNullOrWhiteSpace(req.Model))
        {
            if (RouteReject(ChildRoute.Evaluate(req.ProviderKey, req.Model, consent, pool, providers),
                    "профиля", req.ProviderKey) is { } profileRoute)
                return profileRoute;
        }

        if (ValidateSpaces(req.Spaces) is { } spaceError) return spaceError;

        // Предметы (Т2a, разрез §2): проверяется только то, что есть в теле — PUT без subjects
        // (null) не перечёркивает список: живые предметы сохраняет стор (SetChildProfile).
        if (ValidateSubjects(req.Subjects) is { } subjectError) return subjectError;

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

    // Предметы (Т2a, разрез §2): id — тот же slug и уникальность, что у политики SubjectsValid
    // (ChildModePolicy.cs:46): валидатор пишет те же ключи в стор, и дубль сделал бы ребёнка
    // DenyAll. label 1..40, глиф 1..3, цвет — ключ из ChildSubject.ColorKeys (палитра живёт
    // на фронте, здесь — только ключ). Предмет — только плитка: маршрута и репетитора в нём нет
    // (Б17c, они в проекте-предмете), поэтому и согласия на маршрут предмета не бывает.
    private static Reject? ValidateSubjects(List<ChildSubjectDto>? subjects)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var subject in subjects ?? [])
        {
            var id = subject?.Id ?? "";
            // Тот же разбор, что у SubjectId в политике: ^…$ без Multiline, но $ пропускает
            // хвостовой \n — поэтому \z. Дубль — тот же отказ: два предмета с одним ключом
            // не различимы ни для чата (Session.ChildSubject), ни для провижна (Т2b).
            // Пустой элемент списка ([null] в теле) — тоже невалидный ключ.
            if (subject is null || !SubjectIdRegex.IsMatch(id) || !ids.Add(id))
                return new("subject_id_invalid", $"Ключ предмета «{id}» недопустим: строчные буквы, цифры и «-», длина 1..32, без повторов в списке.");
            if (string.IsNullOrWhiteSpace(subject.Label) || subject.Label.Length > 40)
                return new("subject_label_invalid", $"Название предмета «{id}» — от 1 до 40 символов.");
            if (string.IsNullOrWhiteSpace(subject.Glyph) || subject.Glyph.Length > 3)
                return new("subject_glyph_invalid", $"Глиф предмета «{id}» — от 1 до 3 символов.");
            if (!ChildSubject.ColorKeys.Contains(subject.Color ?? "", StringComparer.Ordinal))
                return new("subject_color_invalid", $"Цвет предмета «{id}» вне палитры: допустимы {string.Join(", ", ChildSubject.ColorKeys)}.");
        }
        return null;
    }

    // Отказ ChildRoute → код 400 ADR-041 ред. 4. Текст один для профиля и предмета: маршрут
    // ребёнка — любой маршрут реестра взрослых (решение 25), поэтому и «что не так» у них одно:
    // ключ не резолвится ни в запись пула с API-ключом, ни в включённый провайдер.
    private static Reject? RouteReject(ChildRouteDecision decision, string what, string? providerKey) =>
        decision.Refusal switch
        {
            null => null,
            ChildRouteRefusal.NoRoute => new("provider_not_api_key",
                $"Провайдер {what} должен быть записью пула с API-ключом или включённым провайдером моделей."),
            ChildRouteRefusal.ModelNotConcrete => new("model_not_concrete",
                $"Модель {what} должна быть конкретной: без tier:*, preset:*, default и claude."),
            ChildRouteRefusal.ModelUnpriced => new("model_unpriced",
                $"У облачной модели {what} не задана цена: без неё бюджет ребёнка не ограничивает расход."),
            // Модель не тянется планом подписки (Б1а, pool.SupportsModel): запись есть, но её тариф
            // эту модель не поддерживает — это не «нет такой записи» и не «нет согласия».
            ChildRouteRefusal.ModelNotOnPlan => new("model_not_on_plan",
                $"Модель {what} недоступна на плане этой подписки «{providerKey}»."),
            ChildRouteRefusal.RiskUnacknowledged => new("route_risk_unacknowledged",
                $"Маршрут {what} не Anthropic: нужно согласие на профиле ребёнка (acknowledgeRouteRisk)."),
            ChildRouteRefusal.ModelNotOfProvider => new("model_not_of_provider",
                $"Модель {what} не принадлежит поставщику «{providerKey}»."),
            _ => new("provider_not_api_key", $"Маршрут {what} не принят."),
        };

    // [GeneratedRegex] не нужен: проверка редкая (тот же приём, что у SubjectId в политике).
    private static readonly System.Text.RegularExpressions.Regex SubjectIdRegex = new(@"^[a-z0-9-]{1,32}\z");
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
    // Предметы (Т2a, разрез §2): отсутствие/null — «предметы не трогать» (живое слияние стора),
    // явный [] — очистить. Порядок списка = порядок плиток на главной (сохраняется как есть).
    List<ChildSubjectDto>? Subjects,
    decimal? DailyBudgetUsd,
    decimal? MonthlyBudgetUsd,
    int? WarnMinutes,
    bool? ResetRules,
    // Согласие админа на риск маршрутов (ADR-041 ред. 4, решение 26): ОДНО на профиль ребёнка,
    // покрывает все предметы и чат правил, сменой модели или поставщика не сбрасывается.
    // Из ВХОДА читается только флаг: true — поставить, поля нет — не трогать, false — отозвать.
    // Момент и автор — поля сервера: их пишет живое слияние стора под замком, из тела они не
    // читаются никогда (тот же приём, что с тьютором и привязкой знания). В ответе
    // acknowledgeRouteRisk = «согласие стоит», routeRiskAcknowledgedAt — момент.
    bool? AcknowledgeRouteRisk = null,
    // Поля только ответа (Б4a, ADR-041 ред. 4 решение 30): из тела не читаются никогда,
    // в ответе — всегда. Автор согласия (решение 26): окно показывает, кто его дал —
    // прежде автор оставался только в сторе.
    DateTimeOffset? RouteRiskAcknowledgedAt = null,
    string? RouteRiskAcknowledgedBy = null,
    // Готовность опекунов к механизму общих проектов (ADR-041 ред. 6 решение 60): кураторы
    // проектов-предметов — опекуны с флагом shared-projects. Окно прямо скажет, у кого включить
    // флаг; сервер флаг за человека не включает.
    List<ChildGuardianDto>? Guardians = null,
    // Рубильник инстанса SharedProjects:Enabled (SharedProjectsGate): без него флаги опекунов
    // ничего не открывают.
    bool? SharedProjectsInstanceEnabled = null);
// Предмет в wire-контракте: плитка (Б17c — без маршрута, репетитора, знаний и согласия:
// их несёт проект-предмет). KnowledgeMoved — только поле ответа: у предмета была привязана база
// знаний, материалы надо перенести в папку проекта-предмета; из тела не читается.
public record ChildSubjectDto(
    string? Id,
    string? Label,
    string? Glyph,
    string? Color,
    bool? Enabled,
    bool? KnowledgeMoved = null);
public record ChildSpaceLimitsDto(List<ChildTimeWindowDto>? Windows, int? DailyMinutes);
public record ChildTimeWindowDto(string? From, string? To, List<int>? Weekdays);
// Опекун профиля в ответе (Б4a): sharedProjectsFlag — пользовательский флаг shared-projects
// без учёта рубильника инстанса (SharedProjectsGate.FlagOn); порядок — как guardianUserIds.
public record ChildGuardianDto(string UserId, bool SharedProjectsFlag);
// Ответ 200 ручки №10: { profile, tutorPersonaId }. Profile — null только когда профиля нет
// (выключение пользователя, который им и не был); при включении — всегда целый.
public record ChildProfileResponse(ChildProfileDto? Profile, string TutorPersonaId);
