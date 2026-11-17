using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Services.Time;

namespace ClaudeHomeServer.Services.ChildMode;

/// <summary>
/// Одна точка расчёта лимитов ребёнка (KM-B5c, ADR-041 И3): статус/heartbeat (KM-B5e),
/// создание чата (KM-B6a) и гейт запуска хода (KM-B5d) читают только её. Своего состояния
/// нет: каждый Evaluate строит снимок из политики, журнала присутствия и швы расхода —
/// правка профиля или выключение Spend действуют со следующего вызова.
///
/// Порядок причин строго по И3: rules_pending → budget_exhausted → out_of_window →
/// time_up → warn → ok; первая сработавшая определяет state. Отказ любого источника —
/// fail-closed состояние, а не предрассудительный допуск:
/// - снимок DenyAll (профиль не читается/невалиден) — типизированный отказ
///   <see cref="ChildPolicyUnavailableException"/> (503 policy_unavailable у ручек);
/// - швы Spend нет или она бросила — budget_exhausted («бюджет не проверить», ADR-041 решение 4);
/// - журнала не прочитать — time_up («времени не проверить»).
/// Не ребёнок (policy.For — null) — статуса нет: null, вызывающий идёт обычным путём
/// (детские ручки и гейт проверяют «ребёнок ли» самой политикой до вызова).
///
/// Таймзона (ADR-041, решение Андрея 3): User.TimeZone, иначе ChildMode:DefaultTimeZone
/// инстанса, не UTC. Время наружу — ISO 8601 с явным смещением этой таймзоны ("O").
/// </summary>
public sealed class ChildLimitsCalculator(
    IChildModePolicy policy,
    ChildUsageStore usage,
    IOwnerSpendReader? spend,
    IUserStore users,
    IConfiguration config,
    ILogger<ChildLimitsCalculator>? log = null)
{
    // Ключи секции "ChildMode" в appsettings.json.
    public const string ConfigTimeZoneKey = "ChildMode:DefaultTimeZone";
    public const string ConfigMaxAttachmentBytesKey = "ChildMode:MaxAttachmentBytes";

    // Фолбэк, когда ни User.TimeZone, ни конфигурация не разрешились: та же зона, что в
    // привезённом appsettings (решение Андрея 3 — не UTC).
    public const string DefaultTimeZoneFallback = "Europe/Moscow";

    // ADR-041 «Контракт»: maxAttachmentBytes по умолчанию 10 МБ.
    public const long DefaultMaxAttachmentBytes = 10 * 1024 * 1024;

    // Allow-list типов вложений — константа, не конфигурация (И1: можно только то, что
    // явно перечислено; состав меняется правкой кода через ревью, а не значением в файле).
    public static readonly IReadOnlyList<string> AttachmentTypesAllowList =
        ["image/png", "image/jpeg", "image/webp", "application/pdf", "text/plain"];

    /// <summary>
    /// Статус ребёнка в пространстве `space` на момент `now`. null — владелец не ребёнок
    /// (политики нет): вызывающий идёт обычным путём. DenyAll — <see cref="ChildPolicyUnavailableException"/>.
    ///
    /// `rulesExempt` — единственное исключение И3 в точке запуска хода (KM-B5d): чат онбординга
    /// правил (Session.OnboardingKind == OnboardingKinds.ChildRules) идёт и до согласия, поэтому
    /// rules_pending для него не вычисляется; окно, дневной лимит и бюджет действуют и на него.
    /// Значение решает вызывающий по свойству чата, не по состоянию: статус ручки и heartbeat
    /// вызывают без него — оболочка ребёнка rules_pending видит всегда, как и раньше.
    /// </summary>
    public ChildStatus? Evaluate(string ownerId, string space, DateTimeOffset now, bool rulesExempt = false)
    {
        var snapshot = policy.For(ownerId);
        if (snapshot is null) return null;

        // Профиль есть, но не читается или невалиден: ребёнок остаётся ребёнком, но
        // запрещено всё — это отказ, а не состояние (503 у ручек, отказ запуска у гейта).
        if (snapshot.DenyAll || snapshot.Profile is null)
            throw new ChildPolicyUnavailableException($"Детский режим: профиль владельца не читается (owner {ownerId})");

        var profile = snapshot.Profile;
        var tz = ResolveTimeZone(ownerId);

        // Окна профиля — записи; разбор и сравнение — за Core-хелпером DailyTimeWindow (KM-B1c).
        // Пространства нет в профиле: окон и лимита нет — для ручки это сначала 403 space_disabled,
        // для калькулятора — out_of_window («входить некуда», как и пустые окна у хелпера).
        List<DailyTimeWindow?> windows = [];
        // Окна в ответе статуса (KM-B5e2): записи профиля как есть, в формате профиля — тем же
        // wire-типом, что ручка №10: оболочка показывает расписание так, как его задал взрослый,
        // а не подставленные из minutesLeft цифры. Пусто — [].
        List<ChildTimeWindowDto> statusWindows = [];
        if (profile.Spaces.TryGetValue(space, out var limits))
            foreach (var w in limits.Windows)
                if (w is not null)
                {
                    windows.Add(new DailyTimeWindow(w.From, w.To, w.Weekdays));
                    statusWindows.Add(new ChildTimeWindowDto(w.From, w.To, w.Weekdays));
                }

        var inWindow = DailyTimeWindow.Contains(now, windows, tz);
        // CurrentEnd вне окон сам отдаёт null — контрактное «null — окон нет или сейчас вне окна».
        var windowEndsAt = DailyTimeWindow.CurrentEnd(now, windows, tz);
        var nextWindowAt = DailyTimeWindow.NextStart(now, windows, tz);

        // Присутствие сегодня — по журналу (KM-B5b): время в пространстве считается heartbeat'ом,
        // а не длительностью ходов. Итог дня читается и без лимита (KM-B5e2): оболочке нужен итог,
        // а не только остаток. Журнала не прочитать — времени не проверить → time_up (fail-closed)
        // только когда лимит есть: без лимита состояния по времени нет, итог дня — null
        // («не прочитать»), а не 0 («прочитали ноль»).
        // Остаток — целые минуты (R-KM-F3): журнал хранит целые минуты округлением вниз,
        // текущая неполная минута в него не входит — остаток никогда не дробный, оболочка
        // ребёнка показывает целое число минут без своего счёта.
        int? minutesToday = null;
        var timeUncheckable = false;
        try
        {
            minutesToday = usage.MinutesToday(ownerId, space, now, tz);
        }
        catch (Exception e)
        {
            log?.LogWarning(e, "Детский режим: журнал присутствия не читается (owner {Owner}, space {Space})", ownerId, space);
            timeUncheckable = limits?.DailyMinutes is int;
        }

        int? minutesLeft = null;
        if (limits?.DailyMinutes is int dailyLimit && !timeUncheckable)
            minutesLeft = Math.Max(0, dailyLimit - minutesToday!.Value);

        // Бюджет — через Core-шов (ADR-041 решение 4): читателя нет или чтение сорвалось —
        // «бюджет не проверить»: ход ребёнка отказывается, задан бюджет в профиле или нет.
        var budgetUncheckable = spend is null;
        if (!budgetUncheckable)
        {
            try
            {
                budgetUncheckable =
                    (profile.DailyBudgetUsd is { } dailyBudget && spend!.TodayUsd(ownerId, tz) >= dailyBudget) ||
                    (profile.MonthlyBudgetUsd is { } monthlyBudget && spend!.MonthUsd(ownerId, tz) >= monthlyBudget);
            }
            catch (Exception e)
            {
                log?.LogWarning(e, "Детский режим: расход владельца не читается (owner {Owner})", ownerId);
                budgetUncheckable = true;
            }
        }

        var warnMinutes = profile.WarnMinutes; // порог из профиля, по умолчанию 5 (ChildProfile)

        var state =
            // rulesExempt — чат онбординга правил: отсутствие согласия его не останавливает
            // (исключение И3, KM-B5d), остальные причины ниже действуют и на него.
            profile.RulesAgreedAt is null && !rulesExempt ? ChildState.RulesPending :
            budgetUncheckable ? ChildState.BudgetExhausted :
            !inWindow ? ChildState.OutOfWindow :
            timeUncheckable || minutesLeft == 0 ? ChildState.TimeUp :
            minutesLeft is { } left && left <= warnMinutes ? ChildState.Warn :
            ChildState.Ok;

        return new ChildStatus(
            state,
            space,
            minutesLeft,
            windowEndsAt?.ToString("O"),
            // Контракт ADR-041: nextWindowAt — только при out_of_window.
            state == ChildState.OutOfWindow ? nextWindowAt?.ToString("O") : null,
            warnMinutes,
            MaxAttachmentBytes(),
            AttachmentTypesAllowList,
            profile.RulesAgreedAt?.ToString("O"),
            // Добавочные поля статуса (KM-B5e2): лимит и окна — из профиля как есть, итог дня —
            // из журнала. Существующие поля не меняются: старый клиент их игнорирует.
            limits?.DailyMinutes,
            statusWindows,
            minutesToday);
    }

    // Таймзона (решение Андрея 3): User.TimeZone → ChildMode:DefaultTimeZone → фолбэк инстанса.
    // Пользовательское значение неразборное или стор бросил — не отказ, переход к следующему
    // кандидату: окна продолжат считаться по таймзоне инстанса, а не по UTC.
    // Публичный: ручка heartbeat (KM-B5e) пишет минуту журнала той же таймзоной, которой этот
    // калькулятор читает «минут сегодня» (MinutesToday берёт местный день переданной зоны) —
    // вторая реализации резолвера здесь не будет: расхождение двух дат сломало бы остаток минут.
    public TimeZoneInfo ResolveTimeZone(string ownerId)
    {
        string? userZone = null;
        try { userZone = users.GetById(ownerId)?.TimeZone; }
        catch (Exception e) { log?.LogWarning(e, "Детский режим: таймзона пользователя не читается (owner {Owner})", ownerId); }

        foreach (var id in new[] { userZone, config[ConfigTimeZoneKey], DefaultTimeZoneFallback })
            if (!string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz))
                return tz;

        log?.LogWarning("Детский режим: таймзона не разрешилась (config: {Config}), время считается по UTC", config[ConfigTimeZoneKey]);
        return TimeZoneInfo.Utc;
    }

    private long MaxAttachmentBytes() =>
        long.TryParse(config[ConfigMaxAttachmentBytesKey], out var bytes) && bytes > 0
            ? bytes
            : DefaultMaxAttachmentBytes;
}

