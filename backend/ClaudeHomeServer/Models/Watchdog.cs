namespace ClaudeHomeServer.Models;

// Статус сторожа чата (план «chat-watchdogs»). Активен → ровно один терминал:
// Fired (условие выполнилось), TimedOut (потолок жизни), LaunchFailed (запуск невозможен),
// Cancelled (сняли вручную). Недоставка будильника статусом НЕ является: терминальный
// исход единственный и затирать его нельзя — факт недоставленного будильника живёт
// в WatchdogRecord.DeliveredAt = null (см. поле). Имя записи — как в плане (модель данных).
public enum WatchdogStatus
{
    Active,
    Fired,
    TimedOut,
    LaunchFailed,
    Cancelled,
}

// Потолки и лимиты сторожей — единственная точка правды для стора, сервиса и тулсета
// (шаг 1 плана): нарушать нельзя, дублировать — не заводить.
public static class WatchdogLimits
{
    // Активных сторожей на чат / на владельца
    public const int MaxPerChat = 5;
    public const int MaxPerOwner = 20;

    // Период МЕЖДУ запусками poll-команды, сек
    public const int MinIntervalSeconds = 30;
    public const int MaxIntervalSeconds = 600;
    public const int DefaultIntervalSeconds = 60;

    // Таймаут ОДНОГО запуска: min(60, интервал) — считается сервером, из модели не приходит
    public const int MaxPollTimeoutSeconds = 60;

    // Потолок жизни сторожа, мин
    public const int DefaultTimeoutMinutes = 240;
    public const int MaxTimeoutMinutes = 1440;

    // Подряд не состоявшихся запусков до терминала launch_failed
    public const int MaxConsecutiveLaunchFailures = 3;

    // Попыток доставки будильника (первая + ретраи с шагом интервала сторожа)
    public const int DeliveryAttempts = 3;

    public static int PollTimeoutFor(int intervalSeconds) =>
        Math.Min(MaxPollTimeoutSeconds, intervalSeconds);
}

// Серверный сторож чата (флаг chat-watchdogs): «дожидаюсь условия и бужу этот чат».
// Сервис бэкенда крутит цикл опроса, переживая ходы, рестарты и смерть процесса CLI —
// в отличие от Monitor/run_in_background харнесса, живущих внутри процесса claude.
// Исполнение poll-команды — через ILauncherFactory.ForOwner(OwnerId): среда владельца
// (local/песочница), WorkingDirectory резолвится живьём на каждый опрос (rootPath проекта
// по ProjectId; чат вне проектов — домашняя папка владельца).
// Хранение — data/watchdogs.json (WatchdogStore); в архив бэкапа попадает автоматически.
public class WatchdogRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string OwnerId { get; init; } = "";
    // Чат-владелец: его будит терминальное событие, его сторожа показывает watch_list
    public string SessionId { get; init; } = "";
    // Проект чата (для резолва WorkingDirectory); null — чат вне проектов
    public string? ProjectId { get; init; }
    // Короткое имя для будильника («⏰ Сторож «{Name}»…»)
    public string Name { get; set; } = "";
    // Команда опроса: exit 0 = дождались (терминал fired); exit != 0 = «ещё нет» (НЕ терминал)
    public string PollCommand { get; set; } = "";
    // Период МЕЖДУ запусками, сек (30..600)
    public int IntervalSeconds { get; set; } = WatchdogLimits.DefaultIntervalSeconds;
    // Таймаут ОДНОГО запуска: min(60, IntervalSeconds); kill по истечении = «ещё нет»
    public int PollTimeoutSeconds { get; set; } = WatchdogLimits.MaxPollTimeoutSeconds;
    // Потолок жизни сторожа, мин (терминал timed_out)
    public int TimeoutMinutes { get; set; } = WatchdogLimits.DefaultTimeoutMinutes;
    public WatchdogStatus Status { get; set; } = WatchdogStatus.Active;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    // Момент терминального перехода (fired/timed_out/launch_failed; у cancelled — null)
    public DateTime? FiredAt { get; set; }
    public DateTime? LastPollAt { get; set; }
    // Обрезанный вывод последнего опроса (для будильника и watch_list)
    public string LastOutput { get; set; } = "";
    // Подряд не СОСТОЯВШИХСЯ запусков (процесс не стартовал / каталог исчез / песочница
    // недоступна). exit != 0 сюда НЕ входит — это штатное «ещё нет». 3 подряд → launch_failed;
    // любой состоявшийся запуск обнуляет.
    public int ConsecutiveLaunchFailures { get; set; }
    // Число сделанных попыток доставки будильника
    public int DeliveryAttempts { get; set; }
    // Момент доставки будильника. null у активного (ещё не будил) и у терминального,
    // чей будильник НЕ доставился после всех ретраев, — это и есть признак недоставки
    // в watch_list. Отдельный статус undelivered не заводится: он затирал бы исход.
    public DateTime? DeliveredAt { get; set; }
}
