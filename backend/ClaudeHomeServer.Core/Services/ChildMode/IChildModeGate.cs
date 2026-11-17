using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.ChildMode;

/// <summary>
/// Детский гейт И1 для MCP и запуска хода (ADR-041, разрез §И1, KM-B3a): allow-list пар
/// (MCP-сервер, инструмент), fail-closed. Для обычного владельца гейт прозрачен (всё разрешено
/// ровно как раньше). Детский владелец получает только пары из <see cref="ChildModeAllowList"/>;
/// владелец с DenyAll — ничего.
/// </summary>
public interface IChildModeGate
{
    /// <summary>
    /// Текст отказа вызова инструмента или null, если вызов разрешён (владелец не ребёнок либо пара
    /// есть в allow-list). Отказ едет content-ошибкой, как у соседних гейтов транспорта (п.9 таблицы И1).
    /// </summary>
    string? Denied(string server, string tool, string? ownerId);

    /// <summary>
    /// Объявлять ли серверу место в конфиге хода (п.10): для не-ребёнка true, для ребёнка —
    /// только если у сервера есть хоть одна пара в allow-list. Решение зависит ТОЛЬКО от владельца
    /// (правило McpToolsetStabilityTests): профиль — свойство пользователя, а не хода.
    /// </summary>
    bool AllowsServer(string? ownerId, string server);

    /// <summary>
    /// Ребёнок ли владелец (профиль включён, в том числе нечитаемый — DenyAll): потребители
    /// внешних источников MCP (реестр владельца) по нему не объявляются вовсе.
    /// </summary>
    bool IsChild(string? ownerId);

    /// <summary>
    /// И1-бис, последний рубеж: сверка полей чата с профилем на запуске хода. Возвращает описание
    /// расхождения (ход не стартует, тихой подстановки нет) либо null, если владелец не ребёнок или
    /// всё сошлось. Профиль с DenyAll — всегда отказ.
    /// </summary>
    string? LaunchMismatch(string? ownerId, Session session);

    /// <summary>
    /// И3 (KM-B5d), точка запуска хода: вне окна, сверх дневного лимита пространства и сверх
    /// бюджета новый ход ребёнка не стартует. Возвращает детский текст отказа либо null, если ход
    /// идёт (владелец не ребёнок, состояние ok/warn, или rules_pending в чате онбординга правил —
    /// единственное исключение: окно и бюджет действуют и на него).
    /// Решение — за <c>ChildLimitsCalculator</c> (одна точка расчёта, KM-B5c); гейт только
    /// переводит состояние в текст и не держит своего состояния: каждый вызов считает заново.
    /// Исключение калькулятора наружу не отдаётся: оно становится этим текстом (fail-closed).
    /// </summary>
    string? LaunchLimitRefusal(string? ownerId, Session session, DateTimeOffset now);

    /// <summary>
    /// Снимок политики владельца (null — обычный пользователь). Нужен запуску хода, чтобы CLI читал
    /// признак ребёнка на каждом ходу; отдельной зависимостью политика в фабрику не едет.
    /// </summary>
    ChildPolicySnapshot? PolicyFor(string ownerId);
}

/// <summary>
/// Детский allow-list пар (сервер, инструмент). Единственный сервер — tutor (ADR-041, «Редакция 3»,
/// К3): материалы предмета для репетитора. Веб-поиск выключен решением Андрея, а
/// websearch/notes/memory/tasks/wsp/медиа/внешние серверы вне списка. Состав для
/// «Студии» волны 2 не обещан: какие инструменты медиа-серверов безопасны ребёнку, решается
/// отдельно (ADR-041, «Последствия»), гейт под список уже есть.
/// Расширение списка — осознанная правка сторожа ChildModeMcpGuardTests.
/// </summary>
public static class ChildModeAllowList
{
    public static readonly IReadOnlySet<(string Server, string Tool)> Pairs =
        new HashSet<(string, string)>
        {
            ("tutor", "search_materials"),
            ("tutor", "list_materials"),
            ("tutor", "read_material"),
        };

    public static bool AllowsServer(string server) => Pairs.Any(p => p.Server == server);

    public static bool Allows(string server, string tool) => Pairs.Contains((server, tool));
}

/// <summary>
/// Режим хода ребёнка. В ChildProfile поля режима нет, поэтому «режим из профиля» (разрез §И1-бис)
/// пока выражен единственным значением политики: Default — разрешения решает автоотказ (KM-B3b),
/// а не «принять правки» и не «обойти».
/// </summary>
public static class ChildModeLaunch
{
    public const ClaudeMode RequiredMode = ClaudeMode.Default;
}