/// <summary>
/// Состояние ребёнка по И3 (ADR-041 «Контракт»). Значения наружу — строки snake_case:
/// контракт общий с фронтом (frontend/src/features/childMode/types.ts) и дословный.
/// JsonStringEnumConverter snake_case дать не может: он игнорирует EnumMember и по CamelCase
/// policy отдаёт "outOfWindow" (красным поймано тестом). Значение отдаёт свой конвертер
/// ChildStateJsonConverter: атрибут на свойстве DTO (атрибут на типе этим рантаймом
/// перекрывается конвертером из опций — тоже проверено тестом), наружу — строка ToWire().
/// </summary>
[JsonConverter(typeof(ChildStateJsonConverter))]
public enum ChildState
{
    Ok,
    Warn,
    TimeUp,
    OutOfWindow,
    RulesPending,
    BudgetExhausted,
}

/// <summary>Дословная строка состояния из контракта ADR-041 в обе стороны.</summary>
public sealed class ChildStateJsonConverter : JsonConverter<ChildState>
{
    public override ChildState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? reader.GetString() switch
            {
                "ok" => ChildState.Ok,
                "warn" => ChildState.Warn,
                "time_up" => ChildState.TimeUp,
                "out_of_window" => ChildState.OutOfWindow,
                "rules_pending" => ChildState.RulesPending,
                "budget_exhausted" => ChildState.BudgetExhausted,
                var other => throw new JsonException($"Неизвестное состояние ребёнка: {other}"),
            }
            : throw new JsonException("Состояние ребёнка ожидается строкой");

