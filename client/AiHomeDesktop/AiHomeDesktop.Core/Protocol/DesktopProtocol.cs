namespace AiHomeDesktop.Core.Protocol;

/// <summary>
/// Зеркало серверного Protocol/DesktopProtocol.cs (ADR-008, «Протокол канала»).
///
/// Числа и строки здесь ДУБЛИРУЮТ сервер намеренно: клиент — отдельное решение вне
/// ClaudeHomeServer.slnx, общей сборки у них нет. Правило одно: расхождение чинится на
/// обеих сторонах сразу, а версия протокола объявляется в Hello — по ней сервер и видит,
/// что клиент говорит на другом языке.
/// </summary>
public static class DesktopProtocol
{
    /// <summary>Версия протокола, на которой говорит этот клиент. Уезжает в Hello.</summary>
    public const int Version = 1;

    /// <summary>Ack на команду: не успели за 2 с — сервер закончит вызов честной ошибкой.</summary>
    public static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Потолок тела результата (~8 МБ). Это лимит HTTP, а НЕ лимит кадра.</summary>
    public const int MaxResultBytes = 8 * 1024 * 1024;

    /// <summary>Потолок шагов в одном батче desktop_act.</summary>
    public const int MaxBatchSteps = 10;

    /// <summary>
    /// Сколько клиент держит запись вызова в локальном журнале. Минуты, не часы: журнал
    /// нужен ровно для двух вопросов реконнекта — «доехал ли результат» и «не тот ли это
    /// вызов, что уже исполнен».
    /// </summary>
    public static readonly TimeSpan JournalTtl = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Дедлайн исполнения ПОСЛЕ встречного go: screen 15 с, ui 20 с, act 30 с, open 30 с,
    /// run 120 с. Сервер присылает своё число в команде — это дефолт на случай, когда его нет.
    /// </summary>
    public static TimeSpan DeadlineFor(string kind) => kind switch
    {
        DesktopCallKinds.Screen => TimeSpan.FromSeconds(15),
        DesktopCallKinds.Ui => TimeSpan.FromSeconds(20),
        DesktopCallKinds.Act => TimeSpan.FromSeconds(30),
        DesktopCallKinds.Open => TimeSpan.FromSeconds(30),
        DesktopCallKinds.Run => TimeSpan.FromSeconds(120),
        _ => TimeSpan.FromSeconds(30)
    };

    /// <summary>Совместим ли сервер, объявивший такую версию, с этим клиентом.</summary>
    public static bool IsSupportedServerVersion(int version) => version == Version;
}

/// <summary>Виды вызовов канала. desktop_devices сюда не приезжает — его обслуживает сервер.</summary>
public static class DesktopCallKinds
{
    public const string Screen = "screen";
    public const string Ui = "ui";
    public const string Act = "act";
    public const string Open = "open";
    public const string Run = "run";

    public static readonly IReadOnlyList<string> All = [Screen, Ui, Act, Open, Run];

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind);

    /// <summary>
    /// Что умеет ЭТА версия клиента (вторая волна — ровно два инструмента). Остальные виды
    /// отвечают честным исходом, а не молчанием: состав tools/list от этого не зависит —
    /// он входит в сигнатуру запуска CLI и меняться по свойствам хода не имеет права.
    /// </summary>
    public static readonly IReadOnlyList<string> Supported = [Screen, Open];

    public static bool IsSupportedByClient(string? kind) => kind is not null && Supported.Contains(kind);
}

/// <summary>
/// Исходы вызова — зеркало серверного DesktopOutcomes. Устройству разрешено присылать не
/// любой из них: серверные (no_ack, device_offline и пр.) ставит сам бэкенд.
/// </summary>
public static class DesktopOutcomes
{
    public const string Ok = "ok";
    public const string SessionLocked = "session_locked";
    public const string SecureDesktop = "secure_desktop";
    public const string TargetElevated = "target_elevated";
    public const string InputBlocked = "input_blocked";
    public const string SelfTargetDenied = "self_target_denied";
    public const string WindowNotAvailable = "window_not_available";
    public const string WindowMinimized = "window_minimized";

    /// <summary>Чем кончилось — неизвестно. В тексте НЕТ подсказки «повтори».</summary>
    public const string Unknown = "unknown";

    public const string SnapshotStale = "snapshot_stale";
    public const string AppliedUnverified = "applied_unverified";
    public const string NoVisibleChange = "no_visible_change";
    public const string AwaitingConfirmation = "awaiting_confirmation";
    public const string Denied = "denied";
    public const string Cancelled = "cancelled";
    public const string DeadlineExceeded = "deadline_exceeded";

    /// <summary>Отказ протокола: клиент не понял вид вызова или его аргументы.</summary>
    public const string ProtocolError = "protocol_error";
}

/// <summary>
/// Человеческие тексты исходов, которые ставит сам клиент. Правило ADR: у неизвестного
/// исхода нет подсказки «повтори» — авто-ретраев в этой грани нет нигде, клик, ввод и
/// запуск не идемпотентны.
/// </summary>
public static class DesktopClientOutcomeText
{
    /// <summary>Вид вызова каналом поддержан, а этой версией клиента — нет.</summary>
    public static string NotSupported(string kind) =>
        $"Вызов «{kind}» эта версия клиента AI Home Desktop не исполняет: она умеет только " +
        "снимок экрана (desktop_screen) и открытие цели (desktop_open). Ни один шаг не применён.";

    public static string UnknownKind(string kind) =>
        $"Клиент не знает вида вызова «{kind}»; ни один шаг не применён.";

    /// <summary>Результат прошлого вызова не доехал, а сам вызов уже исполнен.</summary>
    public static string AlreadyExecuted =>
        "Этот вызов уже исполнялся на устройстве; повторно он не выполняется.";
}