    public override void Write(Utf8JsonWriter writer, ChildState value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ToWire(value));

    /// <summary>
    /// Строка состояния вне JSON-потока тоже: 409 {code:&lt;state&gt;} при создании чата (KM-B6a)
    /// и другие точки, где состояние идёт значением, а не полем DTO.
    /// </summary>
    public static string ToWire(ChildState value) => value switch
    {
        ChildState.Ok => "ok",
        ChildState.Warn => "warn",
        ChildState.TimeUp => "time_up",
        ChildState.OutOfWindow => "out_of_window",
        ChildState.RulesPending => "rules_pending",
        ChildState.BudgetExhausted => "budget_exhausted",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Неизвестное состояние ребёнка"),
    };
}

/// <summary>
/// Статус ребёнка в одном пространстве (DTO ручки, ADR-041 «Контракт», поле 9).
/// MinutesLeft — остаток дневного лимита (null — лимита нет); WindowEndsAt — конец текущего
/// окна (null — окон нет или сейчас вне окна); NextWindowAt — ближайшее окно, только при
/// OutOfWindow; время — ISO 8601 с явным смещением таймзоны ребёнка.
/// DailyMinutes/Windows (KM-B5e2) — дневный лимит и окна профиля как есть (null / пусто —
/// не заданы), формат окон — тот же, что у ручки №10; MinutesToday — минуты, засчитанные
/// сегодня по журналу (null — журнал не читается). Три последних поля — добавочные:
/// существующие не менялись, старый клиент их игнорирует.
/// </summary>
public sealed record ChildStatus(
    [property: JsonConverter(typeof(ChildStateJsonConverter))] ChildState State,
    string Space,
    int? MinutesLeft,
    string? WindowEndsAt,
    string? NextWindowAt,
    int WarnMinutes,
    long MaxAttachmentBytes,
    IReadOnlyList<string> AttachmentTypes,
    string? RulesAgreedAt,
    int? DailyMinutes,
    IReadOnlyList<ChildTimeWindowDto> Windows,
    int? MinutesToday);

/// <summary>
/// Типизированный отказ «политика не читается» (fail-closed): детский профиль есть, но
/// нечитаем или невалиден. Детские ручки отдают его 503 {code:"policy_unavailable"},
/// гейт запуска — отказ хода; обычному (взрослому) пути отказ не отдаётся: не-ребёнок —
/// null от Evaluate, не это исключение.
/// </summary>
public sealed class ChildPolicyUnavailableException(string message) : Exception(message);
